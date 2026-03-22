namespace FileBridge.Infrastructure.Persistence;

using SQLite;

[Table("Devices")]
public class DeviceRecord
{
    [PrimaryKey]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public int Port { get; set; }
    public string PublicKey { get; set; } = string.Empty;
    public int Role { get; set; }
    public DateTime PairedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}
