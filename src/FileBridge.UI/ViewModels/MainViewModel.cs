using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileBridge.Infrastructure;
using FileBridge.Infrastructure.Network;
using FileBridge.Infrastructure.Services;
using FileBridge.UI.Services;

namespace FileBridge.UI.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly INavigationService _navigation;
    private readonly ITransferBackgroundService _transfers;
    private string? _lastReceivedPath;

    /// <summary>Name other devices see.</summary>
    [ObservableProperty]
    private string _thisDevice = string.Empty;

    /// <summary>IP:port where this device accepts files — what to type on the other device if discovery fails.</summary>
    [ObservableProperty]
    private string _listeningOn = "Starting server…";

    [ObservableProperty]
    private string _receivedFolder = string.Empty;

    [ObservableProperty]
    private string _receiveStatus = "No files received yet";

    [ObservableProperty]
    private bool _hasReceivedFile;

    public MainViewModel(
        INavigationService navigation,
        ITransferBackgroundService transfers,
        FileReceiver receiver,
        FileBridgeOptions options)
    {
        _navigation = navigation;
        _transfers = transfers;

        ThisDevice = options.DeviceName;
        ReceivedFolder = receiver.ReceivedFilesDirectory;

        receiver.Progress += (_, e) => MainThread.BeginInvokeOnMainThread(() =>
            ReceiveStatus = $"Receiving {e.FileName} from {e.PeerName ?? "?"}: {Percent(e.BytesReceived, e.TotalBytes)}");
        receiver.FileReceived += (_, e) => MainThread.BeginInvokeOnMainThread(() =>
        {
            _lastReceivedPath = e.FullPath;
            HasReceivedFile = true;
            ReceiveStatus = $"✓ Received {e.FileName} ({e.Size:N0} bytes) from {e.PeerName ?? e.RemoteEndpoint}";
        });
        receiver.ReceiveFailed += (_, e) => MainThread.BeginInvokeOnMainThread(() =>
            ReceiveStatus = $"✗ {e.FileName}: {e.Reason}");

        RefreshStatus();
    }

    [RelayCommand]
    private void RefreshStatus()
    {
        var port = _transfers.ListeningPort;
        if (port == 0)
        {
            ListeningOn = "Server not running yet (pull to refresh; check the logs if it persists)";
            return;
        }

        var addresses = LocalNetwork.GetLocalIPv4Addresses().Select(a => $"{a.Address}:{port}").Distinct().ToList();
        ListeningOn = addresses.Count > 0 ? string.Join("  ·  ", addresses) : $"port {port} (no IPv4 address found)";
    }

    [RelayCommand]
    private async Task OpenLastReceived()
    {
        if (_lastReceivedPath is null)
            return;

        try
        {
            await Launcher.Default.OpenAsync(new OpenFileRequest
            {
                Title = Path.GetFileName(_lastReceivedPath),
                File = new ReadOnlyFile(_lastReceivedPath)
            });
        }
        catch (Exception ex)
        {
            SetError($"Could not open the file: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task OpenDevices() => await _navigation.GoToAsync("devices");

    [RelayCommand]
    private async Task OpenTransfers() => await _navigation.GoToAsync("transfers");

    [RelayCommand]
    private async Task OpenSettings() => await _navigation.GoToAsync("settings");

    private static string Percent(long done, long total) => total > 0 ? $"{done * 100 / total}%" : "…";
}
