using System.Security.Cryptography;
using FileBridge.Application.Services;

namespace FileBridge.Tests.Application;

/// <summary>
/// Tests the real FileService against a temporary folder. (FileServiceTests only checks what a Moq
/// mock returns, so it cannot fail whatever FileService does.)
/// </summary>
[TestClass]
public class FileServiceRealTests
{
    private string _dir = null!;
    private readonly FileService _service = new();

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "filebridge-fileservice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [TestMethod]
    public async Task GetFilesAsync_ListsTheFilesOfTheFolder()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "hola");
        File.WriteAllBytes(Path.Combine(_dir, "b.pdf"), new byte[10]);

        var files = (await _service.GetFilesAsync(_dir)).OrderBy(f => f.FileName).ToList();

        Assert.AreEqual(2, files.Count);
        Assert.AreEqual("a.txt", files[0].FileName);
        Assert.AreEqual("text/plain", files[0].MimeType);
        Assert.AreEqual(10L, files[1].FileSize);
    }

    [TestMethod]
    public async Task ComputeHashAsync_MatchesSha256()
    {
        var path = Path.Combine(_dir, "data.bin");
        var bytes = RandomNumberGenerator.GetBytes(100_000);
        File.WriteAllBytes(path, bytes);

        var hash = await _service.ComputeHashAsync(path);

        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), hash);
    }

    [TestMethod]
    public async Task OpenWriteThenOpenRead_RoundTrips()
    {
        var path = Path.Combine(_dir, "sub", "copy.bin");
        var bytes = RandomNumberGenerator.GetBytes(5000);

        await using (var write = await _service.OpenWriteAsync(path, bytes.Length))
            await write.WriteAsync(bytes);
        await using var read = await _service.OpenReadAsync(path);
        var buffer = new MemoryStream();
        await read.CopyToAsync(buffer);

        CollectionAssert.AreEqual(bytes, buffer.ToArray());
    }
}
