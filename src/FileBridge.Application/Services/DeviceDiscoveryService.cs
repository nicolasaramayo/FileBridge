using System.Threading.Channels;
using FileBridge.Domain.Entities;
using FileBridge.Domain.Interfaces;

namespace FileBridge.Application.Services;

public class DeviceDiscoveryService : IDiscoveryService
{
    private readonly IDiscoveryService _innerDiscovery;
    private readonly Channel<Device> _deviceChannel = Channel.CreateBounded<Device>(new BoundedChannelOptions(16)
    {
        SingleReader = true,
        SingleWriter = true
    });

    public event EventHandler<Device>? OnDeviceFound;
    public event EventHandler<Device>? OnDeviceLost;

    public DeviceDiscoveryService(IDiscoveryService innerDiscovery)
    {
        _innerDiscovery = innerDiscovery ?? throw new ArgumentNullException(nameof(innerDiscovery));
        
        // Wire up events from wrapped discovery
        _innerDiscovery.OnDeviceFound += (sender, device) =>
        {
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