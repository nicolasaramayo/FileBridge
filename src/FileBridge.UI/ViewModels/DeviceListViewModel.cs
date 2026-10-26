using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileBridge.Application.Services;
using FileBridge.Domain.Entities;
using FileBridge.Domain.Interfaces;
using FileBridge.UI.Services;
using System.Collections.ObjectModel;
using DomainDevice = FileBridge.Domain.Entities.Device;

namespace FileBridge.UI.ViewModels;

/// <summary>
/// Registered as a singleton: it subscribes once to the singleton registry/discovery (as a transient,
/// every visit to the page left another live subscriber behind), and the list survives between visits.
/// Scanning only runs while the page is visible (see DeviceListPage.OnAppearing/OnDisappearing).
/// </summary>
public partial class DeviceListViewModel : ViewModelBase
{
    private const int DefaultPort = 45678;

    private readonly DeviceDiscoveryService _discoveryService;
    private readonly INavigationService _navigationService;
    private readonly IPairingService _pairingService;
    private readonly IDeviceRegistry _registry;
    private CancellationTokenSource? _discoveryCts;

    [ObservableProperty]
    private ObservableCollection<DomainDevice> _devices = new();

    [ObservableProperty]
    private bool _isRefreshing;

    /// <summary>"192.168.1.10" or "192.168.1.10:45678" — fallback when mDNS does not work on the network.</summary>
    [ObservableProperty]
    private string _manualAddress = string.Empty;

    public DeviceListViewModel(
        DeviceDiscoveryService discoveryService,
        INavigationService navigationService,
        IPairingService pairingService,
        IDeviceRegistry registry)
    {
        _discoveryService = discoveryService;
        _navigationService = navigationService;
        _pairingService = pairingService;
        _registry = registry;

        // Devices found by the background discovery before this page was opened.
        foreach (var device in _registry.All)
            Devices.Add(device);

        // Every discovered (DeviceDiscoveryService registers them) or manually added device goes through the registry.
        _registry.DeviceAddedOrUpdated += OnDeviceFound;
        _registry.DeviceRemoved += OnDeviceLost; // replaced by a newer entry for the same IP:port
        _discoveryService.OnDeviceLost += OnDeviceLost;
    }

    private void OnDeviceFound(object? sender, DomainDevice device)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var index = Devices.ToList().FindIndex(d => d.Id == device.Id);
            if (index >= 0)
                Devices[index] = device; // address/port may have changed
            else
                Devices.Add(device);
        });
    }

    /// <summary>Called when the page appears: scan while the user is looking at the list.</summary>
    public Task StartScanningAsync() => RefreshAsync();

    /// <summary>Called when the page disappears: stop scanning (battery).</summary>
    public async Task StopScanningAsync()
    {
        try
        {
            _discoveryCts?.Cancel();
            await _discoveryService.StopDiscoveryAsync();
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    private void OnDeviceLost(object? sender, DomainDevice device)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var existing = Devices.FirstOrDefault(d => d.Id == device.Id);
            if (existing != null)
            {
                Devices.Remove(existing);
            }
        });
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsRefreshing = true;
        try
        {
            _discoveryCts?.Cancel();
            _discoveryCts = new CancellationTokenSource();

            // Stop + start makes discovery report every device again.
            await _discoveryService.StopDiscoveryAsync();
            await _discoveryService.StartDiscoveryAsync(_discoveryCts.Token);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    private void AddManualDevice()
    {
        ClearError();
        var text = (ManualAddress ?? string.Empty).Trim();
        var host = text;
        var port = DefaultPort;

        var colon = text.LastIndexOf(':');
        if (colon > 0)
        {
            host = text[..colon];
            if (!int.TryParse(text[(colon + 1)..], out port) || port is <= 0 or > 65535)
            {
                SetError($"Invalid port in '{text}'");
                return;
            }
        }

        // IPAddress.TryParse also accepts short forms ("10" -> 0.0.0.10, "192.168.1" -> 192.168.0.1) and IPv6,
        // but the server listens on IPv4 only: require four dotted parts and store the normalized text.
        if (host.Split('.').Length != 4
            || !System.Net.IPAddress.TryParse(host, out var ip)
            || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            SetError($"'{host}' is not an IPv4 address (example: 192.168.1.10:{DefaultPort})");
            return;
        }

        var address = ip.ToString();
        var existing = _registry.All.FirstOrDefault(d => d.IpAddress == address && d.Port == port);
        _registry.AddOrUpdate(new DomainDevice
        {
            Id = existing?.Id ?? Guid.NewGuid(), // adding the same IP:port twice updates the entry instead of duplicating it
            Name = $"{address}:{port}",
            IpAddress = address,
            Port = port,
            LastSeenAt = DateTimeOffset.UtcNow
        });
        ManualAddress = string.Empty;
    }

    [RelayCommand]
    private async Task SelectDevice(DomainDevice? device)
    {
        if (device == null) return;

        // Pairing is still a placeholder (PairingService accepts any 6-digit code and never talks to the
        // other device), so it no longer blocks the flow: go straight to choosing a file.
        // Relative route: "//files" is an absolute route to a *global* route, which Shell rejects.
        await _navigationService.GoToAsync("files", new Dictionary<string, object>
        {
            { "device", device }
        });
    }

    [RelayCommand]
    private async Task PairDevice(DomainDevice device)
    {
        if (device == null) return;

        try
        {
            var code = await _pairingService.GeneratePairingCodeAsync();
            await _navigationService.GoToAsync("pairing", new Dictionary<string, object>
            {
                { "device", device },
                { "code", code }
            });
        }
        catch (Exception ex)
        {
            SetError($"Pairing failed: {ex.Message}");
        }
    }
}
