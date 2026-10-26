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

            job.Status = TransferStatus.InProgress;
            var progress = new ThrottledProgress(this, job);
            progress.Report(0);

            // Completes only when the receiver confirmed size + SHA-256; throws otherwise.
            var result = await _transport.SendFileAsync(target, job.LocalFilePath, job.Id, progress, cts.Token);

            job.TransferredBytes = result.BytesSent;
            progress.ReportFinal(result.BytesSent);
            job.Status = TransferStatus.Completed;
            job.CompletedAt = DateTimeOffset.UtcNow;
            return true;
        }
        catch (OperationCanceledException)
        {
            if (job.Status != TransferStatus.Paused)
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

    public async Task PauseAsync(string jobId)
    {
        if (_activeTransfers.TryGetValue(jobId, out var job))
        {
            job.Status = TransferStatus.Paused;
            job.ResumeToken = job.TransferredBytes.ToString();
        }

        if (_transferCancellations.TryGetValue(jobId, out var cts))
        {
            await cts.CancelAsync();
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
