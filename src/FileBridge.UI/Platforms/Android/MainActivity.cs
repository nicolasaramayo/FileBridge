using Android.App;
using Android.Content.PM;
using Android.OS;

namespace FileBridge.UI;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
	protected override void OnResume()
	{
		base.OnResume();
		// Discoverable (and able to discover) while the app is on screen.
		MulticastLockHolder.Acquire(this);
	}

	protected override void OnStop()
	{
		// Not visible any more: let the Wi-Fi radio save power. TCP transfers keep working by IP.
		MulticastLockHolder.Release();
		base.OnStop();
	}
}
