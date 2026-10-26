using FileBridge.Domain.Entities;

namespace FileBridge.Domain.Interfaces;

/// <summary>Outcome of a successful transfer, as confirmed by the receiving device.</summary>
public sealed record FileTransferResult(long BytesSent, string Sha256);

/// <summary>
/// Moves the bytes of a local file to another device. Implemented in Infrastructure (TCP),
/// so the Application layer can orchestrate transfers without knowing about sockets.
/// </summary>
public interface IFileTransport
{
    /// <summary>
    /// Sends <paramref name="localFilePath"/> to <paramref name="target"/>. Completes only after the
    /// receiver confirms that size and SHA-256 match; throws otherwise.
    /// </summary>
    Task<FileTransferResult> SendFileAsync(
        Device target,
        string localFilePath,
        Guid transferId,
        IProgress<long>? progress = null,
        CancellationToken ct = default);
}
