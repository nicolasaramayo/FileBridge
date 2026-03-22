namespace FileBridge.Application.DTOs;

public record DeviceDto(
    Guid Id,
    string Name,
    string IpAddress,
    int Port,
    byte[]? PublicKey,
    DateTimeOffset? PairedAt,
    DateTimeOffset LastSeenAt,
    bool IsPaired,
    bool IsConnected
);