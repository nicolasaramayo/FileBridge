using FileBridge.Domain.Entities;

namespace FileBridge.Domain.Interfaces;

public interface IDiscoveryService
{
    Task StartDiscoveryAsync(CancellationToken ct = default);
    Task StopDiscoveryAsync();
    IAsyncEnumerable<Device> GetDiscoveredDevicesAsync(CancellationToken ct = default);
    event EventHandler<Device>? OnDeviceFound;
    event EventHandler<Device>? OnDeviceLost;
}
