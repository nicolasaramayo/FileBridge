using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileBridge.Domain.Interfaces;
using FileBridge.UI.Services;
using System.Collections.ObjectModel;
using DomainDevice = FileBridge.Domain.Entities.Device;

namespace FileBridge.UI.ViewModels;

public partial class PairingViewModel : ViewModelBase
{
    private readonly IPairingService _pairingService;
    private readonly INavigationService _navigationService;
    private DomainDevice? _deviceToPair;
    private string? _pairingCode;

    [ObservableProperty]
    private bool _isGeneratingCode;

    [ObservableProperty]
    private string _statusMessage = "Generate a code to pair devices";

    [ObservableProperty]
    private string _pairingCodeDisplay = string.Empty;

    [ObservableProperty]
    private bool _isPaired;

    [ObservableProperty]
    private string _deviceName = string.Empty;

    public PairingViewModel(IPairingService pairingService, INavigationService navigationService)
    {
        _pairingService = pairingService;
        _navigationService = navigationService;
        _pairingService.OnPairingCompleted += OnPairingCompleted;
    }

    public void SetDevice(DomainDevice device, string? code)
    {
        _deviceToPair = device;
        DeviceName = device.Name;
        if (!string.IsNullOrEmpty(code))
        {
            _pairingCode = code;
            PairingCodeDisplay = code;
            StatusMessage = $"Pairing code for {device.Name}";
        }
        else
        {
            StatusMessage = $"Generate a code to pair with {device.Name}";
        }
    }

    public void InitializeWithCode(string code)
    {
        _pairingCode = code;
        PairingCodeDisplay = code;
        StatusMessage = "Share this code with the other device";
    }

    private void OnPairingCompleted(object? sender, DomainDevice device)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            IsPaired = true;
            StatusMessage = $"Successfully paired with {device.Name}!";
        });
    }

    [RelayCommand]
    private async Task GenerateCodeAsync()
    {
        if (_deviceToPair == null) return;

        IsGeneratingCode = true;
        StatusMessage = "Generating pairing code...";

        try
        {
            _pairingCode = await _pairingService.GeneratePairingCodeAsync();
            PairingCodeDisplay = _pairingCode;
            StatusMessage = $"Share this code with {DeviceName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            SetError(ex.Message);
        }
        finally
        {
            IsGeneratingCode = false;
        }
    }

    [RelayCommand]
    private async Task ValidateCodeAsync(string? code)
    {
        if (_deviceToPair == null || string.IsNullOrEmpty(code)) return;

        IsGeneratingCode = true;
        StatusMessage = "Validating pairing code...";

        try
        {
            var success = await _pairingService.ValidatePairingCodeAsync(code, _deviceToPair);
            if (success)
            {
                StatusMessage = "Pairing successful!";
                IsPaired = true;
            }
            else
            {
                StatusMessage = "Pairing failed. Please try again.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            SetError(ex.Message);
        }
        finally
        {
            IsGeneratingCode = false;
        }
    }

    [RelayCommand]
    private async Task GoBackAsync()
    {
        await _navigationService.GoBackAsync();
    }
}
