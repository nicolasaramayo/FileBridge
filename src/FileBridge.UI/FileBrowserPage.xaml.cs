namespace FileBridge.UI;

using FileBridge.Domain.Entities;
using FileBridge.UI.ViewModels;

public partial class FileBrowserPage : ContentPage
{
    public FileBrowserPage(FileBrowserViewModel viewModel)
    {
        Microsoft.Maui.Controls.Xaml.Extensions.LoadFromXaml(this, typeof(FileBrowserPage));
        BindingContext = viewModel;
    }

    // Partial method declaration - implementation provided by source generator or XamlC.
    partial void InitializeComponent();

    private void OnFileTapped(object? sender, ItemTappedEventArgs e)
    {
        if (e.Item is FileItem file && BindingContext is FileBrowserViewModel vm)
        {
            vm.SelectFileCommand.Execute(file);
        }
    }
}
