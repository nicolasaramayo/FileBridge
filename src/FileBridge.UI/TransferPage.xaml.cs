namespace FileBridge.UI;

using FileBridge.UI.ViewModels;

public partial class TransferPage : ContentPage
{
    public TransferPage(TransferViewModel viewModel)
    {
        Microsoft.Maui.Controls.Xaml.Extensions.LoadFromXaml(this, typeof(TransferPage));
        BindingContext = viewModel;
    }

    // Partial method declaration - implementation provided by source generator or XamlC.
    partial void InitializeComponent();

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (BindingContext is TransferViewModel vm)
        {
            await vm.LoadHistoryCommand.ExecuteAsync(null);
        }
    }
}
