using System.Collections.Concurrent;
using FileBridge.Domain.Entities;
using FileBridge.Domain.Enums;
using FileBridge.Infrastructure;
using FileBridge.Infrastructure.Network;
using FileBridge.Tests.Infrastructure.TestSupport;

namespace FileBridge.Tests.Infrastructure;

/// <summary>
/// Regressions for the protocol bugs found on dev@f1415d8, reproduced over real sockets.
/// </summary>
[TestClass]
public class ProtocolRegressionTests
{
    [TestMethod]
    public async Task HandshakeReply_IsACompleteProtocolMessage()
    {
        // Before: the handler wrote [length][payload] without header/CRC → Deserialize() returned null.
        await using var receiver = await LoopbackReceiver.StartAsync("MY-PC");
        using var raw = new RawProtocolClient();
        await raw.ConnectAsync(receiver.Port);

        await raw.SendAsync(MessageType.Handshake, raw.Serializer.BuildHandshakePayload("phone", Array.Empty<byte>()));
        var reply = await raw.ReceiveAsync();

        Assert.AreEqual(MessageType.Handshake, reply.Type);
        Assert.AreEqual("MY-PC", raw.Serializer.ParseHandshakePayload(reply.Payload).DeviceName);
    }

    [TestMethod]
    public async Task ConnectionManager_MessagesFlowBothWays_OnAnOutboundConnection()
    {
        // Before: SendMessageAsync wrote to the outbound socket without length prefix (the server read
        // "01 04 00 00" as a 1025-byte frame and stalled), and replies to the outbound client were never read.
        await using var pc = await LoopbackReceiver.StartAsync("PC");
        var pcMessages = new ConcurrentQueue<ProtocolMessage>();
        pc.Connections.OnMessageReceived += (_, e) => pcMessages.Enqueue(e.Message);

        var phoneOptions = new FileBridgeOptions { DeviceName = "PHONE", ReceivedFilesDirectory = Path.Combine(Path.GetTempPath(), "fb-phone-" + Guid.NewGuid().ToString("N")) };
        var phoneServer = new TcpServer();
        await phoneServer.StartAsync(0);
        using var phone = new ConnectionManager(phoneServer, new TcpFileClient(), new FileReceiver(phoneOptions), options: phoneOptions);
        var phoneMessages = new ConcurrentQueue<ProtocolMessage>();
        phone.OnMessageReceived += (_, e) => phoneMessages.Enqueue(e.Message);

        var pcDevice = new Device { Id = Guid.NewGuid(), Name = "PC", IpAddress = "127.0.0.1", Port = pc.Port };
        Assert.IsTrue(await phone.ConnectToDeviceAsync(pcDevice));

        // PC answers the handshake and the phone now sees it.
        Assert.IsTrue(await TestFiles.WaitUntilAsync(() => phoneMessages.Any(m => m.Type == MessageType.Handshake)),
            "The phone never saw the PC's handshake reply");

        // A FileRequest sent through the public API reaches the PC, and its ACK comes back.
        var serializer = new ProtocolSerializer();
        await phone.SendMessageAsync(pcDevice.Id.ToString(), new ProtocolMessage
        {
            Type = MessageType.FileRequest,
            Payload = serializer.BuildFileRequestPayload(Guid.NewGuid(), "foto.jpg", 0, 0, false, 0)
        });

        Assert.IsTrue(await TestFiles.WaitUntilAsync(() => pcMessages.Any(m => m.Type == MessageType.FileRequest)),
            "The FileRequest never reached the PC");
        Assert.IsTrue(await TestFiles.WaitUntilAsync(() => phoneMessages.Any(m => m.Type == MessageType.Ack)),
            "The PC's ACK never reached the phone");
    }

    [TestMethod]
    public void Deserialize_RejectsTruncatedOrPaddedFrames()
    {
        var serializer = new ProtocolSerializer();
        var bytes = serializer.Serialize(new ProtocolMessage { Type = MessageType.Data, Payload = new byte[] { 1, 2, 3 } });

        Assert.IsNull(serializer.Deserialize(bytes[..^1]));
        Assert.IsNull(serializer.Deserialize(bytes.Concat(new byte[] { 0 }).ToArray()));
        Assert.IsNotNull(serializer.Deserialize(bytes));
    }

    [TestMethod]
    public void Payloads_WithLongTexts_DoNotCorruptTheMessage()
    {
        // Lengths are stored in one byte: a 300-char name used to wrap around (300 → 44) and corrupt parsing.
        var serializer = new ProtocolSerializer();
        var longName = new string('ñ', 300);

        var (name, key) = serializer.ParseHandshakePayload(serializer.BuildHandshakePayload(longName, new byte[] { 9, 9 }));
        Assert.IsTrue(longName.StartsWith(name) && name.Length > 0);
        CollectionAssert.AreEqual(new byte[] { 9, 9 }, key);

        var (code, message) = serializer.ParseErrorPayload(serializer.BuildErrorPayload(400, longName));
        Assert.AreEqual((ushort)400, code);
        Assert.IsTrue(longName.StartsWith(message));
    }

    [TestMethod]
    public void FileRequest_And_TransferComplete_RoundTrip()
    {
        var serializer = new ProtocolSerializer();
        var id = Guid.NewGuid();

        var request = serializer.ParseFileRequestPayload(serializer.BuildFileRequestPayload(id, "año 2026.pdf", 12345, 0, false, 0));
        Assert.AreEqual(id, request.FileItemId);
        Assert.AreEqual("año 2026.pdf", request.FileName);
        Assert.AreEqual(12345L, request.FileSize);

        var sha = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var complete = serializer.ParseTransferCompletePayload(serializer.BuildTransferCompletePayload(id, sha));
        Assert.AreEqual(id, complete.FileItemId);
        CollectionAssert.AreEqual(sha, complete.Sha256);
    }
}
