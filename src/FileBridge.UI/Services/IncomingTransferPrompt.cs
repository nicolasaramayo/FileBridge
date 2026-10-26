using FileBridge.Infrastructure.Network;
using FileBridge.UI.Converters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FileBridge.UI.Services;

/// <summary>
/// Asks the user before anything is written: the transfer server listens on the whole LAN, so without
/// this any device on the same Wi-Fi could drop files on this one. If the prompt cannot be shown or
/// nobody answers in time (FileBridgeOptions.IncomingApprovalTimeout), the file is declined.
/// </summary>
public sealed class IncomingTransferPrompt : IMauiInitializeService
{
	private ILogger? _logger;

	public void Initialize(IServiceProvider services)
	{
		_logger = services.GetService<ILogger<IncomingTransferPrompt>>();
		services.GetRequiredService<FileReceiver>().ApproveIncoming = AskAsync;
	}

	private Task<bool> AskAsync(IncomingFileOffer offer, CancellationToken ct) =>
		MainThread.InvokeOnMainThreadAsync(async () =>
		{
			var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
			if (page is null)
			{
				_logger?.LogInformation("No window to ask about {File}; declining", offer.FileName);
				return false;
			}

			var from = offer.PeerName ?? offer.RemoteEndpoint ?? "Another device";
			var size = BytesToHumanReadableConverter.BytesToHumanReadable(offer.Size);
			var dialog = page.DisplayAlert(
				"Incoming file",
				$"{from} wants to send you \"{offer.FileName}\" ({size}).",
				"Accept",
				"Decline");

			// If the timeout expires first the file is declined (the dialog cannot be closed from code;
			// answering it later has no effect).
			var timedOut = new TaskCompletionSource<bool>();
			using var registration = ct.Register(() => timedOut.TrySetResult(false));
			var answered = await Task.WhenAny(dialog, timedOut.Task);
			return answered == dialog && await dialog;
		});
}
