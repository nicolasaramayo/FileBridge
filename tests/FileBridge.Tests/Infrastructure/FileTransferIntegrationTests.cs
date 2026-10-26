using System.Net.Sockets;
using FileBridge.Application.Services;
using FileBridge.Domain.Entities;
using FileBridge.Domain.Enums;
using FileBridge.Infrastructure.Network;
using FileBridge.Tests.Infrastructure.TestSupport;

namespace FileBridge.Tests.Infrastructure;

/// <summary>
/// End-to-end tests over real TCP sockets on 127.0.0.1: the same classes the app uses on each device.
/// These are the tests that were missing — the previous suite only exercised in-memory pieces and mocks,
/// so it stayed green while no byte ever reached the other device.
/// </summary>
[TestClass]
public class FileTransferIntegrationTests
{
    [TestMethod]
    public async Task SendFile_OverRealTcp_ArrivesIntactWithSameSha256()
    {
        await using var receiver = await LoopbackReceiver.StartAsync();
        var source = TestFiles.CreateRandomFile(5 * 1024 * 1024 + 123, "video.mp4"); // not a multiple of the chunk size
        FileReceivedEventArgs? received = null;
        receiver.Receiver.FileReceived += (_, e) => received = e;

        var transport = new TcpFileTransport();
        var progress = new SyncProgress();
        var result = await transport.SendFileAsync(receiver.AsDevice(), source, Guid.NewGuid(), progress);

        var expectedSha = TestFiles.Sha256(source);
        var size = new FileInfo(source).Length;
        Assert.AreEqual(size, result.BytesSent);
        Assert.AreEqual(size, progress.Last, "Progress must reach 100%");
        Assert.AreEqual(expectedSha, result.Sha256);
        Assert.IsTrue(await TestFiles.WaitUntilAsync(() => received != null), "FileReceived was not raised");
        Assert.AreEqual("video.mp4", received!.FileName);
        Assert.AreEqual(expectedSha, TestFiles.Sha256(received.FullPath));
        Assert.AreEqual(1, receiver.Files().Length, "Only the final file must remain (no .part leftovers)");
    }

    [TestMethod]
    public async Task SendFile_EmptyFile_Arrives()
    {
        await using var receiver = await LoopbackReceiver.StartAsync();
        var source = TestFiles.CreateRandomFile(0, "empty.txt");

        var result = await new TcpFileTransport().SendFileAsync(receiver.AsDevice(), source, Guid.NewGuid());

        Assert.AreEqual(0L, result.BytesSent);
        Assert.IsTrue(await TestFiles.WaitUntilAsync(() => receiver.Files().Any(f => Path.GetFileName(f) == "empty.txt")));
        Assert.AreEqual(0L, new FileInfo(Path.Combine(receiver.Directory, "empty.txt")).Length);
    }

    [TestMethod]
    public async Task SendFile_SameNameTwice_DoesNotOverwrite()
    {
        await using var receiver = await LoopbackReceiver.StartAsync();
        var first = TestFiles.CreateRandomFile(1000, "notes.txt");
        var second = TestFiles.CreateRandomFile(2000, "notes.txt");
        var transport = new TcpFileTransport();

        await transport.SendFileAsync(receiver.AsDevice(), first, Guid.NewGuid());
        await transport.SendFileAsync(receiver.AsDevice(), second, Guid.NewGuid());

        var names = receiver.Files().Select(Path.GetFileName).OrderBy(n => n).ToArray();
        CollectionAssert.AreEqual(new[] { "notes (1).txt", "notes.txt" }, names);
    }

    [TestMethod]
    public async Task SendFile_ManySequentialTransfers_AllArrive()
    {
        await using var receiver = await LoopbackReceiver.StartAsync();
        var transport = new TcpFileTransport();

        for (var i = 0; i < 10; i++)
        {
            var source = TestFiles.CreateRandomFile(70_000 + i, $"file{i}.bin");
            var result = await transport.SendFileAsync(receiver.AsDevice(), source, Guid.NewGuid());
            Assert.AreEqual(TestFiles.Sha256(source), result.Sha256);
        }

        Assert.AreEqual(10, receiver.Files().Length);
    }

    [TestMethod]
    public async Task SendFile_TwoSendersAtTheSameTime_BothArrive()
    {
        await using var receiver = await LoopbackReceiver.StartAsync();
        var a = TestFiles.CreateRandomFile(3_000_000, "from-phone.bin");
        var b = TestFiles.CreateRandomFile(2_000_000, "from-laptop.bin");

        var results = await Task.WhenAll(
            new TcpFileTransport().SendFileAsync(receiver.AsDevice(), a, Guid.NewGuid()),
            new TcpFileTransport().SendFileAsync(receiver.AsDevice(), b, Guid.NewGuid()));

        Assert.AreEqual(TestFiles.Sha256(a), results[0].Sha256);
        Assert.AreEqual(TestFiles.Sha256(b), results[1].Sha256);
        Assert.AreEqual(TestFiles.Sha256(a), TestFiles.Sha256(Path.Combine(receiver.Directory, "from-phone.bin")));
        Assert.AreEqual(TestFiles.Sha256(b), TestFiles.Sha256(Path.Combine(receiver.Directory, "from-laptop.bin")));
    }

    [TestMethod]
    public async Task SendFile_WhenNobodyListens_Throws()
    {
        // Grab a free port and release it: nothing listens there.
        var probe = new TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var source = TestFiles.CreateRandomFile(10, "a.txt");
        var target = new Device { Id = Guid.NewGuid(), Name = "nobody", IpAddress = "127.0.0.1", Port = port };

        Exception? error = null;
        try { await new TcpFileTransport().SendFileAsync(target, source, Guid.NewGuid()); }
        catch (Exception ex) { error = ex; }

        Assert.IsNotNull(error, "Sending to a closed port must fail, never report success");
    }

    [TestMethod]
    public async Task Orchestrator_SendThroughRealTransport_CompletesOnlyWhenReceiverConfirms()
    {
        await using var receiver = await LoopbackReceiver.StartAsync();
        var target = receiver.AsDevice("PC");
        var registry = new DeviceRegistry();
        registry.AddOrUpdate(target);
        var orchestrator = new TransferOrchestrator(new TcpFileTransport(), registry);
        var source = TestFiles.CreateRandomFile(300_000, "photo.jpg");
        var job = new TransferJob { Id = Guid.NewGuid(), DeviceId = target.Id, LocalFilePath = source, Direction = TransferDirection.Send };

        var ok = await orchestrator.SendFileAsync(job);

        Assert.IsTrue(ok, job.ErrorMessage);
        Assert.AreEqual(TransferStatus.Completed, job.Status);
        Assert.AreEqual(300_000L, job.TransferredBytes);
        Assert.AreEqual(TestFiles.Sha256(source), TestFiles.Sha256(Path.Combine(receiver.Directory, "photo.jpg")));
    }

    [TestMethod]
    public async Task Orchestrator_ReceiverDown_MarksJobFailed()
    {
        var registry = new DeviceRegistry();
        var target = new Device { Id = Guid.NewGuid(), Name = "off", IpAddress = "127.0.0.1", Port = 1 };
        registry.AddOrUpdate(target);
        var orchestrator = new TransferOrchestrator(new TcpFileTransport(), registry);
        var job = new TransferJob { Id = Guid.NewGuid(), DeviceId = target.Id, LocalFilePath = TestFiles.CreateRandomFile(10) };

        var ok = await orchestrator.SendFileAsync(job);

        Assert.IsFalse(ok);
        Assert.AreEqual(TransferStatus.Failed, job.Status);
        Assert.IsFalse(string.IsNullOrEmpty(job.ErrorMessage));
    }

    // ----- receiver robustness (raw protocol) -----

    [TestMethod]
    public async Task Receiver_CorruptedChunk_IsRejected_AndNothingIsLeftOnDisk()
    {
        await using var receiver = await LoopbackReceiver.StartAsync();
        using var raw = new RawProtocolClient();
        await raw.ConnectAsync(receiver.Port);
        var id = Guid.NewGuid();

        await raw.SendAsync(MessageType.FileRequest, raw.Serializer.BuildFileRequestPayload(id, "x.bin", 4, 0, false, 0));
        Assert.AreEqual(MessageType.Ack, (await raw.ReceiveAsync()).Type);

        var payload = raw.Serializer.BuildDataPayload(0, 0, new byte[] { 1, 2, 3, 4 });
        payload[^1] ^= 0xFF; // break the chunk CRC
        await raw.SendAsync(MessageType.Data, payload);

        var reply = await raw.ReceiveAsync();
        Assert.AreEqual(MessageType.Error, reply.Type);
        Assert.AreEqual(ProtocolErrorCodes.IntegrityFailed, raw.Serializer.ParseErrorPayload(reply.Payload).Code);
        Assert.IsTrue(await TestFiles.WaitUntilAsync(() => receiver.Files().Length == 0), "Partial file must be deleted");
    }

    [TestMethod]
    public async Task Receiver_WrongSha256_IsRejected()
    {
        await using var receiver = await LoopbackReceiver.StartAsync();
        using var raw = new RawProtocolClient();
        await raw.ConnectAsync(receiver.Port);
        var id = Guid.NewGuid();
        var data = new byte[] { 10, 20, 30 };

        await raw.SendAsync(MessageType.FileRequest, raw.Serializer.BuildFileRequestPayload(id, "x.bin", data.Length, 0, false, 0));
        await raw.ReceiveAsync();
        await raw.SendAsync(MessageType.Data, raw.Serializer.BuildDataPayload(0, 0, data));
        await raw.SendAsync(MessageType.TransferComplete, raw.Serializer.BuildTransferCompletePayload(id, new byte[32]));

        var reply = await raw.ReceiveAsync();
        Assert.AreEqual(MessageType.Error, reply.Type);
        Assert.AreEqual(ProtocolErrorCodes.IntegrityFailed, raw.Serializer.ParseErrorPayload(reply.Payload).Code);
        Assert.AreEqual(0, receiver.Files().Length);
    }

    [TestMethod]
    public async Task Receiver_ConnectionDroppedMidTransfer_DeletesPartialFile()
    {
        await using var receiver = await LoopbackReceiver.StartAsync();
        string? failure = null;
        receiver.Receiver.ReceiveFailed += (_, e) => failure = e.Reason;
        using var raw = new RawProtocolClient();
        await raw.ConnectAsync(receiver.Port);

        await raw.SendAsync(MessageType.FileRequest, raw.Serializer.BuildFileRequestPayload(Guid.NewGuid(), "big.iso", 1_000_000, 0, false, 0));
        await raw.ReceiveAsync();
        await raw.SendAsync(MessageType.Data, raw.Serializer.BuildDataPayload(0, 0, new byte[1000]));
        Assert.IsTrue(await TestFiles.WaitUntilAsync(() => receiver.Files().Any(f => f.EndsWith(".part"))));

        await raw.CloseAsync();

        Assert.IsTrue(await TestFiles.WaitUntilAsync(() => failure != null && receiver.Files().Length == 0),
            "The .part file must be removed when the sender disappears");
    }

    [TestMethod]
    public async Task Receiver_FileNameWithDirectories_StaysInsideTheReceiveFolder()
    {
        await using var receiver = await LoopbackReceiver.StartAsync();
        using var raw = new RawProtocolClient();
        await raw.ConnectAsync(receiver.Port);
        var id = Guid.NewGuid();
        var data = new byte[] { 1 };

        await raw.SendAsync(MessageType.FileRequest, raw.Serializer.BuildFileRequestPayload(id, "../../evil.txt", 1, 0, false, 0));
        await raw.ReceiveAsync();
        await raw.SendAsync(MessageType.Data, raw.Serializer.BuildDataPayload(0, 0, data));
        await raw.SendAsync(MessageType.TransferComplete, raw.Serializer.BuildTransferCompletePayload(id, System.Security.Cryptography.SHA256.HashData(data)));
        Assert.AreEqual(MessageType.Ack, (await raw.ReceiveAsync()).Type);

        CollectionAssert.AreEqual(new[] { Path.Combine(receiver.Directory, "evil.txt") }, receiver.Files());
    }
}
