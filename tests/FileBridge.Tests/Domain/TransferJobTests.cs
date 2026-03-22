using FileBridge.Domain.Entities;
using FileBridge.Domain.Enums;

namespace FileBridge.Tests.Domain;

[TestClass]
public class TransferJobTests
{
    [TestMethod]
    public void TransferJob_ShouldInitializeWithPendingStatus()
    {
        var job = new TransferJob();
        
        Assert.AreEqual(TransferStatus.Pending, job.Status);
        Assert.AreEqual(0, job.TransferredBytes);
        Assert.AreEqual(65536, job.ChunkSize); // Default chunk size
    }
    
    [TestMethod]
    public void TransferJob_ShouldTrackProgress()
    {
        var job = new TransferJob
        {
            TotalBytes = 1000,
            TransferredBytes = 500,
            Status = TransferStatus.InProgress
        };
        
        Assert.AreEqual(1000, job.TotalBytes);
        Assert.AreEqual(500, job.TransferredBytes);
        Assert.AreEqual(TransferStatus.InProgress, job.Status);
        
        // Calculate progress percentage
        var progress = (double)job.TransferredBytes / job.TotalBytes * 100;
        Assert.AreEqual(50, progress);
    }
    
    [TestMethod]
    public void TransferJob_CompletedJob_ShouldHaveCompletionDate()
    {
        var job = new TransferJob
        {
            Status = TransferStatus.Completed,
            CompletedAt = DateTime.UtcNow
        };
        
        Assert.AreEqual(TransferStatus.Completed, job.Status);
        Assert.IsNotNull(job.CompletedAt);
    }
    
    [TestMethod]
    public void TransferJob_ShouldSupportEncryption()
    {
        var job = new TransferJob { IsEncrypted = true };
        Assert.IsTrue(job.IsEncrypted);
    }
    
    [TestMethod]
    public void TransferJob_ShouldSupportResumeToken()
    {
        var job = new TransferJob { ResumeToken = "resume-token-123" };
        Assert.AreEqual("resume-token-123", job.ResumeToken);
    }
    
    [TestMethod]
    public void TransferJob_FailedJob_ShouldHaveErrorMessage()
    {
        var job = new TransferJob
        {
            Status = TransferStatus.Failed,
            ErrorMessage = "Connection lost"
        };
        
        Assert.AreEqual(TransferStatus.Failed, job.Status);
        Assert.AreEqual("Connection lost", job.ErrorMessage);
    }
}
