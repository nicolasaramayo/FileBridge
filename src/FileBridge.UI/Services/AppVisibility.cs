namespace FileBridge.UI.Services;

/// <summary>
/// Whether the app is on screen. MAUI raises Window.Activated when it comes back (Android OnResume,
/// Windows focus) and Window.Stopped when it is no longer visible (Android OnStop, Windows minimized);
/// pages do not get OnDisappearing in that case, so page-level code cannot rely on it.
/// </summary>
public sealed class AppVisibility
{
	private volatile bool _isOnScreen;

	public bool IsOnScreen => _isOnScreen;

	/// <summary>Raised with the new value when the app goes to the background or comes back.</summary>
	public event EventHandler<bool>? Changed;

	public void Track(Window window)
	{
		window.Created += (_, _) => Set(true);
		window.Activated += (_, _) => Set(true);
		window.Resumed += (_, _) => Set(true);
		window.Stopped += (_, _) => Set(false);
		window.Destroying += (_, _) => Set(false);
	}

	private void Set(bool onScreen)
	{
		if (_isOnScreen == onScreen)
			return;
		_isOnScreen = onScreen;
		Changed?.Invoke(this, onScreen);
	}
}
