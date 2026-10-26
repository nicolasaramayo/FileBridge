using FileBridge.Domain.Entities;
using FileBridge.Domain.Interfaces;

namespace FileBridge.Application.Services;

public class DeviceDiscoveryService : IDiscoveryService
{
    private readonly IDiscoveryService _innerDiscovery;
    private readonly IDeviceRegistry? _registry;

    public event EventHandler<Device>? OnDeviceFound;
    public event EventHandler<Device>? OnDeviceLost;

    public DeviceDiscoveryService(IDiscoveryService innerDiscovery, IDeviceRegistry? registry = null)
    {
        _innerDiscovery = innerDiscovery ?? throw new ArgumentNullException(nameof(innerDiscovery));
        _registry = registry;

        // Wire up events from wrapped discovery
        _innerDiscovery.OnDeviceFound += (sender, device) =>
        {
            // Remember the device so a transfer job (which only has DeviceId) can find its address later.
            _registry?.AddOrUpdate(device);
            OnDeviceFound?.Invoke(this, device);
        };

        _innerDiscovery.OnDeviceLost += (sender, device) =>
        {
            OnDeviceLost?.Invoke(this, device);
        };
    }

    public async Task StartDiscoveryAsync(CancellationToken ct = default)
    {
        await _innerDiscovery.StartDiscoveryAsync(ct);
    }

    public Task StopDiscoveryAsync()
    {
        return _innerDiscovery.StopDiscoveryAsync();
    }

    public async IAsyncEnumerable<Device> GetDiscoveredDevicesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var device in _innerDiscovery.GetDiscoveredDevicesAsync(ct))
        {
            yield return device;
        }
    }
}
