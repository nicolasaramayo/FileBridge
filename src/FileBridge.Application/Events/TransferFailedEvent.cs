namespace FileBridge.Application.Events;

public record TransferFailedEvent(
    string JobId,
    string FileName,
    string DeviceId,
    DateTimeOffset FailedAt,
    long TransferredBytes,
    long TotalBytes,
    string ErrorMessage
);