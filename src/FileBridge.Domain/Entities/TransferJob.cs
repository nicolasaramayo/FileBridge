namespace FileBridge.Domain.Entities;

public class TransferJob
{
    public Guid Id { get; set; }
    public Guid FileItemId { get; set; }
    public Guid DeviceId { get; set; }
    public Enums.TransferStatus Status { get; set; } = Enums.TransferStatus.Pending;
    public Enums.TransferDirection Direction { get; set; }
    public long TotalBytes { get; set; }
    public long TransferredBytes { get; set; }
    public int ChunkSize { get; set; } = 65536;
    public bool IsEncrypted { get; set; }
    public string? ResumeToken { get; set; }
    public string? LocalFilePath { get; set; }
    public string? RemoteFilePath { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
}
