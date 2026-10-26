using FileBridge.Application.Events;
using FileBridge.Domain.Entities;
using FileBridge.Infrastructure.Network;
using FileBridge.Infrastructure.Services;
using FileBridge.Tests.Infrastructure.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FileBridge.Tests.Infrastructure;

/// <summary>Queue semantics of TransferBackgroundService (pause, resume, cancel), with a real receiver.</summary>
[TestClass]
public class TransferQueueTests
{
    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "filebridge-queue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private ServiceProvider BuildSender()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFileBridgeInfrastructureServices(Path.Combine(_root, "sender.db"), o =>
        {
            o.DeviceName = "SENDER";
            o.Port = 0;
            o.ReceivedFilesDirectory = Path.Combine(_root, "sender-received");
        });
        return services.BuildServiceProvider();
    }

    private static async Task<IHostedService> StartQueueAsync(ServiceProvider sp)
    {
        var hosted = sp.GetRequiredService<TransferBackgroundService>();
        await hosted.StartAsync(CancellationToken.None);
        return hosted;
    }

    private static FileItem FileFor(string path) =>
        new() { Id = Guid.NewGuid(), FileName = Path.GetFileName(path), FilePath = path, FileSize = new FileInfo(path).Length };

    [TestMethod]
    public async Task CancelWhileQueued_IsReportedAsCancelled_AndNothingIsSent()
    {
        await using var receiver = await LoopbackReceiver.StartAsync();
        using var sp = BuildSender();
        var queue = sp.GetRequiredService<ITransferBackgroundService>();
        var failed = new TaskCompletionSource<TransferFailedEvent>();
        queue.OnTransferFailed += (_, e) => failed.TrySetResult(e);

        // Queued before the queue loop runs, cancelled while still waiting.
        var jobId = await queue.QueueUploadAsync(FileFor(TestFiles.CreateRandomFile(1000)), receiver.AsDevice());
        await queue.CancelAsync(jobId);
        var hosted = await StartQueueAsync(sp);
        try
        {
            var finished = await Task.WhenAny(failed.Task, Task.Delay(5000));
            Assert.AreSame(failed.Task, finished, "A cancelled job must get a final event");
            Assert.AreEqual(jobId, failed.Task.Result.JobId);
            Assert.AreEqual("Cancelled", failed.Task.Result.ErrorMessage);
            Assert.AreEqual(0, receiver.Files().Length);
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task PausedJob_WaitsUntilResumed_ThenCompletes()
    {
        await using var receiver = await LoopbackReceiver.StartAsync();
        using var sp = BuildSender();
        var queue = sp.GetRequiredService<ITransferBackgroundService>();
        var completed = new TaskCompletionSource<TransferCompletedEvent>();
        var anyFailure = false;
        queue.OnTransferCompleted += (_, e) => completed.TrySetResult(e);
        queue.OnTransferFailed += (_, _) => anyFailure = true;
        var source = TestFiles.CreateRandomFile(200_000, "later.bin");

        var jobId = await queue.QueueUploadAsync(FileFor(source), receiver.AsDevice());
        await queue.PauseAsync(jobId);
        var hosted = await StartQueueAsync(sp);
        try
        {
            await Task.Delay(500);
            Assert.IsFalse(completed.Task.IsCompleted, "A paused job must not be sent");
            Assert.AreEqual(0, receiver.Files().Length);

            await queue.ResumeAsync(jobId);

            var finished = await Task.WhenAny(completed.Task, Task.Delay(10_000));
            Assert.AreSame(completed.Task, finished, "The resumed job did not complete");
            Assert.AreEqual(jobId, completed.Task.Result.JobId);
            Assert.IsFalse(anyFailure);
            Assert.AreEqual(TestFiles.Sha256(source), TestFiles.Sha256(Path.Combine(receiver.Directory, "later.bin")));
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task ServerStart_RaisesStatusChanged_WithTheListeningPort()
    {
        using var sp = BuildSender();
        var queue = sp.GetRequiredService<ITransferBackgroundService>();
        var started = new TaskCompletionSource<int>();
        queue.StatusChanged += (_, _) => started.TrySetResult(queue.ListeningPort);

        var hosted = await StartQueueAsync(sp);
        try
        {
            var finished = await Task.WhenAny(started.Task, Task.Delay(5000));
            Assert.AreSame(started.Task, finished, "StatusChanged was not raised");
            Assert.IsTrue(started.Task.Result > 0);
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }
}
