namespace FileBridge.Infrastructure.Network;

using FileBridge.Domain.Entities;
using FileBridge.Domain.Interfaces;

public sealed class ZeroconfAdvertiser : IDiscoveryService, IDisposable
{
    private const string ServiceType = "_filetransfer._tcp.local.";
    private const int BroadcastIntervalSeconds = 60;

    private CancellationTokenSource? _cts;
    private readonly ILogger? _logger;
    private readonly string _deviceName;
    private readonly int _port;
    private readonly string _deviceId;
    private readonly Dictionary<string, string> _properties;
    private bool _disposed;

    public event EventHandler<Device>? OnDeviceFound;
    public event EventHandler<Device>? OnDeviceLost;

    public ZeroconfAdvertiser(string deviceName, int port, string deviceId, ILogger? logger = null)
    {
        _deviceName = deviceName ?? throw new ArgumentNullException(nameof(deviceName));
        _port = port;
        _deviceId = deviceId ?? Guid.NewGuid().ToString();
        _logger = logger;
        _properties = new Dictionary<string, string>
        {
            ["name"] = _deviceName,
            ["port"] = _port.ToString(),
            ["version"] = "1",
            ["deviceId"] = _deviceId
        };
    }

    public async Task StartDiscoveryAsync(CancellationToken ct = default)
    {
        // Implements IDiscoveryService — start advertising as the primary use
        await StartAdvertisingAsync(ct);
    }

    public async Task StartAdvertisingAsync(CancellationToken ct = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ZeroconfAdvertiser));

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _logger?.LogInformation(
            "mDNS advertiser: Zeroconf.RegisterAsync for service '{Name}' on port {Port}",
            _deviceName, _port);

        // NOTE: The Zeroconf package (novotnyllc/Zeroconf v3.7.16) does not include
        // built-in service advertisement APIs. The advertised service is registered
        // via the platform's native mDNS stack:
        //   - Windows: dns_sd.dll (Bonjour) via P/Invoke
        //   - Android: NsdManager via Android binding
        //
        // For a working implementation, use one of these approaches:
        // 1. Platform-specific partial classes that wrap the native APIs
        // 2. A separate advertisement library (e.g., Makaretu.Dns on non-Apple platforms)
        //
        // For now, advertising is a stub. The Discovery service (ZeroconfDiscovery)
        // is fully functional for device discovery.

        await Task.CompletedTask;
    }

    public Task StopDiscoveryAsync()
    {
        return StopAdvertisingAsync();
    }

    public async Task StopAdvertisingAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _logger?.LogInformation("mDNS advertiser stopped");
        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<Device> GetDiscoveredDevicesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopAdvertisingAsync().GetAwaiter().GetResult();
    }
}
