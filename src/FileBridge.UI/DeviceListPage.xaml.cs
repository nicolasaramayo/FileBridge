namespace FileBridge.UI;

using FileBridge.Domain.Entities;
using FileBridge.UI.ViewModels;

public partial class DeviceListPage : ContentPage
{
    public DeviceListPage(DeviceListViewModel viewModel)
    {
        Microsoft.Maui.Controls.Xaml.Extensions.LoadFromXaml(this, typeof(DeviceListPage));
        BindingContext = viewModel;
    }

    // Partial method declaration - implementation provided by source generator or XamlC.
    // If neither provides it, partial methods are no-ops at runtime.
    partial void InitializeComponent();

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // mDNS scanning runs only while this page is visible (the view model catches its own errors).
        if (BindingContext is DeviceListViewModel vm)
            await vm.StartScanningAsync();
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        if (BindingContext is DeviceListViewModel vm)
            await vm.StopScanningAsync();
    }

    private void OnDeviceTapped(object? sender, ItemTappedEventArgs e)
    {
        if (e.Item is Device device && BindingContext is DeviceListViewModel vm)
        {
            vm.SelectDeviceCommand.Execute(device);
        }
    }

    private void OnDeviceActionClicked(object? sender, EventArgs e)
    {
        if (sender is Button button && button.BindingContext is Device device && BindingContext is DeviceListViewModel vm)
        {
            vm.SelectDeviceCommand.Execute(device);
        }
    }
}
