using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileBridge.Domain.Interfaces;
using FileBridge.Infrastructure.Persistence;
using DomainDevice = FileBridge.Domain.Entities.Device;

namespace FileBridge.UI.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;

    [ObservableProperty]
    private bool _encryptionEnabled;

    [ObservableProperty]
    private bool _autoDiscover;

    [ObservableProperty]
    private int _defaultChunkSize;

    [ObservableProperty]
    private int _serverPort;

    [ObservableProperty]
    private string _appVersion = "1.0.0";

    public SettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    [RelayCommand]
    private async Task LoadSettingsAsync()
    {
        try
        {
            EncryptionEnabled = await _settingsService.GetBoolAsync("encryption_enabled", false);
            AutoDiscover = await _settingsService.GetBoolAsync("auto_discover", true);
            DefaultChunkSize = await _settingsService.GetIntAsync("default_chunk_size", 65536);
            ServerPort = await _settingsService.GetIntAsync("server_port", 45678);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        try
        {
            await _settingsService.SetAsync("encryption_enabled", EncryptionEnabled);
            await _settingsService.SetAsync("auto_discover", AutoDiscover);
            await _settingsService.SetAsync("default_chunk_size", DefaultChunkSize);
            await _settingsService.SetAsync("server_port", ServerPort);
        }
        catch (Exception ex)
        {
            SetError($"Failed to save settings: {ex.Message}");
        }
    }
}
