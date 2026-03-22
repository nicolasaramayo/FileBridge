using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileBridge.Application.Services;
using FileBridge.Domain.Entities;
using FileBridge.Domain.Interfaces;
using FileBridge.UI.Services;
using System.Collections.ObjectModel;
using DomainDevice = FileBridge.Domain.Entities.Device;

namespace FileBridge.UI.ViewModels;

public partial class DeviceListViewModel : ViewModelBase
{
    private readonly DeviceDiscoveryService _discoveryService;
    private readonly INavigationService _navigationService;
    private readonly IPairingService _pairingService;
    private CancellationTokenSource? _discoveryCts;

    [ObservableProperty]
    private ObservableCollection<DomainDevice> _devices = new();

    [ObservableProperty]
    private bool _isRefreshing;

    public DeviceListViewModel(
        DeviceDiscoveryService discoveryService,
        INavigationService navigationService,
        IPairingService pairingService)
    {
        _discoveryService = discoveryService;
        _navigationService = navigationService;
        _pairingService = pairingService;

        _discoveryService.OnDeviceFound += OnDeviceFound;
        _discoveryService.OnDeviceLost += OnDeviceLost;
    }

    private void OnDeviceFound(object? sender, DomainDevice device)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!Devices.Any(d => d.Id == device.Id))
            {
                Devices.Add(device);
            }
        });
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
    private async Task SelectDevice(DomainDevice? device)
    {
        if (device == null) return;

        // Check if device is paired (PairedAt is not default)
        bool isPaired = device.PairedAt != default;

        var parameters = new Dictionary<string, object>
        {
            { "device", device }
        };

        if (isPaired)
        {
            // Navigate to file browser for paired devices
            await _navigationService.GoToAsync("//files", parameters);
        }
        else
        {
            // Navigate to pairing page for unpaired devices
            await _navigationService.GoToAsync("//pairing", parameters);
        }
    }

    [RelayCommand]
    private async Task PairDevice(DomainDevice device)
    {
        if (device == null) return;

        try
        {
            var code = await _pairingService.GeneratePairingCodeAsync();
            await _navigationService.GoToAsync("//pairing", new Dictionary<string, object>
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
