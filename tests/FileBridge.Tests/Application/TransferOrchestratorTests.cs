using FileBridge.Application.Services;
using FileBridge.Domain.Entities;
using FileBridge.Domain.Enums;
using FileBridge.Domain.Interfaces;

namespace FileBridge.Tests.Application;

[TestClass]
public class TransferOrchestratorTests
{
    private sealed class FakeTransport : IFileTransport
    {
        public Func<Device, string, IProgress<long>?, Task<FileTransferResult>> Behavior { get; set; } =
            (_, _, _) => Task.FromResult(new FileTransferResult(0, ""));
        public int Calls { get; private set; }

        public Task<FileTransferResult> SendFileAsync(Device target, string localFilePath, Guid transferId,
            IProgress<long>? progress = null, CancellationToken ct = default)
        {
            Calls++;
            return Behavior(target, localFilePath, progress);
        }
    }

    private static string TempFile(int size)
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    [TestMethod]
    public async Task SendFileAsync_UsesTheTransport_AndReportsProgress()
    {
        var device = new Device { Id = Guid.NewGuid(), Name = "PC", IpAddress = "192.168.1.10", Port = 45678 };
        var registry = new DeviceRegistry();
        registry.AddOrUpdate(device);
        var transport = new FakeTransport
        {
            Behavior = (_, _, progress) =>
            {
                progress?.Report(500);
                progress?.Report(1000);
                return Task.FromResult(new FileTransferResult(1000, "abc"));
            }
        };
        var orchestrator = new TransferOrchestrator(transport, registry);
        var progressEvents = new List<long>();
        orchestrator.OnProgressChanged += (_, e) => progressEvents.Add(e.BytesTransferred);
        var job = new TransferJob { Id = Guid.NewGuid(), DeviceId = device.Id, LocalFilePath = TempFile(1000), TotalBytes = 1000 };

        var ok = await orchestrator.SendFileAsync(job);

        Assert.IsTrue(ok);
        Assert.AreEqual(1, transport.Calls);
        Assert.AreEqual(TransferStatus.Completed, job.Status);
        Assert.AreEqual(1000L, progressEvents.Last());
    }

    [TestMethod]
    public async Task SendFileAsync_UnknownDevice_FailsWithoutCallingTheTransport()
    {
        var transport = new FakeTransport();
        var orchestrator = new TransferOrchestrator(transport, new DeviceRegistry());
        var job = new TransferJob { Id = Guid.NewGuid(), DeviceId = Guid.NewGuid(), LocalFilePath = TempFile(10) };

        var ok = await orchestrator.SendFileAsync(job);

        Assert.IsFalse(ok);
        Assert.AreEqual(TransferStatus.Failed, job.Status);
        Assert.AreEqual(0, transport.Calls);
    }

    [TestMethod]
    public async Task SendFileAsync_TransportError_IsNotReportedAsCompleted()
    {
        // The old implementation read the file locally and always ended in Completed.
        var device = new Device { Id = Guid.NewGuid(), IpAddress = "10.0.0.2", Port = 1 };
        var registry = new DeviceRegistry();
        registry.AddOrUpdate(device);
        var transport = new FakeTransport { Behavior = (_, _, _) => throw new IOException("connection reset") };
        var orchestrator = new TransferOrchestrator(transport, registry);
        var job = new TransferJob { Id = Guid.NewGuid(), DeviceId = device.Id, LocalFilePath = TempFile(10) };

        var ok = await orchestrator.SendFileAsync(job);

        Assert.IsFalse(ok);
        Assert.AreEqual(TransferStatus.Failed, job.Status);
        Assert.AreEqual("connection reset", job.ErrorMessage);
    }

    [TestMethod]
    public async Task ReceiveFileAsync_IsExplicitlyNotSupported_InsteadOfFakingSuccess()
    {
        var orchestrator = new TransferOrchestrator(new FakeTransport(), new DeviceRegistry());
        var job = new TransferJob { Id = Guid.NewGuid(), LocalFilePath = Path.GetTempFileName(), TotalBytes = 1000 };

        var ok = await orchestrator.ReceiveFileAsync(job);

        Assert.IsFalse(ok);
        Assert.AreEqual(TransferStatus.Failed, job.Status);
    }
}
