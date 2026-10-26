namespace FileBridge.UI;

using FileBridge.UI.Services;
using Application = Microsoft.Maui.Controls.Application;

public partial class App : Application
{
	private readonly AppVisibility _visibility;

	// App is resolved through DI (UseMauiApp registers it as IApplication), so it can take services.
	public App(AppVisibility visibility)
	{
		InitializeComponent();
		_visibility = visibility;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new AppShell());
		_visibility.Track(window);
		return window;
	}
}
