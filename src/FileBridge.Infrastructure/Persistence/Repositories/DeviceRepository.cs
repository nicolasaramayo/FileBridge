namespace FileBridge.Infrastructure.Persistence.Repositories;

using FileBridge.Domain.Entities;
using FileBridge.Domain.Enums;

public class DeviceRepository : IDeviceRepository
{
    private readonly FileBridgeDbContext _context;

    public DeviceRepository(FileBridgeDbContext context)
    {
        _context = context;
    }

    public async Task<IEnumerable<Device>> GetAllAsync(CancellationToken ct = default)
    {
        var records = await _context.Devices.ToListAsync();
        return records.Select(MapToDomain);
    }

    public async Task<IEnumerable<Device>> GetAllPairedAsync(CancellationToken ct = default)
    {
        var records = await _context.Devices
            .Where(d => d.PairedAt > DateTime.MinValue)
            .ToListAsync();
        return records.Select(MapToDomain);
    }

    public async Task<Device?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        var record = await _context.Devices
            .Where(d => d.Id == id)
            .FirstOrDefaultAsync();
        return record != null ? MapToDomain(record) : null;
    }

    public async Task AddAsync(Device device, CancellationToken ct = default)
    {
        var record = MapToRecord(device);
        await _context.InsertAsync(record);
    }

    public async Task UpdateAsync(Device device, CancellationToken ct = default)
    {
        var record = MapToRecord(device);
        await _context.UpdateAsync(record);
    }

    public async Task UpdateLastSeenAsync(string id, CancellationToken ct = default)
    {
        var record = await _context.Devices
            .Where(d => d.Id == id)
            .FirstOrDefaultAsync();

        if (record != null)
        {
            record.LastSeenAt = DateTime.UtcNow;
            await _context.UpdateAsync(record);
        }
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        var record = await _context.Devices
            .Where(d => d.Id == id)
            .FirstOrDefaultAsync();
        if (record != null)
        {
            await _context.DeleteAsync(record);
        }
    }

    private static Device MapToDomain(DeviceRecord record)
    {
        return new Device
        {
            Id = Guid.Parse(record.Id),
            Name = record.Name,
            IpAddress = record.IpAddress,
            Port = record.Port,
            PublicKey = string.IsNullOrEmpty(record.PublicKey) 
                ? Array.Empty<byte>() 
                : Convert.FromBase64String(record.PublicKey),
            PairedAt = new DateTimeOffset(record.PairedAt),
            LastSeenAt = new DateTimeOffset(record.LastSeenAt),
            Role = (DeviceRole)record.Role
        };
    }

    private static DeviceRecord MapToRecord(Device device)
    {
        return new DeviceRecord
        {
            Id = device.Id.ToString(),
            Name = device.Name,
            IpAddress = device.IpAddress,
            Port = device.Port,
            PublicKey = device.PublicKey.Length > 0 
                ? Convert.ToBase64String(device.PublicKey) 
                : string.Empty,
            PairedAt = device.PairedAt.UtcDateTime,
            LastSeenAt = device.LastSeenAt.UtcDateTime,
            Role = (int)device.Role
        };
    }
}
