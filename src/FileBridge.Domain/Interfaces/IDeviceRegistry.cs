using FileBridge.Domain.Entities;

namespace FileBridge.Domain.Interfaces;

/// <summary>
/// Devices known in this session (discovered via mDNS or added by hand), so a transfer job
/// that only carries a DeviceId can be resolved to an address and port.
/// </summary>
public interface IDeviceRegistry
{
    /// <summary>
    /// Adds or replaces the device. Another entry with the same IP:port (a peer that restarted with a new id,
    /// or a manual entry later discovered via mDNS) is removed and reported through <see cref="DeviceRemoved"/>.
    /// </summary>
    void AddOrUpdate(Device device);
    Device? Find(Guid deviceId);
    IReadOnlyCollection<Device> All { get; }
    event EventHandler<Device>? DeviceAddedOrUpdated;
    event EventHandler<Device>? DeviceRemoved;
}
