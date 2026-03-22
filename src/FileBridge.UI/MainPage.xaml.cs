namespace FileBridge.UI;

using FileBridge.UI.ViewModels;

public partial class MainPage : ContentPage
{
	public MainPage(MainViewModel viewModel)
	{
		InitializeComponent();
		BindingContext = viewModel;
	}
}
