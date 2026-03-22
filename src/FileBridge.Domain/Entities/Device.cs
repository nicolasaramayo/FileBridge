namespace FileBridge.Domain.Entities;

public class Device
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public int Port { get; set; }
    public byte[] PublicKey { get; set; } = Array.Empty<byte>();
    public DateTimeOffset PairedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public Enums.DeviceRole Role { get; set; } = Enums.DeviceRole.Unknown;
}
