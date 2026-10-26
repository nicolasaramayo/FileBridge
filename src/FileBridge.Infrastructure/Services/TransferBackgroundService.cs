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
    Task PauseAsync(string jobId);
    Task ResumeAsync(string jobId);
    Task CancelAsync(string jobId);
    ChannelReader<(TransferJob Job, TransferProgressDto Progress)> ProgressChannel { get; }
    event EventHandler<TransferCompletedEvent>? OnTransferCompleted;
    event EventHandler<TransferFailedEvent>? OnTransferFailed;

    /// <summary>Port the transfer server is listening on (0 while not started).</summary>
    int ListeningPort { get; }
}

public class TransferBackgroundService : FileBridgeHostedService, ITransferBackgroundService
{
    private readonly TransferOrchestrator _orchestrator;
    private readonly TcpServer _tcpServer;
    private readonly ILogger<TransferBackgroundService> _logger;
    private readonly Channel<(TransferJob Job, TransferProgressDto Progress)> _progressChannel;
    private readonly SemaphoreSlim _queueSemaphore = new(1, 1);
    private readonly Queue<TransferJob> _transferQueue = new();
    // Restored: f1415d8 replaced this declaration with _advertiser and the project stopped compiling (CS0103).
    // Concurrent because it is touched from the UI thread, the queue loop and progress callbacks.
    private readonly ConcurrentDictionary<string, TransferJob> _activeJobs = new();
    private readonly ZeroconfAdvertiser _advertiser;
    private readonly IDeviceRegistry _devices;
    private readonly FileBridgeOptions _options;

    public ChannelReader<(TransferJob Job, TransferProgressDto Progress)> ProgressChannel => _progressChannel.Reader;

    public event EventHandler<TransferCompletedEvent>? OnTransferCompleted;
    public event EventHandler<TransferFailedEvent>? OnTransferFailed;

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

    public async Task<string> QueueUploadAsync(FileItem file, Device targetDevice)
    {
        // The orchestrator resolves the target by DeviceId: make sure this device is known.
        _devices.AddOrUpdate(targetDevice);

        await _queueSemaphore.WaitAsync();
        try
        {
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

            _transferQueue.Enqueue(job);
            _activeJobs[job.Id.ToString()] = job;

            _logger.LogInformation("Queued upload: {FileName} to {DeviceName}", file.FileName, targetDevice.Name);
            return job.Id.ToString();
        }
        finally
        {
            _queueSemaphore.Release();
        }
    }

    public async Task<string> QueueDownloadAsync(string remoteFileId, Device sourceDevice, string localPath)
    {
        await _queueSemaphore.WaitAsync();
        try
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

            _transferQueue.Enqueue(job);
            _activeJobs[job.Id.ToString()] = job;

            _logger.LogInformation("Queued download: {RemoteFileId} from {DeviceName}", remoteFileId, sourceDevice.Name);
            return job.Id.ToString();
        }
        finally
        {
            _queueSemaphore.Release();
        }
    }

    public async Task PauseAsync(string jobId)
    {
        if (_activeJobs.TryGetValue(jobId, out var job))
        {
            job.Status = TransferStatus.Paused;
            await _orchestrator.PauseAsync(jobId);
            _logger.LogInformation("Paused transfer: {JobId}", jobId);
        }
    }

    public Task ResumeAsync(string jobId)
    {
        if (_activeJobs.TryGetValue(jobId, out var job))
        {
            job.Status = TransferStatus.InProgress;
            _logger.LogInformation("Resumed transfer: {JobId}", jobId);
        }
        return Task.CompletedTask;
    }

    public async Task CancelAsync(string jobId)
    {
        if (_activeJobs.TryGetValue(jobId, out var job))
        {
            job.Status = TransferStatus.Cancelled;
            await _orchestrator.CancelAsync(jobId);
            _activeJobs.TryRemove(jobId, out _);
            _logger.LogInformation("Cancelled transfer: {JobId}", jobId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await StartServerAsync(stoppingToken);

        // Start mDNS advertising. If it fails (no multicast on this network, missing Android
        // MulticastLock...), keep going: the server still works and devices can be added by IP.
        try
        {
            await _advertiser.StartAdvertisingAsync(_options.DeviceName, _tcpServer.Port, _options.DeviceId.ToString(), stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "mDNS advertising failed; this device will only be reachable by IP:port");
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TransferJob? job = null;

                await _queueSemaphore.WaitAsync(stoppingToken);
                try
                {
                    if (_transferQueue.Count > 0)
                        job = _transferQueue.Dequeue();
                }
                finally
                {
                    _queueSemaphore.Release();
                }

                if (job != null)
                {
                    await ProcessJobAsync(job, stoppingToken);
                }

                await Task.Delay(100, stoppingToken);
            }
        }
        finally
        {
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
        try
        {
            if (job.Status == TransferStatus.Cancelled)
                return;

            job.Status = TransferStatus.InProgress;

            if (job.Direction == TransferDirection.Send)
            {
                await _orchestrator.SendFileAsync(job, stoppingToken);
            }
            else
            {
                await _orchestrator.ReceiveFileAsync(job, stoppingToken);
            }

            var fileName = Path.GetFileName(job.LocalFilePath ?? job.RemoteFilePath ?? string.Empty);
            if (job.Status == TransferStatus.Completed)
            {
                OnTransferCompleted?.Invoke(this, new TransferCompletedEvent(
                    job.Id.ToString(),
                    fileName,
                    job.DeviceId.ToString(),
                    job.CompletedAt ?? DateTimeOffset.UtcNow,
                    job.TotalBytes,
                    null));

                _logger.LogInformation("Transfer completed: {JobId}", job.Id);
            }
            else if (job.Status == TransferStatus.Failed)
            {
                // Before, a failed job was dropped without telling anyone.
                RaiseFailed(job, fileName);
            }
        }
        catch (Exception ex)
        {
            job.Status = TransferStatus.Failed;
            job.ErrorMessage = ex.Message;
            RaiseFailed(job, Path.GetFileName(job.LocalFilePath ?? job.RemoteFilePath ?? string.Empty));
        }
        finally
        {
            _activeJobs.TryRemove(job.Id.ToString(), out _);
        }
    }

    private void RaiseFailed(TransferJob job, string fileName)
    {
        _logger.LogWarning("Transfer failed: {JobId} ({File}): {Error}", job.Id, fileName, job.ErrorMessage);
        OnTransferFailed?.Invoke(this, new TransferFailedEvent(
            job.Id.ToString(),
            fileName,
            job.DeviceId.ToString(),
            DateTimeOffset.UtcNow,
            job.TransferredBytes,
            job.TotalBytes,
            job.ErrorMessage ?? "Unknown error"));
    }
}
