namespace FileBridge.UI;

using FileBridge.UI.ViewModels;

public partial class SettingsPage : ContentPage
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        Microsoft.Maui.Controls.Xaml.Extensions.LoadFromXaml(this, typeof(SettingsPage));
        BindingContext = viewModel;
    }

    // Partial method declaration - implementation provided by source generator or XamlC.
    partial void InitializeComponent();
}
