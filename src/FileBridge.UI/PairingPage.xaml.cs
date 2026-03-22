namespace FileBridge.UI;

using FileBridge.Domain.Entities;
using FileBridge.UI.ViewModels;

public partial class PairingPage : ContentPage
{
    public PairingPage(PairingViewModel viewModel)
    {
        Microsoft.Maui.Controls.Xaml.Extensions.LoadFromXaml(this, typeof(PairingPage));
        BindingContext = viewModel;
    }

    // Partial method declaration - implementation provided by source generator or XamlC.
    partial void InitializeComponent();

    protected override void OnAppearing()
    {
        base.OnAppearing();
        
        if (BindingContext is PairingViewModel vm)
        {
            // Handle navigation parameters
            if (Shell.Current?.Navigation?.NavigationStack.LastOrDefault() is ContentPage prevPage)
            {
                if (prevPage.BindingContext is DeviceListViewModel deviceListVm)
                {
                    // Already handled in navigation
                }
            }
        }
    }
}
