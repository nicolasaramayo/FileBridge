using FileBridge.Infrastructure.Network;
using FileBridge.Tests.Infrastructure.TestSupport;

namespace FileBridge.Tests.Infrastructure;

/// <summary>
/// The server listens on the whole LAN, so nothing may be written without the receiving user's consent.
/// </summary>
[TestClass]
public class IncomingApprovalTests
{
    [TestMethod]
    public async Task Declined_SenderGetsTheReason_AndNothingIsWritten()
    {
        await using var receiver = await LoopbackReceiver.StartAsync();
        receiver.Receiver.ApproveIncoming = (_, _) => Task.FromResult(false);
        var source = TestFiles.CreateRandomFile(1000, "secret.pdf");

        var error = await SendExpectingErrorAsync(receiver, source);

        Assert.AreEqual(ProtocolErrorCodes.Forbidden, error.Code);
        Assert.IsTrue(error.RemoteMessage.Contains("declined"), error.RemoteMessage);
        Assert.AreEqual(0, receiver.Files().Length);
    }

    [TestMethod]
    public async Task WithoutAnApprover_EverythingIsDeclined()
    {
        await using var receiver = await LoopbackReceiver.StartAsync();
        receiver.Receiver.ApproveIncoming = null;

        var error = await SendExpectingErrorAsync(receiver, TestFiles.CreateRandomFile(10));

        Assert.AreEqual(ProtocolErrorCodes.Forbidden, error.Code);
        Assert.AreEqual(0, receiver.Files().Length);
    }

    [TestMethod]
    public async Task NobodyAnswers_IsDeclinedAfterTheTimeout()
    {
        await using var receiver = await LoopbackReceiver.StartAsync(configure: o => o.IncomingApprovalTimeout = TimeSpan.FromMilliseconds(300));
        var neverAnswered = new TaskCompletionSource<bool>(); // also ignores the token
        receiver.Receiver.ApproveIncoming = (_, _) => neverAnswered.Task;

        var error = await SendExpectingErrorAsync(receiver, TestFiles.CreateRandomFile(10));

        Assert.AreEqual(ProtocolErrorCodes.Forbidden, error.Code);
        Assert.IsTrue(error.RemoteMessage.Contains("nobody accepted"), error.RemoteMessage);
    }

    [TestMethod]
    public async Task TheApproverSeesWhoSendsWhat_BeforeAnyFileExists()
    {
        await using var receiver = await LoopbackReceiver.StartAsync();
        IncomingFileOffer? offer = null;
        var filesWhileAsking = -1;
        receiver.Receiver.ApproveIncoming = (o, _) =>
        {
            offer = o;
            filesWhileAsking = receiver.Files().Length;
            return Task.FromResult(true);
        };
        var source = TestFiles.CreateRandomFile(4321, "vacaciones.jpg");

        await new TcpFileTransport { LocalDeviceName = "Galaxy de Nico" }.SendFileAsync(receiver.AsDevice(), source, Guid.NewGuid());

        Assert.IsNotNull(offer);
        Assert.AreEqual("vacaciones.jpg", offer.FileName);
        Assert.AreEqual(4321L, offer.Size);
        Assert.AreEqual("Galaxy de Nico", offer.PeerName);
        Assert.AreEqual(0, filesWhileAsking, "Nothing may be written before the user accepts");
        Assert.AreEqual(1, receiver.Files().Length);
    }

    [TestMethod]
    public async Task ThrowingApprover_IsTreatedAsDeclined()
    {
        await using var receiver = await LoopbackReceiver.StartAsync();
        receiver.Receiver.ApproveIncoming = (_, _) => throw new InvalidOperationException("no window to show the prompt");

        var error = await SendExpectingErrorAsync(receiver, TestFiles.CreateRandomFile(10));

        Assert.AreEqual(ProtocolErrorCodes.Forbidden, error.Code);
    }

    private static async Task<RemoteProtocolException> SendExpectingErrorAsync(LoopbackReceiver receiver, string source)
    {
        try
        {
            await new TcpFileTransport().SendFileAsync(receiver.AsDevice(), source, Guid.NewGuid());
        }
        catch (RemoteProtocolException ex)
        {
            return ex;
        }
        Assert.Fail("The transfer should have been rejected");
        throw new InvalidOperationException("unreachable");
    }
}
