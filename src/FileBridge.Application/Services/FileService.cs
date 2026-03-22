using System.IO;
using System.Security.Cryptography;
using FileBridge.Domain.Entities;
using FileBridge.Domain.Enums;
using FileBridge.Domain.Interfaces;

namespace FileBridge.Application.Services;

public class FileService : IFileService
{
    public async Task<IEnumerable<FileItem>> GetFilesAsync(string folderPath, CancellationToken ct = default)
    {
        if (!Directory.Exists(folderPath))
            return Enumerable.Empty<FileItem>();

        var files = new List<FileItem>();
        
        await Task.Run(() =>
        {
            foreach (var filePath in Directory.GetFiles(folderPath))
            {
                try
                {
                    var fileInfo = new FileInfo(filePath);
                    files.Add(new FileItem
                    {
                        Id = Guid.NewGuid(),
                        FileName = fileInfo.Name,
                        FilePath = fileInfo.FullName,
                        FileSize = fileInfo.Length,
                        MimeType = GetMimeType(fileInfo.Extension),
                        Direction = TransferDirection.Send,
                        CreatedAt = DateTimeOffset.UtcNow
                    });
                }
                catch { /* Skip inaccessible files */ }
            }
        }, ct);

        return files;
    }

    public async Task<FileItem?> GetFileInfoAsync(string path, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            if (!File.Exists(path))
                return null;

            var fileInfo = new FileInfo(path);
            return new FileItem
            {
                Id = Guid.NewGuid(),
                FileName = fileInfo.Name,
                FilePath = fileInfo.FullName,
                FileSize = fileInfo.Length,
                MimeType = GetMimeType(fileInfo.Extension),
                Direction = TransferDirection.Send,
                CreatedAt = DateTimeOffset.UtcNow
            };
        }, ct);
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await Task.Yield(); // Ensure we're truly async
        return stream;
    }

    public async Task<Stream> OpenWriteAsync(string path, long size, CancellationToken ct = default)
    {
        // Ensure directory exists
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await Task.Yield(); // Ensure we're truly async
        return stream;
    }

    public async Task<string> ComputeHashAsync(string path, CancellationToken ct = default)
    {
        using var sha256 = SHA256.Create();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        var hashBytes = await sha256.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static string GetMimeType(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".txt" => "text/plain",
            ".html" or ".htm" => "text/html",
            ".css" => "text/css",
            ".js" => "application/javascript",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".mp3" => "audio/mpeg",
            ".mp4" => "video/mp4",
            _ => "application/octet-stream"
        };
    }
}