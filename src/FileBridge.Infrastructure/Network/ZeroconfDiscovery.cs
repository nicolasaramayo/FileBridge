namespace FileBridge.Infrastructure.Network;

using System.Net;
using System.Threading.Channels;
using FileBridge.Domain.Entities;
using FileBridge.Domain.Interfaces;
using Zeroconf;

public sealed class ZeroconfDiscovery : IDiscoveryService, IDisposable
{
    private const string ServiceType = "_filetransfer._tcp.local.";
    private const int DiscoveryIntervalSeconds = 10;
    private const int DiscoveryTimeoutSeconds = 30;

    private CancellationTokenSource? _cts;
    private readonly ILogger? _logger;
    private readonly Channel<Device> _deviceChannel = Channel.CreateBounded<Device>(new BoundedChannelOptions(16)
    {
        SingleReader = true,
        SingleWriter = true
    });
    private readonly HashSet<string> _seenDeviceIds = new();
    private readonly object _seenLock = new();
    private Task? _discoveryLoop;
    private bool _disposed;

    public event EventHandler<Device>? OnDeviceFound;
    public event EventHandler<Device>? OnDeviceLost;

    public ZeroconfDiscovery(ILogger? logger = null)
    {
        _logger = logger;
    }

    public async Task StartDiscoveryAsync(CancellationToken ct = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ZeroconfDiscovery));

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _discoveryLoop = DiscoveryLoopAsync(_cts.Token);
        _logger?.LogInformation("mDNS discovery started");
    }

    public async Task StopDiscoveryAsync()
    {
        _cts?.Cancel();
        if (_discoveryLoop != null)
        {
            try { await _discoveryLoop; } catch { /* ignore */ }
        }
        _cts?.Dispose();
        _cts = null;
        _logger?.LogInformation("mDNS discovery stopped");
    }

    public async IAsyncEnumerable<Device> GetDiscoveredDevicesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var device in _deviceChannel.Reader.ReadAllAsync(ct))
        {
            yield return device;
        }
    }

    private async Task DiscoveryLoopAsync(CancellationToken ct)
    {
        var endTime = DateTime.UtcNow.AddSeconds(DiscoveryTimeoutSeconds);

        while (!ct.IsCancellationRequested && DateTime.UtcNow < endTime)
        {
            try
            {
                await ScanForServicesAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "mDNS scan error");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(DiscoveryIntervalSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ScanForServicesAsync(CancellationToken ct)
    {
        // ResolveAsync combines browse + resolve in one call
        var responses = await ZeroconfResolver.ResolveAsync(
            ServiceType,
            scanTime: TimeSpan.FromSeconds(5),
            retries: 1,
            cancellationToken: ct);

        foreach (var response in responses)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var device = ParseHostToDevice(response);
                if (device == null)
                    continue;

                var isNew = false;
                lock (_seenLock)
                {
                    if (!_seenDeviceIds.Contains(device.Id.ToString()))
                    {
                        _seenDeviceIds.Add(device.Id.ToString());
                        isNew = true;
                    }
                }

                if (isNew)
                {
                    await _deviceChannel.Writer.WriteAsync(device, ct);
                    OnDeviceFound?.Invoke(this, device);
                    _logger?.LogInformation("Device discovered: {Name} ({IP})", device.Name, device.IpAddress);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error parsing mDNS host response");
            }
        }
    }

    private Device? ParseHostToDevice(IZeroconfHost host)
    {
        // IPAddresses: IReadOnlyList<string> — each string is an IP address
        var ipAddresses = host.IPAddresses;
        string? ipAddress = null;
        if (ipAddresses != null)
        {
            foreach (var addr in ipAddresses)
            {
                if (addr != "127.0.0.1" && addr != "::1" && !string.IsNullOrEmpty(addr))
                {
                    ipAddress = addr;
                    break;
                }
            }
            if (string.IsNullOrEmpty(ipAddress) && ipAddresses.Count > 0)
                ipAddress = ipAddresses[0];
        }
        if (string.IsNullOrEmpty(ipAddress))
            return null;

        // Get service info
        var services = host.Services;
        if (services == null || services.Count == 0)
            return null;

        var service = services.Values.FirstOrDefault();
        if (service == null)
            return null;

        var port = service.Port > 0 ? service.Port : 42420;

        // Get device name from DisplayName
        var deviceName = host.DisplayName;
        if (string.IsNullOrWhiteSpace(deviceName))
            deviceName = "Unknown Device";

        // Properties: IReadOnlyList<IReadOnlyDictionary<string, string>>
        // Each dictionary entry contains a single key-value pair
        var props = service.Properties;
        string deviceIdStr = Guid.NewGuid().ToString();

        if (props != null)
        {
            foreach (var propDict in props)
            {
                foreach (var kvp in propDict)
                {
                    if (kvp.Key == "name" && !string.IsNullOrEmpty(kvp.Value))
                        deviceName = kvp.Value;
                    else if (kvp.Key == "deviceId" && !string.IsNullOrEmpty(kvp.Value))
                        deviceIdStr = kvp.Value;
                }
            }
        }

        if (!Guid.TryParse(deviceIdStr, out var deviceId))
            deviceId = Guid.NewGuid();

        return new Device
        {
            Id = deviceId,
            Name = deviceName,
            IpAddress = ipAddress,
            Port = port,
            LastSeenAt = DateTimeOffset.UtcNow
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _deviceChannel.Writer.Complete();
    }
}
