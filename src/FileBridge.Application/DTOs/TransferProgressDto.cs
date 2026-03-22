using FileBridge.Domain.Enums;

namespace FileBridge.Application.DTOs;

public record TransferProgressDto(
    string JobId,
    string FileName,
    long TotalBytes,
    long TransferredBytes,
    double ProgressPercent,
    double SpeedBytesPerSecond,
    TimeSpan? EstimatedTimeRemaining,
    TransferStatus Status
);