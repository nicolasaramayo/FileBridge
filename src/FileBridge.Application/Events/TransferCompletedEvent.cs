namespace FileBridge.Application.Events;

public record TransferCompletedEvent(
    string JobId,
    string FileName,
    string DeviceId,
    DateTimeOffset CompletedAt,
    long TotalBytes,
    string? Hash
);