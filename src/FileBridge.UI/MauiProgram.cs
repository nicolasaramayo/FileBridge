using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
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

#if WINDOWS
		// MAUI turns "window hidden" into Window.Stopped but raises nothing when it becomes visible again without
		// focus (only activation counts), so tell AppVisibility directly.
		builder.ConfigureLifecycleEvents(lifecycle => lifecycle.AddWindows(windows => windows.OnVisibilityChanged((_, args) =>
		{
			if (args.Visible)
				(IPlatformApplication.Current?.Services.GetService(typeof(AppVisibility)) as AppVisibility)?.ReportVisible(true);
		})));
#endif

#if DEBUG
		// Network-layer logs now go through ILogger<T>: visible in the Output window / logcat.
		builder.Logging.AddDebug();
		builder.Logging.SetMinimumLevel(LogLevel.Debug);
#endif

		var dbPath = Path.Combine(FileSystem.AppDataDirectory, "filebridge.db");
		builder.Services.AddFileBridgeInfrastructureServices(dbPath, options =>
		{
			options.DeviceName = GetDeviceName();
			options.DeviceId = GetOrCreateDeviceId();
			options.ReceivedFilesDirectory = GetReceivedFilesDirectory();
		});
		builder.Services.AddSingleton<INavigationService, NavigationService>();
		builder.Services.AddSingleton<AppVisibility>(); // App tracks its window; prompt, scanning and network checks read it

		// Run at MauiApp.Build(), in this order. MAUI does not start IHostedService by itself (see HostedServicesStarter);
		// the prompt is registered first so the server never runs without someone to ask (without it, every file is declined).
		builder.Services.AddSingleton<IMauiInitializeService, IncomingTransferPrompt>();
		builder.Services.AddSingleton<IMauiInitializeService, NetworkChangeWatcher>();
		builder.Services.AddSingleton<IMauiInitializeService, HostedServicesStarter>();

		// Register ViewModels. Main and DeviceList hold app-wide state and subscribe to singleton services,
		// so they are singletons too (as transients, each page visit left a live subscriber behind).
		builder.Services.AddSingleton<MainViewModel>();
		builder.Services.AddSingleton<DeviceListViewModel>();
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

	/// <summary>
	/// Same id across restarts: with a new one per launch, every restart of a peer showed up as a new device.
	/// </summary>
	private static Guid GetOrCreateDeviceId()
	{
		const string key = "filebridge_device_id";
		try
		{
			if (Guid.TryParse(Preferences.Default.Get(key, string.Empty), out var stored))
				return stored;

			var id = Guid.NewGuid();
			Preferences.Default.Set(key, id.ToString());
			return id;
		}
		catch
		{
			return Guid.NewGuid();
		}
	}

	private static string GetReceivedFilesDirectory()
	{
#if WINDOWS
		return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "FileBridge");
#elif ANDROID
		// App-specific external storage (Android/data/<package>/files/Download): no storage permission needed,
		// reachable over USB, and MAUI's FileProvider can open files there directly. Files in the internal
		// app folder are first copied synchronously on the UI thread by Launcher.OpenAsync (ANR on big files).
		var downloads = global::Android.App.Application.Context.GetExternalFilesDir(global::Android.OS.Environment.DirectoryDownloads);
		return downloads?.AbsolutePath is { Length: > 0 } path
			? path
			: Path.Combine(FileSystem.AppDataDirectory, "Received");
#else
		return Path.Combine(FileSystem.AppDataDirectory, "Received");
#endif
	}
}
