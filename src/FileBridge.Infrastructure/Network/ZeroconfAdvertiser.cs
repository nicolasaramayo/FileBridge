namespace FileBridge.Infrastructure.Network;

using FileBridge.Domain.Entities;
using FileBridge.Domain.Interfaces;
using Makaretu.Dns;
using Microsoft.Extensions.Logging;

public sealed class ZeroconfAdvertiser : IDiscoveryService, IDisposable
{
    private const string ServiceType = "_filetransfer._tcp"; 
    private const int BroadcastIntervalSeconds = 60;

    private CancellationTokenSource? _cts;
    private readonly ILogger? _logger;
    private string? _deviceName;
    private int _port;
    private string? _deviceId;
    private MulticastService? _mdns;
    private ServiceDiscovery? _sd;
    private ServiceProfile? _profile;

    private bool _disposed;

    public event EventHandler<Device>? OnDeviceFound;
    public event EventHandler<Device>? OnDeviceLost;

    public ZeroconfAdvertiser(ILogger<ZeroconfAdvertiser>? logger = null)
    {
        _logger = logger;
    }

    public async Task StartDiscoveryAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_deviceName))
        {
            _logger?.LogWarning("StartDiscoveryAsync called before parameters were set. Advertising will not start.");
            return;
        }
        await StartAdvertisingAsync(_deviceName, _port, _deviceId ?? Guid.NewGuid().ToString(), ct);
    }

    public async Task StartAdvertisingAsync(string deviceName, int port, string deviceId, CancellationToken ct = default)
    {
        _deviceName = deviceName;
        _port = port;
        _deviceId = deviceId;
        if (_disposed)
            throw new ObjectDisposedException(nameof(ZeroconfAdvertiser));

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        
        try
        {
            _mdns = new MulticastService();
            _sd = new ServiceDiscovery(_mdns);
            
            // Name must be unique on network and a valid DNS label (<= 63 bytes, no dots).
            string instanceName = BuildInstanceName(_deviceName, _deviceId);
            
            _profile = new ServiceProfile(instanceName, ServiceType, (ushort)_port);
            _profile.AddProperty("name", _deviceName.Length > 100 ? _deviceName[..100] : _deviceName);
            _profile.AddProperty("port", _port.ToString());
            _profile.AddProperty("version", "1");
            _profile.AddProperty("deviceId", _deviceId);

            _sd.Advertise(_profile);
            _mdns.Start();

            _logger?.LogInformation(
                "mDNS advertiser: Makaretu.Dns started for service '{Name}' on port {Port}",
                _deviceName, _port);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to start mDNS advertiser");
            throw;
        }

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

        try
        {
            if (_sd != null && _profile != null)
            {
                _sd.Unadvertise(_profile);
                _sd.Dispose();
                _sd = null;
            }
            if (_mdns != null)
            {
                _mdns.Stop();
                _mdns.Dispose();
                _mdns = null;
            }
            _logger?.LogInformation("mDNS advertiser stopped");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error while stopping mDNS advertiser");
        }
        
        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<Device> GetDiscoveredDevicesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    /// <summary>
    /// "{name}-{deviceId}" as a single DNS label: only ASCII letters/digits/'-', at most 63 bytes.
    /// A device name with dots, accents or more than ~26 characters (common on phones) produced an
    /// invalid label before.
    /// </summary>
    public static string BuildInstanceName(string? deviceName, string deviceId)
    {
        const int MaxLabelLength = 63;
        var cleaned = new string((deviceName ?? string.Empty)
            .Select(c => c < 128 && char.IsLetterOrDigit(c) ? c : '-')
            .ToArray()).Trim('-');
        if (cleaned.Length == 0)
            cleaned = "FileBridge";

        var maxNameLength = MaxLabelLength - 1 - deviceId.Length;
        if (maxNameLength < 1)
            return deviceId.Length <= MaxLabelLength ? deviceId : deviceId[..MaxLabelLength];
        if (cleaned.Length > maxNameLength)
            cleaned = cleaned[..maxNameLength].TrimEnd('-');

        return $"{cleaned}-{deviceId}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAdvertisingAsync().GetAwaiter().GetResult();
    }
}
