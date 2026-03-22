using FileBridge.Domain.Entities;

namespace FileBridge.Domain.Interfaces;

public interface IFileService
{
    Task<IEnumerable<FileItem>> GetFilesAsync(string folderPath, CancellationToken ct = default);
    Task<FileItem?> GetFileInfoAsync(string path, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string path, CancellationToken ct = default);
    Task<Stream> OpenWriteAsync(string path, long size, CancellationToken ct = default);
    Task<string> ComputeHashAsync(string path, CancellationToken ct = default);
}
