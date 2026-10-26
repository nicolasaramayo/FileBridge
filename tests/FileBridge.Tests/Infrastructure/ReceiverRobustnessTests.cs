using FileBridge.Domain.Enums;
using FileBridge.Infrastructure.Network;
using FileBridge.Tests.Infrastructure.TestSupport;

namespace FileBridge.Tests.Infrastructure;

[TestClass]
public class ReceiverRobustnessTests
{
    [TestMethod]
    public void ParseDataPayload_RejectsAForgedLength_WithoutAllocatingIt()
    {
        var serializer = new ProtocolSerializer();
        var payload = serializer.BuildDataPayload(0, 0, new byte[] { 1, 2, 3, 4 });
        BitConverter.GetBytes(int.MaxValue).CopyTo(payload, 12); // DataLength field

        InvalidDataException? error = null;
        try { serializer.ParseDataPayload(payload); }
        catch (InvalidDataException ex) { error = ex; }

        Assert.IsNotNull(error, "A length that does not match the payload must be rejected");
    }

    [TestMethod]
    public async Task ForgedDataLength_OverTheWire_GetsBadRequest_AndTheTransferIsDropped()
    {
        await using var receiver = await LoopbackReceiver.StartAsync();
        using var raw = new RawProtocolClient();
        await raw.ConnectAsync(receiver.Port);

        await raw.SendAsync(MessageType.FileRequest, raw.Serializer.BuildFileRequestPayload(Guid.NewGuid(), "x.bin", 4, 0, false, 0));
        Assert.AreEqual(MessageType.Ack, (await raw.ReceiveAsync()).Type);

        var payload = raw.Serializer.BuildDataPayload(0, 0, new byte[] { 1, 2, 3, 4 });
        BitConverter.GetBytes(int.MaxValue - 7).CopyTo(payload, 12);
        await raw.SendAsync(MessageType.Data, payload);

        var reply = await raw.ReceiveAsync();
        Assert.AreEqual(MessageType.Error, reply.Type);
        Assert.AreEqual(ProtocolErrorCodes.BadRequest, raw.Serializer.ParseErrorPayload(reply.Payload).Code);
        Assert.IsTrue(await TestFiles.WaitUntilAsync(() => receiver.Files().Length == 0));
    }

    [TestMethod]
    public async Task ThrowingSubscribers_DoNotBreakATransferThatSucceeded()
    {
        await using var receiver = await LoopbackReceiver.StartAsync();
        var secondSubscriberCalled = false;
        receiver.Receiver.Progress += (_, _) => throw new InvalidOperationException("UI bug in a progress handler");
        receiver.Receiver.FileReceived += (_, _) => throw new InvalidOperationException("UI bug in a completion handler");
        receiver.Receiver.FileReceived += (_, _) => secondSubscriberCalled = true;
        var source = TestFiles.CreateRandomFile(500_000, "ok.bin");

        var result = await new TcpFileTransport().SendFileAsync(receiver.AsDevice(), source, Guid.NewGuid());

        Assert.AreEqual(TestFiles.Sha256(source), result.Sha256, "The sender must still get its ACK");
        Assert.IsTrue(File.Exists(Path.Combine(receiver.Directory, "ok.bin")));
        Assert.IsTrue(secondSubscriberCalled, "Other subscribers must still be called");
    }
}
