using FileBridge.Domain.Entities;

namespace FileBridge.Domain.Interfaces;

public class TransferProgressEventArgs : EventArgs
{
    public string JobId { get; init; } = string.Empty;
    public long BytesTransferred { get; init; }
    public long TotalBytes { get; init; }
    public double SpeedBytesPerSec { get; init; }
    public TimeSpan? EstimatedTimeRemaining { get; init; }
}

public interface ITransferService
{
    Task<bool> SendFileAsync(TransferJob job, CancellationToken ct = default);
    Task<bool> ReceiveFileAsync(TransferJob job, CancellationToken ct = default);
    Task PauseAsync(string jobId);
    Task ResumeAsync(string jobId);
    Task CancelAsync(string jobId);
    event EventHandler<TransferProgressEventArgs>? OnProgressChanged;
}
