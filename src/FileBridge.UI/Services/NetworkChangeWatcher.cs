using FileBridge.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FileBridge.UI.Services;

/// <summary>
/// Re-announces this device over mDNS when the network changes (new Wi-Fi, new IP from DHCP, mobile data
/// to Wi-Fi). The mDNS records carry the addresses this device had when advertising started, and the
/// mDNS library only re-scans network interfaces on its own on Windows, so the phone kept announcing
/// a stale IP.
/// </summary>
public sealed class NetworkChangeWatcher : IMauiInitializeService
{
	private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(3);

	private ITransferBackgroundService? _transfers;
	private ILogger? _logger;
	private CancellationTokenSource? _pending;

	public void Initialize(IServiceProvider services)
	{
		_transfers = services.GetRequiredService<ITransferBackgroundService>();
		_logger = services.GetService<ILogger<NetworkChangeWatcher>>();
		try
		{
			Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;
		}
		catch (Exception ex)
		{
			_logger?.LogWarning(ex, "Network change notifications are not available; mDNS will not be refreshed automatically");
		}
	}

	private async void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
	{
		// Events come in bursts and the new IP may not be assigned yet: wait, and only act on the last one.
		var current = new CancellationTokenSource();
		var previous = Interlocked.Exchange(ref _pending, current);
		previous?.Cancel();
		previous?.Dispose();

		try
		{
			await Task.Delay(SettleDelay, current.Token);
			if (e.NetworkAccess == NetworkAccess.None || _transfers is null)
				return;

			_logger?.LogInformation("Network changed ({Profiles}); announcing this device again",
				string.Join(", ", e.ConnectionProfiles));
			await _transfers.RestartAdvertisingAsync(); // logs and swallows its own errors
		}
		catch (OperationCanceledException)
		{
			// superseded by a newer change
		}
		catch (Exception ex)
		{
			_logger?.LogWarning(ex, "Could not refresh mDNS after a network change");
		}
	}
}
