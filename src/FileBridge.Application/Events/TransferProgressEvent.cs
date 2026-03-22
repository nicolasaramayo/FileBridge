namespace FileBridge.Application.Events;

public record TransferProgressEvent(
    string JobId,
    string FileName,
    long TransferredBytes,
    long TotalBytes,
    double ProgressPercent,
    double SpeedBytesPerSecond,
    TimeSpan? EstimatedTimeRemaining
);