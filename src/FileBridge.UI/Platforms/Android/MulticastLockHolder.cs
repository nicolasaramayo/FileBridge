using Android.Content;
using Android.Net.Wifi;

namespace FileBridge.UI;

/// <summary>
/// mDNS (Zeroconf discovery + Makaretu advertising) needs a Wi-Fi MulticastLock on Android: without it the
/// Wi-Fi stack drops multicast packets, so the phone neither finds the PC nor answers its queries
/// (the Zeroconf README requires it; the manifest already has CHANGE_WIFI_MULTICAST_STATE).
/// It is held only while the app is visible, because it keeps the Wi-Fi radio from saving power.
/// </summary>
internal static class MulticastLockHolder
{
	private static readonly object Sync = new();
	private static WifiManager.MulticastLock? _lock;

	public static void Acquire(Context context)
	{
		lock (Sync)
		{
			try
			{
				if (_lock is null)
				{
					var wifi = context.ApplicationContext?.GetSystemService(Context.WifiService) as WifiManager;
					_lock = wifi?.CreateMulticastLock("FileBridge.mDNS");
					_lock?.SetReferenceCounted(false);
				}

				if (_lock is { IsHeld: false })
					_lock.Acquire();
			}
			catch (Exception ex)
			{
				global::Android.Util.Log.Warn("FileBridge", $"Could not acquire the multicast lock: {ex.Message}");
			}
		}
	}

	public static void Release()
	{
		lock (Sync)
		{
			try
			{
				if (_lock is { IsHeld: true })
					_lock.Release();
			}
			catch (Exception ex)
			{
				global::Android.Util.Log.Warn("FileBridge", $"Could not release the multicast lock: {ex.Message}");
			}
		}
	}
}
