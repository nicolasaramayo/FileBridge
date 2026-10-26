using FileBridge.Domain.Entities;

namespace FileBridge.Domain.Interfaces;

/// <summary>
/// Devices known in this session (discovered via mDNS or added by hand), so a transfer job
/// that only carries a DeviceId can be resolved to an address and port.
/// </summary>
public interface IDeviceRegistry
{
    void AddOrUpdate(Device device);
    Device? Find(Guid deviceId);
    IReadOnlyCollection<Device> All { get; }
    event EventHandler<Device>? DeviceAddedOrUpdated;
}
