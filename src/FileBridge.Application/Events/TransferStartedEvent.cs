namespace FileBridge.Application.Events;

public record TransferStartedEvent(
    string JobId,
    string FileName,
    string DeviceId,
    DateTimeOffset StartedAt,
    long TotalBytes,
    string Direction
);