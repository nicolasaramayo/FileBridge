using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileBridge.UI.Services;

namespace FileBridge.UI.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly INavigationService _navigation;
    
    public MainViewModel(INavigationService navigation)
    {
        _navigation = navigation;
    }
    
    [RelayCommand]
    private async Task OpenDevices() => await _navigation.GoToAsync("devices");
    
    [RelayCommand]
    private async Task OpenTransfers() => await _navigation.GoToAsync("transfers");
    
    [RelayCommand]
    private async Task OpenSettings() => await _navigation.GoToAsync("settings");
}
