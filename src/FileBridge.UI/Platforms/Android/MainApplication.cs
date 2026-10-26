using Android.App;
using Android.Net.Wifi;
using Android.Runtime;

namespace FileBridge.UI;

[Application]
public class MainApplication : MauiApplication
{
	private WifiManager.MulticastLock? _multicastLock;

	public MainApplication(IntPtr handle, JniHandleOwnership ownership)
		: base(handle, ownership)
	{
	}

	public override void OnCreate()
	{
		// mDNS (Zeroconf discovery + Makaretu advertising) needs this on Android: without the lock the
		// Wi-Fi stack drops multicast packets, so the phone neither finds the PC nor answers its queries.
		// The Zeroconf README requires it explicitly. The manifest already has CHANGE_WIFI_MULTICAST_STATE.
		// Acquired before base.OnCreate(), which builds the MauiApp and starts the network services.
		try
		{
			var wifi = GetSystemService(WifiService) as WifiManager;
			_multicastLock = wifi?.CreateMulticastLock("FileBridge.mDNS");
			_multicastLock?.SetReferenceCounted(false);
			_multicastLock?.Acquire();
		}
		catch (Exception ex)
		{
			Android.Util.Log.Warn("FileBridge", $"Could not acquire the multicast lock: {ex.Message}");
		}

		base.OnCreate();
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
