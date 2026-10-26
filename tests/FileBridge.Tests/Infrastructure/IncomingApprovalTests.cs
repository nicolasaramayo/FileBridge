using FileBridge.Domain.Enums;
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
        receiver.Receiver.ApproveIncoming = (_, _) => Task.FromResult(IncomingFileDecision.Declined);
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
        var neverAnswered = new TaskCompletionSource<IncomingFileDecision>(); // also ignores the token
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
            return Task.FromResult(IncomingFileDecision.Accepted);
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

    [TestMethod]
    public async Task Unavailable_TellsTheSenderTheAppIsNotOpen()
    {
        await using var receiver = await LoopbackReceiver.StartAsync();
        receiver.Receiver.ApproveIncoming = (_, _) => Task.FromResult(IncomingFileDecision.Unavailable);

        var error = await SendExpectingErrorAsync(receiver, TestFiles.CreateRandomFile(10));

        Assert.AreEqual(ProtocolErrorCodes.Forbidden, error.Code);
        Assert.IsTrue(error.RemoteMessage.Contains("not open"), error.RemoteMessage);
    }

    [TestMethod]
    public async Task ASecondOfferWhileTheUserIsDeciding_IsRefusedAsBusy()
    {
        await using var receiver = await LoopbackReceiver.StartAsync();
        var asking = new TaskCompletionSource();
        var answer = new TaskCompletionSource<IncomingFileDecision>();
        var asked = 0;
        receiver.Receiver.ApproveIncoming = (_, _) =>
        {
            Interlocked.Increment(ref asked);
            asking.TrySetResult();
            return answer.Task;
        };

        var first = new TcpFileTransport().SendFileAsync(receiver.AsDevice(), TestFiles.CreateRandomFile(100, "first.bin"), Guid.NewGuid());
        await asking.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var error = await SendExpectingErrorAsync(receiver, TestFiles.CreateRandomFile(100, "second.bin"));
        Assert.AreEqual(ProtocolErrorCodes.Forbidden, error.Code);
        Assert.IsTrue(error.RemoteMessage.Contains("already asking"), error.RemoteMessage);
        Assert.AreEqual(1, asked, "Only one prompt at a time");

        answer.SetResult(IncomingFileDecision.Accepted);
        await first;
        CollectionAssert.AreEqual(new[] { "first.bin" }, receiver.Files().Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public async Task AfterADecline_TheSameSenderIsRefusedForAWhile_WithoutAskingAgain()
    {
        await using var receiver = await LoopbackReceiver.StartAsync();
        var asked = 0;
        receiver.Receiver.ApproveIncoming = (_, _) =>
        {
            Interlocked.Increment(ref asked);
            return Task.FromResult(IncomingFileDecision.Declined);
        };

        await SendExpectingErrorAsync(receiver, TestFiles.CreateRandomFile(10));
        var again = await SendExpectingErrorAsync(receiver, TestFiles.CreateRandomFile(10));

        Assert.AreEqual(1, asked, "A spamming sender must not get a new prompt right after a decline");
        Assert.IsTrue(again.RemoteMessage.Contains("a moment ago"), again.RemoteMessage);
    }

    [TestMethod]
    public async Task SenderDisconnectsWhileThePromptIsOpen_ThePromptIsCancelled()
    {
        await using var receiver = await LoopbackReceiver.StartAsync();
        var promptCancelled = new TaskCompletionSource();
        var asking = new TaskCompletionSource();
        receiver.Receiver.ApproveIncoming = async (_, ct) =>
        {
            asking.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { promptCancelled.TrySetResult(); }
            return IncomingFileDecision.Declined;
        };
        using var raw = new RawProtocolClient();
        await raw.ConnectAsync(receiver.Port);
        await raw.SendAsync(MessageType.FileRequest, raw.Serializer.BuildFileRequestPayload(Guid.NewGuid(), "x.bin", 10, 0, false, 0));
        await asking.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await raw.CloseAsync();

        var closed = await Task.WhenAny(promptCancelled.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.AreSame(promptCancelled.Task, closed, "The prompt must close when the sender goes away, not wait for the 60 s timeout");
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
