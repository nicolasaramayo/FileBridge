using FileBridge.Domain.Enums;

namespace FileBridge.Application.DTOs;

public record FileItemDto(
    Guid Id,
    string FileName,
    string FilePath,
    long FileSize,
    string? MimeType,
    string? Hash,
    TransferDirection Direction,
    DateTimeOffset CreatedAt
);