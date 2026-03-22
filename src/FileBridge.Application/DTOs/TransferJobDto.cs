using FileBridge.Domain.Enums;

namespace FileBridge.Application.DTOs;

public record TransferJobDto(
    Guid Id,
    Guid FileItemId,
    Guid DeviceId,
    string FileName,
    TransferStatus Status,
    TransferDirection Direction,
    long TotalBytes,
    long TransferredBytes,
    int ChunkSize,
    bool IsEncrypted,
    string? ResumeToken,
    string? LocalFilePath,
    string? RemoteFilePath,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorMessage
);