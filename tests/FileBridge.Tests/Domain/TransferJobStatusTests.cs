using FileBridge.Domain.Entities;
using FileBridge.Domain.Enums;

namespace FileBridge.Tests.Domain;

[TestClass]
public class TransferJobStatusTests
{
    [TestMethod]
    public void TryChangeStatus_OnlyChangesFromTheAllowedStates()
    {
        var job = new TransferJob { Status = TransferStatus.InProgress };

        Assert.IsTrue(job.TryChangeStatus(TransferStatus.Paused, out var previous, TransferStatus.Pending, TransferStatus.InProgress));
        Assert.AreEqual(TransferStatus.InProgress, previous);
        Assert.AreEqual(TransferStatus.Paused, job.Status);

        Assert.IsFalse(job.TryChangeStatus(TransferStatus.Cancelled, out previous, TransferStatus.InProgress));
        Assert.AreEqual(TransferStatus.Paused, previous);
        Assert.AreEqual(TransferStatus.Paused, job.Status);
    }

    [TestMethod]
    public void TryChangeStatus_ConcurrentChanges_ExactlyOneWins()
    {
        for (var i = 0; i < 500; i++)
        {
            var job = new TransferJob { Status = TransferStatus.InProgress };
            var winners = 0;

            Parallel.Invoke(
                () => { if (job.TryChangeStatus(TransferStatus.Completed, TransferStatus.InProgress)) Interlocked.Increment(ref winners); },
                () => { if (job.TryChangeStatus(TransferStatus.Paused, TransferStatus.InProgress)) Interlocked.Increment(ref winners); },
                () => { if (job.TryChangeStatus(TransferStatus.Cancelled, TransferStatus.InProgress)) Interlocked.Increment(ref winners); });

            Assert.AreEqual(1, winners, $"iteration {i}");
        }
    }
}
