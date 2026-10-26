using Microsoft.Extensions.Logging;
using FileBridge.Infrastructure.Services;
using FileBridge.UI.Services;
using FileBridge.UI.ViewModels;

namespace FileBridge.UI;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();

		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

#if DEBUG
		// Network-layer logs now go through ILogger<T>: visible in the Output window / logcat.
		builder.Logging.AddDebug();
		builder.Logging.SetMinimumLevel(LogLevel.Debug);
#endif

		var dbPath = Path.Combine(FileSystem.AppDataDirectory, "filebridge.db");
		builder.Services.AddFileBridgeInfrastructureServices(dbPath, options =>
		{
			options.DeviceName = GetDeviceName();
			options.ReceivedFilesDirectory = GetReceivedFilesDirectory();
		});
		builder.Services.AddSingleton<INavigationService, NavigationService>();

		// MAUI does not start IHostedService by itself (see HostedServicesStarter).
		builder.Services.AddSingleton<IMauiInitializeService, HostedServicesStarter>();

		// Register ViewModels
		builder.Services.AddTransient<MainViewModel>();
		builder.Services.AddTransient<DeviceListViewModel>();
		builder.Services.AddTransient<PairingViewModel>();
		builder.Services.AddTransient<FileBrowserViewModel>();
		builder.Services.AddTransient<TransferViewModel>();
		builder.Services.AddTransient<SettingsViewModel>();

		// Register Pages
		builder.Services.AddTransient<MainPage>();
		builder.Services.AddTransient<DeviceListPage>();
		builder.Services.AddTransient<PairingPage>();
		builder.Services.AddTransient<FileBrowserPage>();
		builder.Services.AddTransient<TransferPage>();
		builder.Services.AddTransient<SettingsPage>();

		return builder.Build();
	}

	private static string GetDeviceName()
	{
		// On Android Environment.MachineName is "localhost"; DeviceInfo gives the name the user recognizes.
		try
		{
			var name = DeviceInfo.Current.Name;
			if (!string.IsNullOrWhiteSpace(name))
				return name;
		}
		catch
		{
			// fall back below
		}
		return Environment.MachineName;
	}

	private static string GetReceivedFilesDirectory()
	{
#if WINDOWS
		return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "FileBridge");
#else
		// App-private on Android (no storage permission needed). The main page shows the path and can open the last file.
		return Path.Combine(FileSystem.AppDataDirectory, "Received");
#endif
	}
}
