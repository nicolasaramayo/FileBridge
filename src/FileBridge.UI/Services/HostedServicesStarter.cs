using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FileBridge.UI.Services;

/// <summary>
/// .NET MAUI builds a DI container but, unlike the generic host, never calls IHostedService.StartAsync
/// (MauiAppBuilder.Build only runs IMauiInitializeService). Without this, TransferBackgroundService
/// (TCP server + mDNS advertising) and DeviceDiscoveryHostedService never ran on the device.
/// </summary>
public sealed class HostedServicesStarter : IMauiInitializeService
{
	public void Initialize(IServiceProvider services)
	{
		var logger = services.GetService<ILogger<HostedServicesStarter>>();
		foreach (var hostedService in services.GetServices<IHostedService>())
		{
			_ = StartAsync(hostedService, logger);
		}
	}

	private static async Task StartAsync(IHostedService service, ILogger? logger)
	{
		try
		{
			// Off the UI thread: binding the socket and starting mDNS should not delay startup.
			await Task.Run(() => service.StartAsync(CancellationToken.None));
			logger?.LogInformation("Started {Service}", service.GetType().Name);
		}
		catch (Exception ex)
		{
			logger?.LogError(ex, "Could not start {Service}", service.GetType().Name);
		}
	}
}
