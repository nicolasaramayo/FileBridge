using FileBridge.Domain.Entities;
using FileBridge.Domain.Enums;

namespace FileBridge.Tests.Domain;

[TestClass]
public class FileItemTests
{
    [TestMethod]
    public void FileItem_ShouldInitializeWithDefaults()
    {
        var file = new FileItem();
        
        // Guid is auto-generated, verify it's not empty
        Assert.AreNotEqual(Guid.Empty, file.Id);
        Assert.AreEqual(0, file.FileSize);
        Assert.AreEqual(string.Empty, file.MimeType);
    }
    
    [TestMethod]
    public void FileItem_ShouldSetProperties()
    {
        var file = new FileItem
        {
            FileName = "test.pdf",
            FilePath = "/documents/test.pdf",
            FileSize = 1024 * 1024, // 1MB
            MimeType = "application/pdf",
            Hash = "abc123"
        };
        
        Assert.AreEqual("test.pdf", file.FileName);
        Assert.AreEqual("/documents/test.pdf", file.FilePath);
        Assert.AreEqual(1048576, file.FileSize);
        Assert.AreEqual("application/pdf", file.MimeType);
        Assert.AreEqual("abc123", file.Hash);
    }
    
    [TestMethod]
    public void FileItem_ShouldSupportTransferDirection()
    {
        var file = new FileItem
        {
            Direction = TransferDirection.Send
        };
        
        Assert.AreEqual(TransferDirection.Send, file.Direction);
    }
    
    [TestMethod]
    public void FileItem_ShouldTrackCreatedAt()
    {
        var now = DateTimeOffset.UtcNow;
        var file = new FileItem { CreatedAt = now };
        
        Assert.AreEqual(now, file.CreatedAt);
    }
}
