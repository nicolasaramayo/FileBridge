namespace FileBridge.Infrastructure.Network;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

/// <summary>What the user is asked to accept before anything is written to disk.</summary>
public sealed record IncomingFileOffer(Guid TransferId, string FileName, long Size, string? PeerName, string? RemoteEndpoint);

public sealed class FileReceivedEventArgs : EventArgs
{
    public required Guid TransferId { get; init; }
    public required string FileName { get; init; }
    public required string FullPath { get; init; }
    public required long Size { get; init; }
    public required string Sha256 { get; init; }
    public string? PeerName { get; init; }
    public string? RemoteEndpoint { get; init; }
}

public sealed class ReceiveProgressEventArgs : EventArgs
{
    public required Guid TransferId { get; init; }
    public required string FileName { get; init; }
    public required long BytesReceived { get; init; }
    public required long TotalBytes { get; init; }
    public string? PeerName { get; init; }
}

public sealed class ReceiveFailedEventArgs : EventArgs
{
    public required Guid TransferId { get; init; }
    public required string FileName { get; init; }
    public required string Reason { get; init; }
}

/// <summary>
/// Writes incoming files to disk: one receive session per TCP connection
/// (FileRequest → Data… → TransferComplete). Every file must be accepted first through
/// <see cref="ApproveIncoming"/>; without an approver nothing is accepted. Data goes to "&lt;name&gt;.part"
/// and is renamed only after size and SHA-256 match what the sender announced, so a broken transfer
/// never leaves a file that looks complete.
/// </summary>
public sealed class FileReceiver
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);
    private static readonly char[] ForbiddenFileNameChars = "\\/:*?\"<>|".ToCharArray();
    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly FileBridgeOptions _options;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<TcpClient, Session> _sessions = new();
    private readonly object _pathLock = new();

    public event EventHandler<FileReceivedEventArgs>? FileReceived;
    public event EventHandler<ReceiveProgressEventArgs>? Progress;
    public event EventHandler<ReceiveFailedEventArgs>? ReceiveFailed;

    /// <summary>
    /// Decides whether an incoming file is accepted (the app asks the user). If it is null, every
    /// file is declined: since the server listens on the whole LAN, accepting silently would let any
    /// device on the same Wi-Fi drop files on this one.
    /// </summary>
    public Func<IncomingFileOffer, CancellationToken, Task<bool>>? ApproveIncoming { get; set; }

    public string ReceivedFilesDirectory => _options.ReceivedFilesDirectory;

    public FileReceiver(FileBridgeOptions options, ILogger<FileReceiver>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    public int ActiveSessions => _sessions.Count;

    public async Task BeginAsync(TcpClient connection, Guid transferId, string fileName, long size, string? peerName, CancellationToken ct = default)
    {
        if (size < 0 || size > _options.MaxIncomingFileSize)
            throw new ProtocolException(ProtocolErrorCodes.BadRequest, $"File size {size} is not accepted.");

        // One transfer at a time per connection: a new FileRequest replaces an unfinished one.
        Abort(connection, "superseded by a new FileRequest");

        var safeName = SanitizeFileName(fileName);
        var remoteEndpoint = connection.Client?.RemoteEndPoint?.ToString();
        await EnsureApprovedAsync(new IncomingFileOffer(transferId, safeName, size, peerName, remoteEndpoint), ct);

        string finalPath, partPath;
        FileStream stream;
        try
        {
            lock (_pathLock)
            {
                Directory.CreateDirectory(_options.ReceivedFilesDirectory);
                finalPath = GetUniquePath(_options.ReceivedFilesDirectory, safeName);
                partPath = finalPath + ".part";
                stream = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tell the sender why (otherwise it only sees the connection drop).
            throw new ProtocolException(ProtocolErrorCodes.InsufficientStorage,
                $"Cannot write to '{_options.ReceivedFilesDirectory}': {ex.Message}", ex);
        }

        var session = new Session(transferId, safeName, size, partPath, finalPath, stream, peerName, remoteEndpoint);
        _sessions[connection] = session;
        _logger?.LogInformation("Receiving {File} ({Size} bytes) from {Peer} into {Path}", safeName, size, peerName ?? remoteEndpoint, partPath);
    }

    private async Task EnsureApprovedAsync(IncomingFileOffer offer, CancellationToken ct)
    {
        var approve = ApproveIncoming;
        if (approve is null)
            throw new ProtocolException(ProtocolErrorCodes.Forbidden, "this device is not accepting files");

        bool accepted;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(_options.IncomingApprovalTimeout);
            try
            {
                // WaitAsync: the timeout applies even if the approver ignores the token.
                accepted = await approve(offer, timeout.Token).WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger?.LogInformation("Nobody answered for {File} from {Peer}", offer.FileName, offer.PeerName ?? offer.RemoteEndpoint);
                throw new ProtocolException(ProtocolErrorCodes.Forbidden,
                    $"nobody accepted the file within {_options.IncomingApprovalTimeout.TotalSeconds:0} s");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "The approval prompt failed; declining {File}", offer.FileName);
                accepted = false;
            }
        }

        if (!accepted)
        {
            _logger?.LogInformation("Declined {File} from {Peer}", offer.FileName, offer.PeerName ?? offer.RemoteEndpoint);
            throw new ProtocolException(ProtocolErrorCodes.Forbidden, "declined on the receiving device");
        }
    }

    public async Task WriteChunkAsync(TcpClient connection, long offset, byte[] data, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(connection, out var s))
            throw new ProtocolException(ProtocolErrorCodes.Conflict, "Data received without an active FileRequest.");
        if (offset != s.BytesWritten)
            throw new ProtocolException(ProtocolErrorCodes.Conflict, $"Unexpected chunk offset {offset}; expected {s.BytesWritten}.");
        if (s.BytesWritten + data.Length > s.ExpectedSize)
            throw new ProtocolException(ProtocolErrorCodes.BadRequest, "More data than the size announced in FileRequest.");

        try
        {
            await s.Stream.WriteAsync(data, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProtocolException(ProtocolErrorCodes.InsufficientStorage, $"Could not write the file: {ex.Message}", ex);
        }

        s.Hash.AppendData(data);
        s.BytesWritten += data.Length;

        var now = s.Clock.Elapsed;
        if (now - s.LastProgressAt >= ProgressInterval || s.BytesWritten == s.ExpectedSize)
        {
            s.LastProgressAt = now;
            Raise(Progress, new ReceiveProgressEventArgs
            {
                TransferId = s.TransferId,
                FileName = s.FileName,
                BytesReceived = s.BytesWritten,
                TotalBytes = s.ExpectedSize,
                PeerName = s.PeerName
            }, nameof(Progress));
        }
    }

    /// <summary>Verifies size and SHA-256 and publishes the file. Throws <see cref="ProtocolException"/> if anything does not match.</summary>
    public async Task<FileReceivedEventArgs> CompleteAsync(TcpClient connection, Guid transferId, byte[] expectedSha256, CancellationToken ct = default)
    {
        if (!_sessions.TryRemove(connection, out var s))
            throw new ProtocolException(ProtocolErrorCodes.Conflict, "TransferComplete received without an active FileRequest.");

        FileReceivedEventArgs received;
        try
        {
            await s.Stream.FlushAsync(ct);
            await s.Stream.DisposeAsync();

            if (transferId != s.TransferId)
                throw new ProtocolException(ProtocolErrorCodes.Conflict, $"TransferComplete for {transferId} but the active transfer is {s.TransferId}.");
            if (s.BytesWritten != s.ExpectedSize)
                throw new ProtocolException(ProtocolErrorCodes.IntegrityFailed, $"Received {s.BytesWritten} of {s.ExpectedSize} bytes.");

            var actual = s.Hash.GetHashAndReset();
            if (expectedSha256.Length != actual.Length || !CryptographicOperations.FixedTimeEquals(actual, expectedSha256))
                throw new ProtocolException(ProtocolErrorCodes.IntegrityFailed, "SHA-256 of the received file does not match the sender's.");

            string finalPath;
            try
            {
                lock (_pathLock)
                {
                    finalPath = File.Exists(s.FinalPath) ? GetUniquePath(Path.GetDirectoryName(s.FinalPath)!, s.FileName) : s.FinalPath;
                    File.Move(s.PartPath, finalPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ProtocolException(ProtocolErrorCodes.Internal, $"Could not save the file: {ex.Message}", ex);
            }

            received = new FileReceivedEventArgs
            {
                TransferId = s.TransferId,
                FileName = Path.GetFileName(finalPath),
                FullPath = finalPath,
                Size = s.BytesWritten,
                Sha256 = Convert.ToHexString(actual).ToLowerInvariant(),
                PeerName = s.PeerName,
                RemoteEndpoint = s.RemoteEndpoint
            };
        }
        catch (Exception ex)
        {
            try { await s.Stream.DisposeAsync(); } catch { /* already disposed or broken */ }
            Discard(s, ex is ProtocolException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            s.Hash.Dispose();
        }

        // The file is already in place: a failing subscriber must not turn this into an error for the sender.
        _logger?.LogInformation("Received {File} ({Size} bytes, SHA-256 OK) -> {Path}", received.FileName, received.Size, received.FullPath);
        Raise(FileReceived, received, nameof(FileReceived));
        return received;
    }

    /// <summary>Drops an unfinished transfer on this connection (disconnect, protocol error…) and deletes the partial file.</summary>
    public void Abort(TcpClient connection, string reason)
    {
        if (_sessions.TryRemove(connection, out var s))
        {
            try { s.Stream.Dispose(); } catch { /* ignore */ }
            s.Hash.Dispose();
            Discard(s, reason);
        }
    }

    private void Discard(Session s, string reason)
    {
        try { if (File.Exists(s.PartPath)) File.Delete(s.PartPath); } catch { /* best effort */ }
        _logger?.LogWarning("Transfer of {File} aborted ({Received}/{Expected} bytes): {Reason}", s.FileName, s.BytesWritten, s.ExpectedSize, reason);
        Raise(ReceiveFailed, new ReceiveFailedEventArgs { TransferId = s.TransferId, FileName = s.FileName, Reason = reason }, nameof(ReceiveFailed));
    }

    /// <summary>Calls every subscriber; one that throws is logged and does not affect the transfer or the others.</summary>
    private void Raise<T>(EventHandler<T>? handler, T args, string eventName)
    {
        if (handler is null)
            return;

        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<T>)subscriber)(this, args);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "A {Event} subscriber threw", eventName);
            }
        }
    }

    /// <summary>
    /// Keeps only the file name (no directories → no path traversal), removes characters invalid on
    /// Windows/Android and avoids Windows device names (CON, NUL, COM1…), which cannot be used as files.
    /// </summary>
    public static string SanitizeFileName(string? fileName)
    {
        var name = (fileName ?? string.Empty).Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var chars = name.Select(c => c < 32 || ForbiddenFileNameChars.Contains(c) || Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray();
        name = new string(chars).Trim().TrimEnd('.', ' ');
        if (name is "" or "." or "..")
            name = "received-file";
        if (name.Length > 180)
        {
            var ext = Path.GetExtension(name);
            name = name[..(180 - Math.Min(ext.Length, 20))] + ext[..Math.Min(ext.Length, 20)];
        }

        // "nul.txt" and "con.part" are devices on Windows: the part before the first dot decides.
        var stem = name.Split('.')[0].TrimEnd(' ');
        if (WindowsReservedNames.Contains(stem))
            name = "_" + name;

        return name;
    }

    private static string GetUniquePath(string directory, string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var candidate = Path.Combine(directory, fileName);
        for (var i = 1; File.Exists(candidate) || File.Exists(candidate + ".part"); i++)
            candidate = Path.Combine(directory, $"{baseName} ({i}){ext}");
        return candidate;
    }

    private sealed class Session
    {
        public Session(Guid transferId, string fileName, long expectedSize, string partPath, string finalPath,
            FileStream stream, string? peerName, string? remoteEndpoint)
        {
            TransferId = transferId;
            FileName = fileName;
            ExpectedSize = expectedSize;
            PartPath = partPath;
            FinalPath = finalPath;
            Stream = stream;
            PeerName = peerName;
            RemoteEndpoint = remoteEndpoint;
        }

        public Guid TransferId { get; }
        public string FileName { get; }
        public long ExpectedSize { get; }
        public string PartPath { get; }
        public string FinalPath { get; }
        public FileStream Stream { get; }
        public string? PeerName { get; }
        public string? RemoteEndpoint { get; }
        public IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
        public TimeSpan LastProgressAt { get; set; }
        public long BytesWritten { get; set; }
    }
}
