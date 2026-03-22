using FileBridge.Domain.ValueObjects;

namespace FileBridge.Tests.Domain;

[TestClass]
public class ValueObjectTests
{
    [TestMethod]
    public void FileHash_ShouldBeEqualForSameValue()
    {
        var hash1 = new FileHash("abc123");
        var hash2 = new FileHash("abc123");
        
        Assert.AreEqual(hash1, hash2);
        Assert.IsTrue(hash1 == hash2);
    }
    
    [TestMethod]
    public void FileHash_ShouldBeDifferentForDifferentValues()
    {
        var hash1 = new FileHash("abc123");
        var hash2 = new FileHash("def456");
        
        Assert.AreNotEqual(hash1, hash2);
        Assert.IsTrue(hash1 != hash2);
    }
    
    [TestMethod]
    public void FileHash_ShouldThrowForEmptyValue()
    {
        ArgumentException? ex = null;
        try
        {
            _ = new FileHash("");
        }
        catch (ArgumentException e)
        {
            ex = e;
        }
        Assert.IsNotNull(ex);
    }
    
    [TestMethod]
    public void FileHash_ShouldThrowForWhitespaceOnly()
    {
        ArgumentException? ex = null;
        try
        {
            _ = new FileHash("   ");
        }
        catch (ArgumentException e)
        {
            ex = e;
        }
        Assert.IsNotNull(ex);
    }
    
    [TestMethod]
    public void FileHash_ShouldStoreValue()
    {
        var hash = new FileHash("test-hash");
        Assert.AreEqual("test-hash", hash.Value);
    }
    
    [TestMethod]
    public void ChunkInfo_ShouldInitializeCorrectly()
    {
        var data = new byte[] { 1, 2, 3, 4 };
        var chunk = new ChunkInfo(0, 4, data);
        
        Assert.AreEqual(0, chunk.Index);
        Assert.AreEqual(4, chunk.Size);
        Assert.AreEqual(data, chunk.Data);
    }
    
    [TestMethod]
    public void ChunkInfo_ShouldThrowForNegativeIndex()
    {
        ArgumentException? ex = null;
        try
        {
            _ = new ChunkInfo(-1, 4, new byte[4]);
        }
        catch (ArgumentException e)
        {
            ex = e;
        }
        Assert.IsNotNull(ex);
    }
    
    [TestMethod]
    public void ChunkInfo_ShouldThrowForZeroSize()
    {
        ArgumentException? ex = null;
        try
        {
            _ = new ChunkInfo(0, 0, new byte[0]);
        }
        catch (ArgumentException e)
        {
            ex = e;
        }
        Assert.IsNotNull(ex);
    }
    
    [TestMethod]
    public void ChunkInfo_ShouldThrowForNullData()
    {
        ArgumentNullException? ex = null;
        try
        {
            _ = new ChunkInfo(0, 4, null!);
        }
        catch (ArgumentNullException e)
        {
            ex = e;
        }
        Assert.IsNotNull(ex);
    }
    
    [TestMethod]
    public void ChunkInfo_ShouldBeEqualForSameIndexAndSize()
    {
        var chunk1 = new ChunkInfo(1, 1024, new byte[] { 1 });
        var chunk2 = new ChunkInfo(1, 1024, new byte[] { 2 }); // Different data, same index/size
        
        Assert.AreEqual(chunk1, chunk2);
    }
}
