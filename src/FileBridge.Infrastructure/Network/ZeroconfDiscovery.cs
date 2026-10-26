namespace FileBridge.Infrastructure.Network;

using System.Threading.Channels;
using FileBridge.Domain.Entities;
using FileBridge.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Zeroconf;

public sealed class ZeroconfDiscovery : IDiscoveryService, IDisposable
{
    private const string ServiceType = "_filetransfer._tcp.local.";
    private const int DiscoveryIntervalSeconds = 10;

    private CancellationTokenSource? _cts;
    private readonly ILogger? _logger;
    private readonly Guid? _localDeviceId;
    // Several producers can run (hosted service + "Refresh"), and nobody may be reading:
    // never block the scan loop on a full channel.
    private readonly Channel<Device> _deviceChannel = Channel.CreateBounded<Device>(new BoundedChannelOptions(64)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest
    });
    // deviceId -> "ip:port" last reported, so a device that changes address is reported again.
    private readonly Dictionary<string, string> _seenDevices = new();
    private readonly object _seenLock = new();
    // Start and Stop are serialized: a Stop that was still waiting for the old loop used to clear and dispose
    // the cancellation source of a loop started in the meantime, which then could never be stopped.
    private readonly SemaphoreSlim _startStopGate = new(1, 1);
    private Task? _discoveryLoop;
    private bool _disposed;

    public event EventHandler<Device>? OnDeviceFound;
    public event EventHandler<Device>? OnDeviceLost;

    /// <summary>Optional limit for a discovery run; null (default) scans until <see cref="StopDiscoveryAsync"/>.
    /// Before, the loop silently ended after 30 s, so a device started later was never found.</summary>
    public TimeSpan? MaxDiscoveryDuration { get; set; }

    public ZeroconfDiscovery(ILogger<ZeroconfDiscovery>? logger = null, FileBridgeOptions? options = null)
    {
        _logger = logger;
        _localDeviceId = options?.DeviceId;
    }

    /// <summary>True while a discovery run is active.</summary>
    public bool IsScanning => _discoveryLoop is { IsCompleted: false };

    public async Task StartDiscoveryAsync(CancellationToken ct = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ZeroconfDiscovery));

        await _startStopGate.WaitAsync(CancellationToken.None);
        try
        {
            if (_discoveryLoop is { IsCompleted: false })
                return; // already scanning (e.g. hosted service + Refresh)

            // A new run reports everything again: a page created after the first run starts empty.
            lock (_seenLock)
                _seenDevices.Clear();

            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _discoveryLoop = DiscoveryLoopAsync(_cts.Token);
        }
        finally
        {
            _startStopGate.Release();
        }

        _logger?.LogInformation("mDNS discovery started");
    }

    public async Task StopDiscoveryAsync()
    {
        await _startStopGate.WaitAsync();
        try
        {
            _cts?.Cancel();
            if (_discoveryLoop != null)
            {
                try { await _discoveryLoop; } catch { /* ignore */ }
            }

            _cts?.Dispose();
            _cts = null;
            _discoveryLoop = null;
        }
        finally
        {
            _startStopGate.Release();
        }
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
        var endTime = MaxDiscoveryDuration is { } max ? DateTime.UtcNow + max : DateTime.MaxValue;

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

        _logger?.LogInformation("mDNS scan: {Count} response(s)", responses.Count);

        foreach (var response in responses)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var device = ParseHostToDevice(response);
                if (device == null)
                    continue;

                if (_localDeviceId is { } self && device.Id == self)
                    continue; // our own advertisement

                var endpoint = $"{device.IpAddress}:{device.Port}";
                var isNewOrMoved = false;
                lock (_seenLock)
                {
                    var key = device.Id.ToString();
                    if (!_seenDevices.TryGetValue(key, out var previous) || previous != endpoint)
                    {
                        _seenDevices[key] = endpoint;
                        isNewOrMoved = true;
                    }
                }

                if (isNewOrMoved)
                {
                    _deviceChannel.Writer.TryWrite(device);
                    OnDeviceFound?.Invoke(this, device);
                    _logger?.LogInformation("Device discovered: {Name} at {Endpoint} (advertised: {All})",
                        device.Name, endpoint, string.Join(", ", response.IPAddresses ?? Array.Empty<string>()));
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
        // A PC advertises every adapter (Wi-Fi, vEthernet/WSL, VirtualBox, VPN...). Prefer an IPv4 address
        // in one of our own subnets instead of whichever came first.
        var candidates = host.IPAddresses ?? Array.Empty<string>();
        var ipAddress = LocalNetwork.ChooseBestAddress(candidates)
                        ?? candidates.FirstOrDefault(a => !string.IsNullOrEmpty(a) && a != "127.0.0.1" && a != "::1");
        if (string.IsNullOrEmpty(ipAddress))
            return null;

        // Get service info
        var services = host.Services;
        if (services == null || services.Count == 0)
            return null;

        var service = services.Values.FirstOrDefault();
        if (service == null || service.Port <= 0)
            return null;

        // Get device name from DisplayName
        var deviceName = host.DisplayName;
        if (string.IsNullOrWhiteSpace(deviceName))
            deviceName = "Unknown Device";

        // Properties: IReadOnlyList<IReadOnlyDictionary<string, string>>
        // Each dictionary entry contains a single key-value pair
        var props = service.Properties;
        string? deviceIdStr = null;

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

        // Without a stable id every scan would create a "new" device: derive one from the endpoint instead.
        if (!Guid.TryParse(deviceIdStr, out var deviceId))
            deviceId = DeterministicGuid($"{ipAddress}:{service.Port}");

        return new Device
        {
            Id = deviceId,
            Name = deviceName,
            IpAddress = ipAddress,
            Port = service.Port,
            LastSeenAt = DateTimeOffset.UtcNow
        };
    }

    private static Guid DeterministicGuid(string value)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { /* already stopped */ }
        _deviceChannel.Writer.TryComplete();
    }
}
