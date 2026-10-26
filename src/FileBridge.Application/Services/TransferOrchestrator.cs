using System.Collections.Concurrent;
using System.Diagnostics;
using FileBridge.Domain.Entities;
using FileBridge.Domain.Enums;
using FileBridge.Domain.Interfaces;

namespace FileBridge.Application.Services;

public class TransferOrchestrator : ITransferService
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(500);

    private readonly IFileTransport _transport;
    private readonly IDeviceRegistry _devices;
    private readonly ConcurrentDictionary<string, TransferJob> _activeTransfers = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _transferCancellations = new();

    public event EventHandler<TransferProgressEventArgs>? OnProgressChanged;

    public TransferOrchestrator(IFileTransport transport, IDeviceRegistry devices)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
    }

    public async Task<bool> SendFileAsync(TransferJob job, CancellationToken ct = default)
    {
        if (job == null) throw new ArgumentNullException(nameof(job));
        if (string.IsNullOrEmpty(job.LocalFilePath))
            throw new ArgumentException("Local file path is required", nameof(job));

        var key = job.Id.ToString();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _transferCancellations[key] = cts;
        _activeTransfers[key] = job;

        try
        {
            // The job only carries the DeviceId: resolve address/port from the devices seen in this session.
            var target = _devices.Find(job.DeviceId)
                ?? throw new InvalidOperationException(
                    $"Device {job.DeviceId} is unknown: it was not discovered nor added manually in this session.");

            if (job.TotalBytes <= 0 && File.Exists(job.LocalFilePath))
                job.TotalBytes = new FileInfo(job.LocalFilePath).Length;

            // The queue (TransferBackgroundService) already moved it to InProgress; a job handed to us directly
            // is still Pending. Anything else (paused/cancelled before the cancellation source above was
            // registered) stops here instead of being overwritten with InProgress.
            if (!job.TryChangeStatus(TransferStatus.InProgress, TransferStatus.Pending, TransferStatus.InProgress))
                throw new OperationCanceledException();

            var progress = new ThrottledProgress(this, job);
            progress.Report(0);

            // Completes only when the receiver confirmed size + SHA-256; throws otherwise.
            var result = await _transport.SendFileAsync(target, job.LocalFilePath, job.Id, progress, cts.Token);

            job.TransferredBytes = result.BytesSent;
            job.CompletedAt = DateTimeOffset.UtcNow;
            // The receiver confirmed the file: it is complete even if a pause/cancel arrived at the last moment.
            job.TryChangeStatus(TransferStatus.Completed,
                TransferStatus.InProgress, TransferStatus.Paused, TransferStatus.Cancelled, TransferStatus.Pending);
            progress.ReportFinal(result.BytesSent);
            return true;
        }
        catch (Exception ex) when (ex is OperationCanceledException || cts.IsCancellationRequested)
        {
            // Only a running attempt becomes Cancelled. Paused stays Paused, and Pending means it was
            // resumed while this attempt was unwinding (it is queued again): overwriting either would
            // turn a pause or a resume into a cancel.
            job.TryChangeStatus(TransferStatus.Cancelled, TransferStatus.InProgress);
            return false;
        }
        catch (Exception ex)
        {
            job.ErrorMessage = ex.Message;
            job.TryChangeStatus(TransferStatus.Failed, TransferStatus.InProgress, TransferStatus.Pending);
            return false;
        }
        finally
        {
            _activeTransfers.TryRemove(key, out _);
            _transferCancellations.TryRemove(key, out var removedCts);
            removedCts?.Dispose();
        }
    }

    public Task<bool> ReceiveFileAsync(TransferJob job, CancellationToken ct = default)
    {
        if (job == null) throw new ArgumentNullException(nameof(job));

        // Receiving is push-based: the other device connects to our TcpServer and FileReceiver
        // (Infrastructure) writes the file. Pulling a file *from* a remote device would need
        // ListFiles/FileRequest support on that device, which the protocol does not implement yet.
        // Fail explicitly instead of reporting a fake "Completed" with an empty file.
        job.Status = TransferStatus.Failed;
        job.ErrorMessage = "Pulling files from a remote device is not implemented yet; files are received when the other device sends them.";
        return Task.FromResult(false);
    }

    /// <summary>Stops the running attempt and leaves the job Paused (callers such as the queue may already have set it).</summary>
    public async Task PauseAsync(string jobId)
    {
        if (_activeTransfers.TryGetValue(jobId, out var job)
            && job.TryChangeStatus(TransferStatus.Paused, TransferStatus.InProgress))
        {
            job.ResumeToken = job.TransferredBytes.ToString();
        }

        await CancelAttemptAsync(jobId);
    }

    /// <summary>
    /// Nothing to do here: a paused attempt has already ended. Resuming means sending the job again,
    /// which the queue does (TransferBackgroundService.ResumeAsync).
    /// </summary>
    public Task ResumeAsync(string jobId) => Task.CompletedTask;

    /// <summary>Stops the running attempt and leaves the job Cancelled; a completed job stays Completed.</summary>
    public async Task CancelAsync(string jobId)
    {
        if (_activeTransfers.TryGetValue(jobId, out var job))
            job.TryChangeStatus(TransferStatus.Cancelled, TransferStatus.Pending, TransferStatus.InProgress, TransferStatus.Paused);

        await CancelAttemptAsync(jobId);
    }

    private async Task CancelAttemptAsync(string jobId)
    {
        if (!_transferCancellations.TryGetValue(jobId, out var cts))
            return;

        try
        {
            await cts.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // The attempt finished (and disposed it) in the meantime: nothing left to stop.
        }
    }

    public IEnumerable<TransferJob> GetActiveTransfers()
    {
        return _activeTransfers.Values;
    }

    /// <summary>Turns raw byte counts from the transport into throttled progress events with speed and ETA.</summary>
    private sealed class ThrottledProgress : IProgress<long>
    {
        private readonly TransferOrchestrator _owner;
        private readonly TransferJob _job;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private bool _hasReported;
        private TimeSpan _lastReportAt;
        private long _lastReportBytes;
        private double _speed;

        public ThrottledProgress(TransferOrchestrator owner, TransferJob job)
        {
            _owner = owner;
            _job = job;
        }

        public void Report(long bytesTransferred)
        {
            _job.TransferredBytes = bytesTransferred;
            var now = _clock.Elapsed;
            if (_hasReported && now - _lastReportAt < ProgressInterval)
                return;
            Raise(bytesTransferred, now);
        }

        public void ReportFinal(long bytesTransferred) => Raise(bytesTransferred, _clock.Elapsed);

        private void Raise(long bytes, TimeSpan now)
        {
            var seconds = (now - _lastReportAt).TotalSeconds;
            if (_hasReported && seconds > 0)
                _speed = (bytes - _lastReportBytes) / seconds;

            _hasReported = true;
            _lastReportAt = now;
            _lastReportBytes = bytes;

            var remaining = Math.Max(0, _job.TotalBytes - bytes);
            _owner.OnProgressChanged?.Invoke(_owner, new TransferProgressEventArgs
            {
                JobId = _job.Id.ToString(),
                BytesTransferred = bytes,
                TotalBytes = _job.TotalBytes,
                SpeedBytesPerSec = _speed,
                EstimatedTimeRemaining = _speed > 0 ? TimeSpan.FromSeconds(remaining / _speed) : null
            });
        }
    }
}
