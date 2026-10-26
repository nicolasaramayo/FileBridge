using FileBridge.Infrastructure.Network;
using FileBridge.UI.Converters;

namespace FileBridge.UI.Services;

/// <summary>
/// Accept/Decline for an incoming file, shown as a modal page (not DisplayAlert) so it can be closed from
/// code when the offer times out or the sender disconnects: a DisplayAlert stays on screen, and on Windows
/// alerts are queued, so a stale one blocked the next offer's prompt.
/// </summary>
internal sealed class IncomingFilePromptPage : ContentPage
{
	private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

	public Task<bool> Result => _result.Task;

	public IncomingFilePromptPage(IncomingFileOffer offer)
	{
		Title = "Incoming file";

		// The name comes from the sender's handshake and is not verified: always show its IP too.
		var ip = offer.RemoteEndpoint ?? "unknown address";
		var colon = ip.LastIndexOf(':');
		if (colon > 0)
			ip = ip[..colon]; // "192.168.1.5:51234" -> "192.168.1.5"
		var from = string.IsNullOrWhiteSpace(offer.PeerName) ? ip : $"{offer.PeerName} ({ip})";

		var accept = new Button { Text = "Accept" };
		var decline = new Button { Text = "Decline" };
		accept.Clicked += (_, _) => Close(true);
		decline.Clicked += (_, _) => Close(false);

		Content = new VerticalStackLayout
		{
			Padding = 24,
			Spacing = 12,
			VerticalOptions = LayoutOptions.Center,
			Children =
			{
				new Label { Text = "Incoming file", FontSize = 22, FontAttributes = FontAttributes.Bold },
				new Label { Text = $"{from} wants to send you:", LineBreakMode = LineBreakMode.WordWrap },
				new Label { Text = offer.FileName, FontSize = 16, FontAttributes = FontAttributes.Bold, LineBreakMode = LineBreakMode.MiddleTruncation },
				new Label { Text = BytesToHumanReadableConverter.BytesToHumanReadable(offer.Size), Opacity = 0.7 },
				new HorizontalStackLayout { Spacing = 12, Margin = new Thickness(0, 12, 0, 0), Children = { decline, accept } }
			}
		};
	}

	/// <summary>Android Back = Decline.</summary>
	protected override bool OnBackButtonPressed()
	{
		Close(false);
		return true;
	}

	/// <summary>Answers once and removes the page. Safe to call from any thread and more than once.</summary>
	public void Close(bool accepted)
	{
		if (!_result.TrySetResult(accepted))
			return;

		MainThread.BeginInvokeOnMainThread(async () =>
		{
			try
			{
				if (Navigation.ModalStack.LastOrDefault() == this)
					await Navigation.PopModalAsync(false);
			}
			catch
			{
				// the window is gone; nothing to close
			}
		});
	}
}
