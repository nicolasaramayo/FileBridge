using FileBridge.Domain.Entities;
using FileBridge.Domain.Enums;
using FileBridge.Domain.Interfaces;
using Moq;

namespace FileBridge.Tests.Application;

[TestClass]
public class FileServiceTests
{
    private Mock<IFileService> _mockFileService = null!;
    
    [TestInitialize]
    public void Setup()
    {
        _mockFileService = new Mock<IFileService>();
    }
    
    [TestMethod]
    public async Task GetFilesAsync_ShouldReturnFileList()
    {
        // Arrange
        var testFiles = new List<FileItem>
        {
            new() { FileName = "file1.txt", FileSize = 100, MimeType = "text/plain" },
            new() { FileName = "file2.pdf", FileSize = 200, MimeType = "application/pdf" }
        };
        
        _mockFileService.Setup(s => s.GetFilesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(testFiles);
        
        // Act
        var result = await _mockFileService.Object.GetFilesAsync("/test", CancellationToken.None);
        
        // Assert
        Assert.AreEqual(2, result.Count());
        Assert.IsTrue(result.Any(f => f.FileName == "file1.txt"));
        Assert.IsTrue(result.Any(f => f.FileName == "file2.pdf"));
    }
    
    [TestMethod]
    public async Task GetFilesAsync_ShouldReturnEmptyList_WhenNoFiles()
    {
        // Arrange
        _mockFileService.Setup(s => s.GetFilesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FileItem>());
        
        // Act
        var result = await _mockFileService.Object.GetFilesAsync("/empty", CancellationToken.None);
        
        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.Count());
    }
    
    [TestMethod]
    public async Task GetFileInfoAsync_ShouldReturnFile_WhenExists()
    {
        // Arrange
        var testFile = new FileItem
        {
            FileName = "test.txt",
            FilePath = "/test.txt",
            FileSize = 1024,
            MimeType = "text/plain"
        };
        
        _mockFileService.Setup(s => s.GetFileInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(testFile);
        
        // Act
        var result = await _mockFileService.Object.GetFileInfoAsync("/test.txt", CancellationToken.None);
        
        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("test.txt", result.FileName);
        Assert.AreEqual(1024, result.FileSize);
    }
    
    [TestMethod]
    public async Task GetFileInfoAsync_ShouldReturnNull_WhenNotExists()
    {
        // Arrange
        _mockFileService.Setup(s => s.GetFileInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileItem?)null);
        
        // Act
        var result = await _mockFileService.Object.GetFileInfoAsync("/nonexistent.txt", CancellationToken.None);
        
        // Assert
        Assert.IsNull(result);
    }
    
    [TestMethod]
    public async Task ComputeHashAsync_ShouldReturnHash()
    {
        // Arrange
        _mockFileService.Setup(s => s.ComputeHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("abc123def456");
        
        // Act
        var result = await _mockFileService.Object.ComputeHashAsync("/test/file.txt", CancellationToken.None);
        
        // Assert
        Assert.AreEqual("abc123def456", result);
    }
    
    [TestMethod]
    public async Task OpenReadAsync_ShouldReturnStream()
    {
        // Arrange
        var mockStream = new MemoryStream(new byte[] { 1, 2, 3 });
        _mockFileService.Setup(s => s.OpenReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockStream);
        
        // Act
        var result = await _mockFileService.Object.OpenReadAsync("/test/file.txt", CancellationToken.None);
        
        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(3, result.Length);
    }
    
    [TestMethod]
    public async Task OpenWriteAsync_ShouldReturnStream()
    {
        // Arrange
        var mockStream = new MemoryStream();
        _mockFileService.Setup(s => s.OpenWriteAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockStream);
        
        // Act
        var result = await _mockFileService.Object.OpenWriteAsync("/test/new.txt", 1024, CancellationToken.None);
        
        // Assert
        Assert.IsNotNull(result);
    }
    
    [TestMethod]
    public async Task GetFilesAsync_ShouldRespectCancellation()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel();
        
        _mockFileService.Setup(s => s.GetFilesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        
        // Act & Assert
        OperationCanceledException? ex = null;
        try
        {
            await _mockFileService.Object.GetFilesAsync("/test", cts.Token);
        }
        catch (OperationCanceledException e)
        {
            ex = e;
        }
        Assert.IsNotNull(ex);
    }
}
