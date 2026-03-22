namespace FileBridge.Domain.Entities;

public class FileItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string MimeType { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public Enums.TransferDirection Direction { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
