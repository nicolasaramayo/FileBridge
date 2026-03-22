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
		builder.Logging.AddDebug();
#endif

		var dbPath = Path.Combine(FileSystem.AppDataDirectory, "filebridge.db");
		builder.Services.AddFileBridgeInfrastructureServices(dbPath);
		builder.Services.AddSingleton<INavigationService, NavigationService>();
		
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
}
