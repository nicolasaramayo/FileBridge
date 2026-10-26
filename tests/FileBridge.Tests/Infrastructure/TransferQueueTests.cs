using FileBridge.Application.Events;
using FileBridge.Domain.Entities;
using FileBridge.Domain.Interfaces;
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

    private ServiceProvider BuildSender(IFileTransport? transport = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFileBridgeInfrastructureServices(Path.Combine(_root, "sender.db"), o =>
        {
            o.DeviceName = "SENDER";
            o.Port = 0;
            o.ReceivedFilesDirectory = Path.Combine(_root, "sender-received");
        });
        if (transport != null)
            services.AddSingleton(transport); // last registration wins
        return services.BuildServiceProvider();
    }

    /// <summary>First call blocks until cancelled, then waits for <see cref="ReleaseFirstCall"/> before throwing (a slow unwind).</summary>
    private sealed class ScriptedTransport : IFileTransport
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource FirstCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<FileTransferResult> SendFileAsync(Device target, string localFilePath, Guid transferId,
            IProgress<long>? progress = null, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstCallStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    await ReleaseFirstCall.Task;
                    throw;
                }
            }
            return new FileTransferResult(new FileInfo(localFilePath).Length, "fake");
        }
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
    public async Task PausedWhileQueued_ThenCancelled_IsReportedOnce()
    {
        await using var receiver = await LoopbackReceiver.StartAsync();
        using var sp = BuildSender();
        var queue = sp.GetRequiredService<ITransferBackgroundService>();
        var cancelledEvents = 0;
        queue.OnTransferFailed += (_, _) => Interlocked.Increment(ref cancelledEvents);

        var jobId = await queue.QueueUploadAsync(FileFor(TestFiles.CreateRandomFile(1000)), receiver.AsDevice());
        await queue.PauseAsync(jobId);
        await queue.CancelAsync(jobId);        // reported here...
        var hosted = await StartQueueAsync(sp); // ...and the stale queue entry must not report it again
        try
        {
            await Task.Delay(500);
            Assert.AreEqual(1, Volatile.Read(ref cancelledEvents));
            Assert.AreEqual(0, receiver.Files().Length);
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task ResumeWhileThePausedAttemptIsStillUnwinding_SendsItAgain()
    {
        // Before: the orchestrator turned the new Pending status into Cancelled, so the job was dropped.
        var transport = new ScriptedTransport();
        using var sp = BuildSender(transport);
        var queue = sp.GetRequiredService<ITransferBackgroundService>();
        var completed = new TaskCompletionSource<TransferCompletedEvent>();
        var failures = 0;
        queue.OnTransferCompleted += (_, e) => completed.TrySetResult(e);
        queue.OnTransferFailed += (_, _) => Interlocked.Increment(ref failures);
        var target = new Device { Id = Guid.NewGuid(), Name = "PC", IpAddress = "192.0.2.1", Port = 45678 };
        var hosted = await StartQueueAsync(sp);
        try
        {
            var jobId = await queue.QueueUploadAsync(FileFor(TestFiles.CreateRandomFile(1000)), target);
            await transport.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await queue.PauseAsync(jobId);
            await queue.ResumeAsync(jobId);           // the paused attempt has not finished unwinding yet
            transport.ReleaseFirstCall.TrySetResult();

            var finished = await Task.WhenAny(completed.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.AreSame(completed.Task, finished, "The resumed job was never sent again");
            Assert.AreEqual(jobId, completed.Task.Result.JobId);
            Assert.AreEqual(2, transport.Calls);
            Assert.AreEqual(0, Volatile.Read(ref failures));
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
