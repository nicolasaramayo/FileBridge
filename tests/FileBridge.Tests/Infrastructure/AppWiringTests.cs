using FileBridge.Application.Services;
using FileBridge.Domain.Entities;
using FileBridge.Domain.Interfaces;
using FileBridge.Infrastructure.Network;
using FileBridge.Infrastructure.Services;
using FileBridge.Tests.Infrastructure.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FileBridge.Tests.Infrastructure;

/// <summary>
/// Builds the same DI container the app builds (AddFileBridgeInfrastructureServices) for two
/// "devices" in one process, starts their hosted services the way the MAUI app now does, and sends
/// a file from one to the other through ITransferBackgroundService — the path the UI uses.
/// </summary>
[TestClass]
public class AppWiringTests
{
    private static ServiceProvider BuildDevice(string name, string root)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFileBridgeInfrastructureServices(Path.Combine(root, name + ".db"), o =>
        {
            o.DeviceName = name;
            o.Port = 0; // random port: both "devices" live on this machine
            o.ReceivedFilesDirectory = Path.Combine(root, name + "-received");
        });
        return services.BuildServiceProvider();
    }

    [TestMethod]
    public void Container_ResolvesEverythingTheAppNeeds_AsSingleInstances()
    {
        var root = Path.Combine(Path.GetTempPath(), "filebridge-di-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var sp = BuildDevice("A", root);

        Assert.IsNotNull(sp.GetRequiredService<ConnectionManager>());
        Assert.IsNotNull(sp.GetRequiredService<ITransferService>());
        Assert.IsNotNull(sp.GetRequiredService<IFileTransport>());
        Assert.AreSame(sp.GetRequiredService<FileService>(), sp.GetRequiredService<IFileService>());
        Assert.AreSame(sp.GetRequiredService<TransferOrchestrator>(), sp.GetRequiredService<ITransferService>());

        var background = sp.GetRequiredService<ITransferBackgroundService>();
        var hosted = sp.GetServices<IHostedService>().ToList();
        Assert.AreEqual(2, hosted.Count);
        Assert.IsTrue(hosted.Any(h => ReferenceEquals(h, background)), "The hosted service and the one the UI uses must be the same instance");
    }

    [TestMethod]
    public async Task TwoDevices_SendThroughTheBackgroundService_FileArrives()
    {
        var root = Path.Combine(Path.GetTempPath(), "filebridge-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var phone = BuildDevice("PHONE", root);
        using var pc = BuildDevice("PC", root);

        var started = new List<IHostedService>();
        try
        {
            foreach (var hosted in phone.GetServices<IHostedService>().Concat(pc.GetServices<IHostedService>()))
            {
                await hosted.StartAsync(CancellationToken.None);
                started.Add(hosted);
            }

            var pcBackground = pc.GetRequiredService<ITransferBackgroundService>();
            Assert.IsTrue(await TestFiles.WaitUntilAsync(() => pcBackground.ListeningPort > 0), "PC server did not start");

            FileReceivedEventArgs? received = null;
            var pcReceiver = pc.GetRequiredService<FileReceiver>();
            pcReceiver.ApproveIncoming = LoopbackReceiver.AcceptAll; // the app asks the user here
            pcReceiver.FileReceived += (_, e) => received = e;

            var phoneBackground = phone.GetRequiredService<ITransferBackgroundService>();
            var completed = new TaskCompletionSource<string>();
            phoneBackground.OnTransferCompleted += (_, e) => completed.TrySetResult(e.JobId);
            phoneBackground.OnTransferFailed += (_, e) => completed.TrySetException(new Exception(e.ErrorMessage));

            var source = TestFiles.CreateRandomFile(1_500_000, "vacaciones.jpg");
            var target = new Device { Id = Guid.NewGuid(), Name = "PC", IpAddress = "127.0.0.1", Port = pcBackground.ListeningPort };
            var jobId = await phoneBackground.QueueUploadAsync(
                new FileItem { Id = Guid.NewGuid(), FileName = "vacaciones.jpg", FilePath = source, FileSize = 1_500_000 }, target);

            var finished = await Task.WhenAny(completed.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            Assert.AreSame(completed.Task, finished, "Transfer did not finish in 20 s");
            Assert.AreEqual(jobId, await completed.Task);
            Assert.IsTrue(await TestFiles.WaitUntilAsync(() => received != null));
            Assert.AreEqual(TestFiles.Sha256(source), received!.Sha256);
            Assert.AreEqual(Path.Combine(root, "PC-received", "vacaciones.jpg"), received.FullPath);
        }
        finally
        {
            foreach (var hosted in started)
                await hosted.StopAsync(CancellationToken.None);
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }
}
