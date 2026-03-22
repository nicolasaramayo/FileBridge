namespace FileBridge.Infrastructure.Persistence;

using SQLite;

[Table("TransferJobs")]
public class TransferJobRecord
{
    [PrimaryKey]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    
    public string FileItemId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public int Status { get; set; }
    public int Direction { get; set; }
    public long TotalBytes { get; set; }
    public long TransferredBytes { get; set; }
    public int ChunkSize { get; set; } = 65536;
    public bool IsEncrypted { get; set; }
    public string? ResumeToken { get; set; }
    public string? LocalFilePath { get; set; }
    public string? RemoteFilePath { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
