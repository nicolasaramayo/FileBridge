using System.Collections.Concurrent;
using FileBridge.Domain.Entities;
using FileBridge.Domain.Interfaces;

namespace FileBridge.Application.Services;

/// <summary>In-memory registry of the devices seen during this session.</summary>
public class DeviceRegistry : IDeviceRegistry
{
    private readonly ConcurrentDictionary<Guid, Device> _devices = new();

    public event EventHandler<Device>? DeviceAddedOrUpdated;

    public IReadOnlyCollection<Device> All => _devices.Values.ToList();

    public void AddOrUpdate(Device device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _devices[device.Id] = device;
        DeviceAddedOrUpdated?.Invoke(this, device);
    }

    public Device? Find(Guid deviceId) => _devices.TryGetValue(deviceId, out var device) ? device : null;
}
