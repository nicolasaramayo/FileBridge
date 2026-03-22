namespace FileBridge.UI;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();
		RegisterRoutes();
	}

	private static void RegisterRoutes()
	{
		Routing.RegisterRoute("devices", typeof(DeviceListPage));
		Routing.RegisterRoute("files", typeof(FileBrowserPage));
		Routing.RegisterRoute("transfers", typeof(TransferPage));
		Routing.RegisterRoute("settings", typeof(SettingsPage));
		Routing.RegisterRoute("pairing", typeof(PairingPage));
	}
}
