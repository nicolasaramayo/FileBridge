using FileBridge.Infrastructure.Network;
using FileBridge.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FileBridge.UI.Services;

/// <summary>
/// Re-announces this device over mDNS when its IPv4 addresses change. The mDNS records carry the addresses
/// this device had when advertising started, and the mDNS library only re-scans network interfaces on its
/// own on Windows, so the phone kept announcing a stale IP. MAUI's ConnectivityChanged only fires when the
/// access level or connection type changes (not for a new DHCP lease on the same Wi-Fi), so the addresses
/// are also compared when the app comes back on screen and every minute while it is visible.
/// </summary>
public sealed class NetworkChangeWatcher : IMauiInitializeService
{
	private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(3);
	private static readonly TimeSpan PeriodicCheck = TimeSpan.FromMinutes(1);

	private ITransferBackgroundService? _transfers;
	private AppVisibility? _visibility;
	private ILogger? _logger;
	private readonly object _sync = new();
	private CancellationTokenSource? _pending;
	private string _lastAddresses = string.Empty;

	public void Initialize(IServiceProvider services)
	{
		_transfers = services.GetRequiredService<ITransferBackgroundService>();
		_visibility = services.GetRequiredService<AppVisibility>();
		_logger = services.GetService<ILogger<NetworkChangeWatcher>>();
		_lastAddresses = CurrentAddresses();

		try
		{
			Connectivity.Current.ConnectivityChanged += (_, _) => ScheduleCheck();
		}
		catch (Exception ex)
		{
			_logger?.LogWarning(ex, "Network change notifications are not available; relying on periodic checks");
		}

		_visibility.Changed += (_, onScreen) =>
		{
			if (onScreen)
				ScheduleCheck();
		};

		_ = PeriodicCheckAsync();
	}

	private async Task PeriodicCheckAsync()
	{
		while (true)
		{
			await Task.Delay(PeriodicCheck);
			if (_visibility is { IsOnScreen: true })
				await CheckAsync();
		}
	}

	/// <summary>Events come in bursts and the new IP may not be assigned yet: wait, then check once.</summary>
	private async void ScheduleCheck()
	{
		var current = new CancellationTokenSource();
		var previous = Interlocked.Exchange(ref _pending, current);
		previous?.Cancel();
		previous?.Dispose();

		try
		{
			await Task.Delay(SettleDelay, current.Token);
			await CheckAsync();
		}
		catch (OperationCanceledException)
		{
			// superseded by a newer change
		}
	}

	private async Task CheckAsync()
	{
		try
		{
			var addresses = CurrentAddresses();
			string previous;
			lock (_sync)
			{
				if (addresses == _lastAddresses)
					return;
				previous = _lastAddresses;
				// Also recorded when empty (offline): coming back with the same IP is then a change too.
				_lastAddresses = addresses;
			}

			if (addresses.Length == 0 || _transfers is null)
				return;

			_logger?.LogInformation("IPv4 addresses changed ({Old} -> {New}); announcing this device again", previous, addresses);
			await _transfers.RestartAdvertisingAsync(); // logs and swallows its own errors; raises StatusChanged
		}
		catch (Exception ex)
		{
			_logger?.LogWarning(ex, "Could not refresh mDNS after a network change");
		}
	}

	private static string CurrentAddresses() =>
		string.Join(",", LocalNetwork.GetLocalIPv4Addresses().Select(a => a.Address.ToString()).Distinct().OrderBy(a => a, StringComparer.Ordinal));
}
