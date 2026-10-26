namespace FileBridge.Infrastructure.Services;

using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading.Channels;
using FileBridge.Application.DTOs;
using FileBridge.Application.Events;
using FileBridge.Application.Services;
using FileBridge.Domain.Entities;
using FileBridge.Domain.Enums;
using FileBridge.Domain.Interfaces;
using FileBridge.Infrastructure.Network;
using Microsoft.Extensions.Logging;

public interface ITransferBackgroundService
{
    Task<string> QueueUploadAsync(FileItem file, Device targetDevice);
    Task<string> QueueDownloadAsync(string remoteFileId, Device sourceDevice, string localPath);

    /// <summary>Stops a queued or running transfer; it stays paused until <see cref="ResumeAsync"/>.</summary>
    Task PauseAsync(string jobId);

    /// <summary>Queues a paused transfer again. It restarts from the beginning (the protocol has no resume yet).</summary>
    Task ResumeAsync(string jobId);

    /// <summary>Cancels a transfer; <see cref="OnTransferFailed"/> reports it as cancelled.</summary>
    Task CancelAsync(string jobId);

    ChannelReader<(TransferJob Job, TransferProgressDto Progress)> ProgressChannel { get; }
    event EventHandler<TransferCompletedEvent>? OnTransferCompleted;

    /// <summary>A transfer ended without completing (failed or cancelled).</summary>
    event EventHandler<TransferFailedEvent>? OnTransferFailed;

    /// <summary>The server started or this device was announced again (e.g. after a network change).</summary>
    event EventHandler? StatusChanged;

    /// <summary>Port the transfer server is listening on (0 while not started).</summary>
    int ListeningPort { get; }

    /// <summary>Announces this device again over mDNS, e.g. after the Wi-Fi network or the IP changed.</summary>
    /// <returns>False if it is not advertised afterwards (server not running, or mDNS failed to start).</returns>
    Task<bool> RestartAdvertisingAsync();
}

public class TransferBackgroundService : FileBridgeHostedService, ITransferBackgroundService
{
    private readonly TransferOrchestrator _orchestrator;
    private readonly TcpServer _tcpServer;
    private readonly ILogger<TransferBackgroundService> _logger;
    private readonly Channel<(TransferJob Job, TransferProgressDto Progress)> _progressChannel;
    // The queue loop waits on this channel instead of polling every 100 ms.
    private readonly Channel<TransferJob> _queue = Channel.CreateUnbounded<TransferJob>(new UnboundedChannelOptions { SingleReader = true });
    // f1415d8 replaced this declaration with _advertiser and the project stopped compiling (CS0103).
    // Concurrent because it is touched from the UI thread, the queue loop and progress callbacks.
    private readonly ConcurrentDictionary<string, TransferJob> _activeJobs = new();
    // Every status change (here and in the orchestrator) is an atomic TransferJob.TryChangeStatus, and a final
    // event is raised only by whoever removes the job from _activeJobs, so it is raised exactly once.
    private readonly ZeroconfAdvertiser _advertiser;
    private readonly SemaphoreSlim _advertiseLock = new(1, 1);
    private readonly IDeviceRegistry _devices;
    private readonly FileBridgeOptions _options;

    public ChannelReader<(TransferJob Job, TransferProgressDto Progress)> ProgressChannel => _progressChannel.Reader;

    public event EventHandler<TransferCompletedEvent>? OnTransferCompleted;
    public event EventHandler<TransferFailedEvent>? OnTransferFailed;
    public event EventHandler? StatusChanged;

    public int ListeningPort => _tcpServer.IsRunning ? _tcpServer.Port : 0;

    public TransferBackgroundService(
        TransferOrchestrator orchestrator,
        TcpServer tcpServer,
        ConnectionManager connections,
        ZeroconfAdvertiser advertiser,
        IDeviceRegistry devices,
        FileBridgeOptions options,
        ILogger<TransferBackgroundService> logger)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _tcpServer = tcpServer ?? throw new ArgumentNullException(nameof(tcpServer));
        // ConnectionManager is what plugs the protocol handler into the TcpServer. Nothing in the app
        // ever resolved it, so incoming data had no handler. Taking it here guarantees it exists
        // before the server starts accepting connections.
        ArgumentNullException.ThrowIfNull(connections);
        _advertiser = advertiser ?? throw new ArgumentNullException(nameof(advertiser));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _progressChannel = Channel.CreateBounded<(TransferJob, TransferProgressDto)>(
            new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest });

        _orchestrator.OnProgressChanged += OnOrchestratorProgressChanged;
    }

    private void OnOrchestratorProgressChanged(object? sender, TransferProgressEventArgs e)
    {
        if (!_activeJobs.TryGetValue(e.JobId, out var job))
            return;

        var dto = new TransferProgressDto(
            e.JobId,
            Path.GetFileName(job.LocalFilePath ?? job.RemoteFilePath ?? string.Empty),
            e.TotalBytes,
            e.BytesTransferred,
            e.TotalBytes > 0 ? (double)e.BytesTransferred / e.TotalBytes * 100 : 0,
            e.SpeedBytesPerSec,
            e.EstimatedTimeRemaining,
            TransferStatus.InProgress);

        _progressChannel.Writer.TryWrite((job, dto));
    }

    public Task<string> QueueUploadAsync(FileItem file, Device targetDevice)
    {
        // The orchestrator resolves the target by DeviceId: make sure this device is known.
        _devices.AddOrUpdate(targetDevice);

        var job = new TransferJob
        {
            Id = Guid.NewGuid(),
            FileItemId = file.Id,
            DeviceId = targetDevice.Id,
            LocalFilePath = file.FilePath,
            Direction = TransferDirection.Send,
            Status = TransferStatus.Pending,
            TotalBytes = file.FileSize,
            ChunkSize = 65536,
            CreatedAt = DateTimeOffset.UtcNow
        };

        Enqueue(job);
        _logger.LogInformation("Queued upload: {FileName} to {DeviceName}", file.FileName, targetDevice.Name);
        return Task.FromResult(job.Id.ToString());
    }

    public Task<string> QueueDownloadAsync(string remoteFileId, Device sourceDevice, string localPath)
    {
        var job = new TransferJob
        {
            Id = Guid.NewGuid(),
            DeviceId = sourceDevice.Id,
            RemoteFilePath = remoteFileId,
            LocalFilePath = localPath,
            Direction = TransferDirection.Receive,
            Status = TransferStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow
        };

        Enqueue(job);
        _logger.LogInformation("Queued download: {RemoteFileId} from {DeviceName}", remoteFileId, sourceDevice.Name);
        return Task.FromResult(job.Id.ToString());
    }

    private void Enqueue(TransferJob job)
    {
        _activeJobs[job.Id.ToString()] = job;
        if (!_queue.Writer.TryWrite(job))
            throw new InvalidOperationException("The transfer service is stopped.");
    }

    public async Task PauseAsync(string jobId)
    {
        if (!_activeJobs.TryGetValue(jobId, out var job))
            return;

        if (!job.TryChangeStatus(TransferStatus.Paused, out var previous, TransferStatus.Pending, TransferStatus.InProgress))
            return;

        if (previous == TransferStatus.InProgress)
            await _orchestrator.PauseAsync(jobId); // stops the attempt; the job stays Paused
        _logger.LogInformation("Paused transfer: {JobId}", jobId);
    }

    public Task ResumeAsync(string jobId)
    {
        if (!_activeJobs.TryGetValue(jobId, out var job))
            return Task.CompletedTask;

        if (!job.TryChangeStatus(TransferStatus.Pending, TransferStatus.Paused))
            return Task.CompletedTask;
        job.TransferredBytes = 0;
        job.ResumeToken = null;

        // A paused job may still have an older entry in the queue: the loop skips entries that are not Pending
        // and only sends once (the second entry finds the job already InProgress/Completed).
        if (_queue.Writer.TryWrite(job))
            _logger.LogInformation("Resumed transfer: {JobId} (restarts from the beginning)", jobId);
        return Task.CompletedTask;
    }

    public async Task CancelAsync(string jobId)
    {
        if (!_activeJobs.TryGetValue(jobId, out var job))
            return;

        if (!job.TryChangeStatus(TransferStatus.Cancelled, out var previous,
                TransferStatus.Pending, TransferStatus.InProgress, TransferStatus.Paused))
            return; // already completed, failed or cancelled
        _logger.LogInformation("Cancelling transfer: {JobId}", jobId);

        switch (previous)
        {
            case TransferStatus.InProgress:
                await _orchestrator.CancelAsync(jobId); // the queue loop reports it when the attempt unwinds
                break;
            case TransferStatus.Paused:
                // Not running (it may still have a stale queue entry, which will find it removed): report it now.
                if (_activeJobs.TryRemove(jobId, out _))
                    RaiseFailed(job, FileNameOf(job), "Cancelled");
                break;
            // Pending: still in the queue; the queue loop reports it when it gets there.
        }
    }

    public async Task<bool> RestartAdvertisingAsync()
    {
        if (!_tcpServer.IsRunning)
            return false;

        var advertised = false;
        await _advertiseLock.WaitAsync();
        try
        {
            await _advertiser.StopAdvertisingAsync();
            await _advertiser.StartAdvertisingAsync(_options.DeviceName, _tcpServer.Port, _options.DeviceId.ToString());
            advertised = true;
            _logger.LogInformation("mDNS advertising restarted");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not restart mDNS advertising");
        }
        finally
        {
            _advertiseLock.Release();
        }

        RaiseStatusChanged();
        return advertised;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await StartServerAsync(stoppingToken);
        RaiseStatusChanged();

        // Start mDNS advertising. If it fails (no multicast on this network, missing Android
        // MulticastLock...), keep going: the server still works and devices can be added by IP.
        await _advertiseLock.WaitAsync(stoppingToken);
        try
        {
            await _advertiser.StartAdvertisingAsync(_options.DeviceName, _tcpServer.Port, _options.DeviceId.ToString(), stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "mDNS advertising failed; this device will only be reachable by IP:port");
        }
        finally
        {
            _advertiseLock.Release();
        }

        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await ProcessJobAsync(job, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
        finally
        {
            try { await _advertiser.StopAdvertisingAsync(); } catch { /* best effort */ }
            await _tcpServer.StopAsync();
            _logger.LogInformation("TCP transfer server stopped");
        }
    }

    private async Task StartServerAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _tcpServer.StartAsync(_options.Port, stoppingToken);
        }
        catch (SocketException ex) when (_options.Port != 0)
        {
            _logger.LogWarning("Port {Port} is not available ({Reason}); using a random port", _options.Port, ex.SocketErrorCode);
            await _tcpServer.StartAsync(0, stoppingToken);
        }

        var addresses = LocalNetwork.GetLocalIPv4Addresses().Select(a => $"{a.Address}:{_tcpServer.Port} ({a.InterfaceName})");
        _logger.LogInformation("TCP transfer server listening on {Endpoints}", string.Join(", ", addresses));
    }

    private async Task ProcessJobAsync(TransferJob job, CancellationToken stoppingToken)
    {
        var key = job.Id.ToString();
        var fileName = FileNameOf(job);

        if (!job.TryChangeStatus(TransferStatus.InProgress, out var queuedStatus, TransferStatus.Pending))
        {
            // Cancelled while it was waiting in the queue: report it (once).
            if (queuedStatus == TransferStatus.Cancelled && _activeJobs.TryRemove(key, out _))
                RaiseFailed(job, fileName, "Cancelled");
            // Otherwise paused while queued (kept for ResumeAsync), or a stale entry after a resume.
            return;
        }

        try
        {
            if (job.Direction == TransferDirection.Send)
            {
                await _orchestrator.SendFileAsync(job, stoppingToken);
            }
            else
            {
                await _orchestrator.ReceiveFileAsync(job, stoppingToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            job.ErrorMessage = ex.Message;
            job.TryChangeStatus(TransferStatus.Failed, TransferStatus.InProgress);
        }

        switch (job.Status)
        {
            case TransferStatus.Completed:
                if (!_activeJobs.TryRemove(key, out _))
                    break;
                _logger.LogInformation("Transfer completed: {JobId}", job.Id);
                Raise(OnTransferCompleted, new TransferCompletedEvent(
                    key,
                    fileName,
                    job.DeviceId.ToString(),
                    job.CompletedAt ?? DateTimeOffset.UtcNow,
                    job.TotalBytes,
                    null));
                break;

            case TransferStatus.Paused:
                // Stays in _activeJobs so ResumeAsync can queue it again.
                _logger.LogInformation("Transfer paused: {JobId}", job.Id);
                break;

            case TransferStatus.Pending:
                // Resumed while the paused attempt was unwinding: it is already queued again.
                break;

            case TransferStatus.Cancelled:
                if (_activeJobs.TryRemove(key, out _))
                    RaiseFailed(job, fileName, "Cancelled");
                break;

            default:
                // Before, a failed job was dropped without telling anyone.
                if (_activeJobs.TryRemove(key, out _))
                    RaiseFailed(job, fileName, job.ErrorMessage ?? "Unknown error");
                break;
        }
    }

    private static string FileNameOf(TransferJob job) =>
        Path.GetFileName(job.LocalFilePath ?? job.RemoteFilePath ?? string.Empty);

    private void RaiseFailed(TransferJob job, string fileName, string reason)
    {
        _logger.LogWarning("Transfer did not complete: {JobId} ({File}): {Reason}", job.Id, fileName, reason);
        Raise(OnTransferFailed, new TransferFailedEvent(
            job.Id.ToString(),
            fileName,
            job.DeviceId.ToString(),
            DateTimeOffset.UtcNow,
            job.TransferredBytes,
            job.TotalBytes,
            reason));
    }

    private void RaiseStatusChanged()
    {
        var handler = StatusChanged;
        if (handler is null)
            return;
        foreach (var subscriber in handler.GetInvocationList())
        {
            try { ((EventHandler)subscriber)(this, EventArgs.Empty); }
            catch (Exception ex) { _logger.LogWarning(ex, "A StatusChanged subscriber threw"); }
        }
    }

    /// <summary>A UI subscriber that throws must not stop the queue loop.</summary>
    private void Raise<T>(EventHandler<T>? handler, T args)
    {
        if (handler is null)
            return;
        foreach (var subscriber in handler.GetInvocationList())
        {
            try { ((EventHandler<T>)subscriber)(this, args); }
            catch (Exception ex) { _logger.LogWarning(ex, "A transfer event subscriber threw"); }
        }
    }
}
