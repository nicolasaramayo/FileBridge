namespace FileBridge.Infrastructure.Services;

using System.IO;
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
}

public class TransferBackgroundService : FileBridgeHostedService, ITransferBackgroundService
{
    private readonly TransferOrchestrator _orchestrator;
    private readonly TcpServer _tcpServer;
    private readonly ILogger<TransferBackgroundService> _logger;
    private readonly Channel<(TransferJob Job, TransferProgressDto Progress)> _progressChannel;
    private readonly SemaphoreSlim _queueSemaphore = new(1, 1);
    private readonly Queue<TransferJob> _transferQueue = new();
    private readonly Dictionary<string, TransferJob> _activeJobs = new();

    public ChannelReader<(TransferJob Job, TransferProgressDto Progress)> ProgressChannel => _progressChannel.Reader;

    public event EventHandler<TransferCompletedEvent>? OnTransferCompleted;

    public TransferBackgroundService(
        TransferOrchestrator orchestrator,
        TcpServer tcpServer,
        ILogger<TransferBackgroundService> logger)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _tcpServer = tcpServer ?? throw new ArgumentNullException(nameof(tcpServer));
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

    public Task PauseAsync(string jobId)
    {
        if (_activeJobs.TryGetValue(jobId, out var job))
        {
            job.Status = TransferStatus.Paused;
            _logger.LogInformation("Paused transfer: {JobId}", jobId);
        }
        return Task.CompletedTask;
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

    public Task CancelAsync(string jobId)
    {
        if (_activeJobs.TryGetValue(jobId, out var job))
        {
            job.Status = TransferStatus.Cancelled;
            _activeJobs.Remove(jobId);
            _logger.LogInformation("Cancelled transfer: {JobId}", jobId);
        }
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Start the TCP server
        await _tcpServer.StartAsync(0, stoppingToken);
        _logger.LogInformation("TCP transfer server started on port {Port}", _tcpServer.Port);

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
                    try
                    {
                        job.Status = TransferStatus.InProgress;

                        if (job.Direction == TransferDirection.Send)
                        {
                            await _orchestrator.SendFileAsync(job, stoppingToken);
                        }
                        else
                        {
                            await _orchestrator.ReceiveFileAsync(job, stoppingToken);
                        }

                        if (job.Status == TransferStatus.Completed)
                        {
                            OnTransferCompleted?.Invoke(this, new TransferCompletedEvent(
                                job.Id.ToString(),
                                Path.GetFileName(job.LocalFilePath ?? job.RemoteFilePath ?? string.Empty),
                                job.DeviceId.ToString(),
                                job.CompletedAt ?? DateTimeOffset.UtcNow,
                                job.TotalBytes,
                                null));

                            _logger.LogInformation("Transfer completed: {JobId}", job.Id);
                        }
                    }
                    catch (Exception ex)
                    {
                        job.Status = TransferStatus.Failed;
                        job.ErrorMessage = ex.Message;
                        _logger.LogWarning(ex, "Transfer failed: {JobId}", job.Id);
                    }
                    finally
                    {
                        _activeJobs.Remove(job.Id.ToString());
                    }
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
}
