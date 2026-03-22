using System.Collections.Concurrent;
using FileBridge.Domain.Entities;
using FileBridge.Domain.Enums;
using FileBridge.Domain.Interfaces;

namespace FileBridge.Application.Services;

public class TransferOrchestrator : ITransferService
{
    private readonly IFileService _fileService;
    private readonly ConcurrentDictionary<string, TransferJob> _activeTransfers = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _transferCancellations = new();
    private readonly ConcurrentDictionary<string, Device> _connectedDevices = new();

    public event EventHandler<TransferProgressEventArgs>? OnProgressChanged;

    public TransferOrchestrator(IFileService fileService)
    {
        _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));
    }

    public async Task<bool> SendFileAsync(TransferJob job, CancellationToken ct = default)
    {
        if (job == null) throw new ArgumentNullException(nameof(job));
        if (string.IsNullOrEmpty(job.LocalFilePath))
            throw new ArgumentException("Local file path is required", nameof(job));

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _transferCancellations[job.Id.ToString()] = cts;
        _activeTransfers[job.Id.ToString()] = job;

        try
        {
            // Get file stream
            var readStream = await _fileService.OpenReadAsync(job.LocalFilePath, cts.Token);
            
            // Notify started
            OnProgressChanged?.Invoke(this, new TransferProgressEventArgs
            {
                JobId = job.Id.ToString(),
                BytesTransferred = 0,
                TotalBytes = job.TotalBytes,
                SpeedBytesPerSec = 0
            });

            // Stream chunks 
            var buffer = new byte[job.ChunkSize];
            long totalSent = 0;
            var lastProgressTime = DateTimeOffset.UtcNow;
            long lastSentBytes = 0;

            while (!cts.Token.IsCancellationRequested)
            {
                var bytesRead = await readStream.ReadAsync(buffer, cts.Token);
                if (bytesRead == 0) break;

                totalSent += bytesRead;
                job.TransferredBytes = totalSent;

                // Calculate speed and ETA every 500ms
                var now = DateTimeOffset.UtcNow;
                var elapsed = now - lastProgressTime;
                if (elapsed.TotalSeconds >= 0.5)
                {
                    var bytesPerSecond = elapsed.TotalSeconds > 0 
                        ? (totalSent - lastSentBytes) / elapsed.TotalSeconds 
                        : 0;
                    var remainingBytes = job.TotalBytes - totalSent;
                    var eta = bytesPerSecond > 0 ? TimeSpan.FromSeconds(remainingBytes / bytesPerSecond) : (TimeSpan?)null;

                    OnProgressChanged?.Invoke(this, new TransferProgressEventArgs
                    {
                        JobId = job.Id.ToString(),
                        BytesTransferred = totalSent,
                        TotalBytes = job.TotalBytes,
                        SpeedBytesPerSec = bytesPerSecond,
                        EstimatedTimeRemaining = eta
                    });

                    lastProgressTime = now;
                    lastSentBytes = totalSent;
                }
            }

            await readStream.DisposeAsync();
            
            if (cts.Token.IsCancellationRequested)
            {
                job.Status = TransferStatus.Cancelled;
                return false;
            }

            job.Status = TransferStatus.Completed;
            job.CompletedAt = DateTimeOffset.UtcNow;
            return true;
        }
        catch (OperationCanceledException)
        {
            job.Status = TransferStatus.Cancelled;
            return false;
        }
        catch (Exception ex)
        {
            job.Status = TransferStatus.Failed;
            job.ErrorMessage = ex.Message;
            return false;
        }
        finally
        {
            _activeTransfers.TryRemove(job.Id.ToString(), out _);
            _transferCancellations.TryRemove(job.Id.ToString(), out var removedCts);
            removedCts?.Dispose();
        }
    }

    public async Task<bool> ReceiveFileAsync(TransferJob job, CancellationToken ct = default)
    {
        if (job == null) throw new ArgumentNullException(nameof(job));
        if (string.IsNullOrEmpty(job.LocalFilePath))
            throw new ArgumentException("Local file path is required", nameof(job));

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _transferCancellations[job.Id.ToString()] = cts;
        _activeTransfers[job.Id.ToString()] = job;

        try
        {
            // Create file stream for writing
            var writeStream = await _fileService.OpenWriteAsync(job.LocalFilePath, job.TotalBytes, cts.Token);
            
            // Notify started
            OnProgressChanged?.Invoke(this, new TransferProgressEventArgs
            {
                JobId = job.Id.ToString(),
                BytesTransferred = 0,
                TotalBytes = job.TotalBytes,
                SpeedBytesPerSec = 0
            });

            var buffer = new byte[job.ChunkSize];
            long totalReceived = 0;
            var lastProgressTime = DateTimeOffset.UtcNow;
            long lastReceivedBytes = 0;

            while (totalReceived < job.TotalBytes && !cts.Token.IsCancellationRequested)
            {
                // In a real implementation, this would receive data from the network
                // For now, simulate the progress
                await Task.Delay(100, cts.Token);
                totalReceived += Math.Min(job.ChunkSize, job.TotalBytes - totalReceived);
                job.TransferredBytes = totalReceived;

                // Calculate speed and ETA
                var now = DateTimeOffset.UtcNow;
                if ((now - lastProgressTime).TotalSeconds >= 0.5)
                {
                    var bytesPerSecond = (now - lastProgressTime).TotalSeconds > 0 
                        ? (totalReceived - lastReceivedBytes) / (now - lastProgressTime).TotalSeconds 
                        : 0;
                    var remainingBytes = job.TotalBytes - totalReceived;
                    var eta = bytesPerSecond > 0 ? TimeSpan.FromSeconds(remainingBytes / bytesPerSecond) : (TimeSpan?)null;

                    OnProgressChanged?.Invoke(this, new TransferProgressEventArgs
                    {
                        JobId = job.Id.ToString(),
                        BytesTransferred = totalReceived,
                        TotalBytes = job.TotalBytes,
                        SpeedBytesPerSec = bytesPerSecond,
                        EstimatedTimeRemaining = eta
                    });

                    lastProgressTime = now;
                    lastReceivedBytes = totalReceived;
                }
            }

            await writeStream.DisposeAsync();

            if (cts.Token.IsCancellationRequested)
            {
                job.Status = TransferStatus.Cancelled;
                return false;
            }

            // Verify hash
            if (!string.IsNullOrEmpty(job.LocalFilePath))
            {
                var hash = await _fileService.ComputeHashAsync(job.LocalFilePath, cts.Token);
                // Hash verification would go here
            }

            job.Status = TransferStatus.Completed;
            job.CompletedAt = DateTimeOffset.UtcNow;
            return true;
        }
        catch (OperationCanceledException)
        {
            job.Status = TransferStatus.Cancelled;
            return false;
        }
        catch (Exception ex)
        {
            job.Status = TransferStatus.Failed;
            job.ErrorMessage = ex.Message;
            return false;
        }
        finally
        {
            _activeTransfers.TryRemove(job.Id.ToString(), out _);
            _transferCancellations.TryRemove(job.Id.ToString(), out var removedCts);
            removedCts?.Dispose();
        }
    }

    public async Task PauseAsync(string jobId)
    {
        if (_transferCancellations.TryGetValue(jobId, out var cts))
        {
            await cts.CancelAsync();
        }

        if (_activeTransfers.TryGetValue(jobId, out var job))
        {
            job.Status = TransferStatus.Paused;
            job.ResumeToken = job.TransferredBytes.ToString();
        }
    }

    public async Task ResumeAsync(string jobId)
    {
        if (_activeTransfers.TryGetValue(jobId, out var job) && !string.IsNullOrEmpty(job.ResumeToken))
        {
            if (long.TryParse(job.ResumeToken, out var offset))
            {
                job.TransferredBytes = offset;
                job.Status = TransferStatus.InProgress;
            }
        }
        
        await Task.CompletedTask;
    }

    public async Task CancelAsync(string jobId)
    {
        if (_transferCancellations.TryGetValue(jobId, out var cts))
        {
            await cts.CancelAsync();
        }

        if (_activeTransfers.TryGetValue(jobId, out var job))
        {
            job.Status = TransferStatus.Cancelled;
        }
        
        await Task.CompletedTask;
    }

    public IEnumerable<TransferJob> GetActiveTransfers()
    {
        return _activeTransfers.Values;
    }
}