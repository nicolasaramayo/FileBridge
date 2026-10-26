using FileBridge.Domain.Entities;
using FileBridge.Domain.Interfaces;

namespace FileBridge.Application.Services;

/// <summary>In-memory registry of the devices seen during this session, one entry per IP:port.</summary>
public class DeviceRegistry : IDeviceRegistry
{
    private readonly Dictionary<Guid, Device> _devices = new();
    private readonly object _lock = new();

    public event EventHandler<Device>? DeviceAddedOrUpdated;
    public event EventHandler<Device>? DeviceRemoved;

    public IReadOnlyCollection<Device> All
    {
        get
        {
            lock (_lock)
                return _devices.Values.ToList();
        }
    }

    public void AddOrUpdate(Device device)
    {
        ArgumentNullException.ThrowIfNull(device);

        List<Device> replaced;
        lock (_lock)
        {
            // Two devices cannot listen on the same IP:port at the same time: an older entry for that
            // endpoint is a stale identity (peer restarted with a new id, or a manual entry).
            replaced = _devices.Values
                .Where(d => d.Id != device.Id
                            && d.Port == device.Port
                            && string.Equals(d.IpAddress, device.IpAddress, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var stale in replaced)
                _devices.Remove(stale.Id);

            _devices[device.Id] = device;
        }

        foreach (var stale in replaced)
            DeviceRemoved?.Invoke(this, stale);
        DeviceAddedOrUpdated?.Invoke(this, device);
    }

    public Device? Find(Guid deviceId)
    {
        lock (_lock)
            return _devices.TryGetValue(deviceId, out var device) ? device : null;
    }
}
