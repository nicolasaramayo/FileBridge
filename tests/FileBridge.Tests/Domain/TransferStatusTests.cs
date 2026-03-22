using FileBridge.Domain.Enums;

namespace FileBridge.Tests.Domain;

[TestClass]
public class TransferStatusTests
{
    [TestMethod]
    public void TransferStatus_ShouldHaveExpectedValues()
    {
        var pending = (int)TransferStatus.Pending;
        var inProgress = (int)TransferStatus.InProgress;
        var paused = (int)TransferStatus.Paused;
        var completed = (int)TransferStatus.Completed;
        var failed = (int)TransferStatus.Failed;
        var cancelled = (int)TransferStatus.Cancelled;
        
        Assert.AreEqual(0, pending);
        Assert.AreEqual(1, inProgress);
        Assert.AreEqual(2, paused);
        Assert.AreEqual(3, completed);
        Assert.AreEqual(4, failed);
        Assert.AreEqual(5, cancelled);
    }
    
    [TestMethod]
    public void TransferDirection_ShouldHaveSendAndReceive()
    {
        var send = (int)TransferDirection.Send;
        var receive = (int)TransferDirection.Receive;
        
        Assert.AreEqual(0, send);
        Assert.AreEqual(1, receive);
    }
    
    [TestMethod]
    public void TransferStatus_CanBeUsedInSwitchExpression()
    {
        var status = TransferStatus.InProgress;
        var result = status switch
        {
            TransferStatus.Pending => "pending",
            TransferStatus.InProgress => "in progress",
            TransferStatus.Completed => "completed",
            _ => "unknown"
        };
        
        Assert.AreEqual("in progress", result);
    }
}
