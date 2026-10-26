using FileBridge.Infrastructure.Network;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FileBridge.UI.Services;

/// <summary>
/// Asks the user before anything is written: the transfer server listens on the whole LAN, so without
/// this any device on the same Wi-Fi could drop files on this one. FileReceiver asks one offer at a time,
/// and cancels the token on timeout (FileBridgeOptions.IncomingApprovalTimeout) or when the sender
/// disconnects; the prompt closes then and the file is declined.
/// </summary>
public sealed class IncomingTransferPrompt : IMauiInitializeService
{
	private AppVisibility? _visibility;
	private ILogger? _logger;

	public void Initialize(IServiceProvider services)
	{
		_logger = services.GetService<ILogger<IncomingTransferPrompt>>();
		_visibility = services.GetRequiredService<AppVisibility>();
		services.GetRequiredService<FileReceiver>().ApproveIncoming = AskAsync;
	}

	private async Task<IncomingFileDecision> AskAsync(IncomingFileOffer offer, CancellationToken ct)
	{
		// In the background nobody would see the prompt: tell the sender right away instead of timing out.
		if (_visibility is { IsOnScreen: false })
		{
			_logger?.LogInformation("App not on screen; not asking about {File}", offer.FileName);
			return IncomingFileDecision.Unavailable;
		}

		return await MainThread.InvokeOnMainThreadAsync(async () =>
		{
			var host = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
			if (host is null)
				return IncomingFileDecision.Unavailable;

			var prompt = new IncomingFilePromptPage(offer);
			await host.Navigation.PushModalAsync(prompt, false);

			// Timeout or sender gone: close the prompt (answering it later would do nothing).
			using var registration = ct.Register(() => prompt.Close(false));
			return await prompt.Result ? IncomingFileDecision.Accepted : IncomingFileDecision.Declined;
		});
	}
}
