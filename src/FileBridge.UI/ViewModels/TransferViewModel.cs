using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileBridge.Application.Events;
using FileBridge.Application.Services;
using FileBridge.Domain.Entities;
using FileBridge.Domain.Interfaces;
using FileBridge.Infrastructure.Services;
using FileBridge.UI.Services;
using System.Collections.ObjectModel;
using DomainDevice = FileBridge.Domain.Entities.Device;

namespace FileBridge.UI.ViewModels;

public partial class TransferViewModel : ViewModelBase, IQueryAttributable
{
    private readonly TransferOrchestrator _transferOrchestrator;
    private readonly ITransferStore _transferStore;
    private readonly INavigationService _navigationService;
    private readonly ITransferBackgroundService _transfers;
    private readonly Dictionary<string, string> _queuedJobs = new(); // jobId -> file name
    private readonly Dictionary<string, string> _finishedBeforeTracked = new(); // jobId -> final message
    private bool _queued;

    [ObservableProperty]
    private ObservableCollection<Domain.Entities.TransferJob> _activeTransfers = new();

    [ObservableProperty]
    private ObservableCollection<Domain.Entities.TransferJob> _transferHistory = new();

    [ObservableProperty]
    private Domain.Entities.TransferJob? _selectedTransfer;

    /// <summary>What is happening with the transfer(s) started from this page.</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public TransferViewModel(
        TransferOrchestrator transferOrchestrator,
        ITransferStore transferStore,
        INavigationService navigationService,
        ITransferBackgroundService transfers)
    {
        _transferOrchestrator = transferOrchestrator;
        _transferStore = transferStore;
        _navigationService = navigationService;
        _transfers = transfers;
    }

    /// <summary>
    /// FileBrowserViewModel navigates here with "file" (or "files") and "device". Before, nothing read
    /// these parameters and nothing ever queued a transfer.
    /// </summary>
    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (_queued || !query.TryGetValue("device", out var d) || d is not DomainDevice device)
            return;

        var files = new List<FileItem>();
        if (query.TryGetValue("file", out var f) && f is FileItem single)
            files.Add(single);
        if (query.TryGetValue("files", out var many) && many is IEnumerable<FileItem> list)
            files.AddRange(list);
        if (files.Count == 0)
            return;

        _queued = true; // Shell can re-apply the query when navigating back to this page
        _ = QueueAsync(files, device);
    }

    private async Task QueueAsync(IReadOnlyList<FileItem> files, DomainDevice device)
    {
        _transferOrchestrator.OnProgressChanged += OnProgress;
        _transfers.OnTransferCompleted += OnCompleted;
        _transfers.OnTransferFailed += OnFailed;

        foreach (var file in files)
        {
            var jobId = await _transfers.QueueUploadAsync(file, device);
            string? earlyResult;
            lock (_queuedJobs)
            {
                _queuedJobs[jobId] = file.FileName;
                _finishedBeforeTracked.Remove(jobId, out earlyResult);
            }
            if (earlyResult != null)
                Finish(jobId, earlyResult); // a tiny file can finish before QueueUploadAsync returns
        }

        StatusMessage = files.Count == 1
            ? $"Sending {files[0].FileName} to {device.Name} ({device.IpAddress}:{device.Port})…"
            : $"Sending {files.Count} files to {device.Name}…";
    }

    private void OnProgress(object? sender, TransferProgressEventArgs e)
    {
        string? fileName;
        lock (_queuedJobs)
            _queuedJobs.TryGetValue(e.JobId, out fileName);
        if (fileName is null)
            return;

        var percent = e.TotalBytes > 0 ? e.BytesTransferred * 100 / e.TotalBytes : 0;
        var speed = e.SpeedBytesPerSec > 0 ? $" · {e.SpeedBytesPerSec / 1024 / 1024:0.0} MB/s" : string.Empty;
        MainThread.BeginInvokeOnMainThread(() => StatusMessage = $"Sending {fileName}: {percent}%{speed}");
    }

    private void OnCompleted(object? sender, TransferCompletedEvent e) =>
        Finish(e.JobId, $"✓ {e.FileName} sent (size and SHA-256 confirmed by the other device)");

    private void OnFailed(object? sender, TransferFailedEvent e) =>
        Finish(e.JobId, $"✗ {e.FileName} was not sent: {e.ErrorMessage}");

    private void Finish(string jobId, string message)
    {
        bool allDone;
        lock (_queuedJobs)
        {
            if (!_queuedJobs.Remove(jobId))
            {
                _finishedBeforeTracked[jobId] = message;
                return;
            }
            allDone = _queuedJobs.Count == 0;
        }

        if (allDone)
        {
            _transferOrchestrator.OnProgressChanged -= OnProgress;
            _transfers.OnTransferCompleted -= OnCompleted;
            _transfers.OnTransferFailed -= OnFailed;
        }

        MainThread.BeginInvokeOnMainThread(() => StatusMessage = message);
    }

    [RelayCommand]
    private async Task LoadHistoryAsync()
    {
        try
        {
            var history = await _transferStore.GetHistoryAsync();
            TransferHistory = new ObservableCollection<Domain.Entities.TransferJob>(history);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    [RelayCommand]
    private async Task PauseTransfer(Domain.Entities.TransferJob job)
    {
        if (job == null) return;

        try
        {
            await _transferOrchestrator.PauseAsync(job.Id.ToString());
            job.Status = Domain.Enums.TransferStatus.Paused;
        }
        catch (Exception ex)
        {
            SetError($"Failed to pause: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ResumeTransfer(Domain.Entities.TransferJob job)
    {
        if (job == null) return;

        try
        {
            await _transferOrchestrator.ResumeAsync(job.Id.ToString());
        }
        catch (Exception ex)
        {
            SetError($"Failed to resume: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task CancelTransfer(Domain.Entities.TransferJob job)
    {
        if (job == null) return;

        try
        {
            await _transferOrchestrator.CancelAsync(job.Id.ToString());
            job.Status = Domain.Enums.TransferStatus.Cancelled;
            await LoadHistoryAsync();
        }
        catch (Exception ex)
        {
            SetError($"Failed to cancel: {ex.Message}");
        }
    }
}
