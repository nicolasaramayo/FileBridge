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

    // All guarded by _lock. A job can finish before QueueUploadAsync returns its id (tiny files),
    // so results that arrive for an id we do not know yet are kept until it is registered.
    private readonly object _lock = new();
    private readonly Dictionary<string, string> _queuedJobs = new(); // jobId -> file name
    private readonly Dictionary<string, string> _finishedBeforeTracked = new(); // jobId -> final message
    private bool _queueing;
    private bool _subscribed;
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

    /// <summary>"No active transfers" must not cover the page while a file is being sent.</summary>
    [ObservableProperty]
    private bool _showEmptyState = true;

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

        _queued = true; // never queue the same selection twice
        _ = QueueAsync(files, device);
    }

    private async Task QueueAsync(IReadOnlyList<FileItem> files, DomainDevice device)
    {
        // Set before queuing: a fast job can report its result while we are still in the loop below.
        StatusMessage = files.Count == 1
            ? $"Waiting for {device.Name} to accept {files[0].FileName}…"
            : $"Waiting for {device.Name} to accept {files.Count} files…";
        ShowEmptyState = false;

        lock (_lock)
            _queueing = true;
        Subscribe();

        try
        {
            foreach (var file in files)
            {
                var jobId = await _transfers.QueueUploadAsync(file, device);
                string? earlyResult;
                lock (_lock)
                {
                    _queuedJobs[jobId] = file.FileName;
                    _finishedBeforeTracked.Remove(jobId, out earlyResult);
                }
                if (earlyResult != null)
                    Finish(jobId, earlyResult);
            }
        }
        catch (Exception ex)
        {
            SetError($"Could not queue the transfer: {ex.Message}");
            StatusMessage = $"✗ Could not queue the transfer: {ex.Message}";
        }
        finally
        {
            lock (_lock)
            {
                _queueing = false;
                _finishedBeforeTracked.Clear();
            }
            UnsubscribeIfDone();
        }
    }

    private void Subscribe()
    {
        lock (_lock)
        {
            if (_subscribed)
                return;
            _subscribed = true;
        }
        _transferOrchestrator.OnProgressChanged += OnProgress;
        _transfers.OnTransferCompleted += OnCompleted;
        _transfers.OnTransferFailed += OnFailed;
    }

    /// <summary>Only once every queued file has finished: the view model is transient and the services are singletons.</summary>
    private void UnsubscribeIfDone()
    {
        lock (_lock)
        {
            if (!_subscribed || _queueing || _queuedJobs.Count > 0)
                return;
            _subscribed = false;
        }
        _transferOrchestrator.OnProgressChanged -= OnProgress;
        _transfers.OnTransferCompleted -= OnCompleted;
        _transfers.OnTransferFailed -= OnFailed;
        MainThread.BeginInvokeOnMainThread(() => ShowEmptyState = ActiveTransfers.Count == 0);
    }

    private void OnProgress(object? sender, TransferProgressEventArgs e)
    {
        string? fileName;
        lock (_lock)
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
        lock (_lock)
        {
            if (!_queuedJobs.Remove(jobId))
            {
                // Not registered yet (still queuing), or a job started from another page.
                if (_queueing)
                    _finishedBeforeTracked[jobId] = message;
                return;
            }
        }

        MainThread.BeginInvokeOnMainThread(() => StatusMessage = message);
        UnsubscribeIfDone();
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
            await _transfers.PauseAsync(job.Id.ToString());
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
            await _transfers.ResumeAsync(job.Id.ToString());
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
            await _transfers.CancelAsync(job.Id.ToString());
            await LoadHistoryAsync();
        }
        catch (Exception ex)
        {
            SetError($"Failed to cancel: {ex.Message}");
        }
    }
}
