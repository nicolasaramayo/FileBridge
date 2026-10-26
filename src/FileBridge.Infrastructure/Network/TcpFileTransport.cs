namespace FileBridge.Infrastructure.Network;

using System.IO;
using System.Security.Cryptography;
using System.Threading.Channels;
using FileBridge.Domain.Entities;
using FileBridge.Domain.Enums;
using FileBridge.Domain.Interfaces;
using Microsoft.Extensions.Logging;

/// <summary>
/// Sends a file to another FileBridge device over its own TCP connection:
/// Handshake → FileRequest (wait Ack) → Data chunks → TransferComplete with SHA-256 (wait Ack).
/// It only reports success once the receiver confirmed size and hash.
/// </summary>
public sealed class TcpFileTransport : IFileTransport
{
    private readonly ProtocolSerializer _serializer = new();
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger? _logger;

    public TcpFileTransport(FileBridgeOptions? options = null, ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<TcpFileTransport>();
        LocalDeviceName = options?.DeviceName ?? Environment.MachineName;
    }

    public string LocalDeviceName { get; set; }
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long to wait for the FileRequest answer: the user on the other device has to accept the file.
    /// Longer than the receiver's own approval timeout (60 s by default), so its "nobody answered" arrives first.
    /// </summary>
    public TimeSpan AcceptTimeout { get; set; } = TimeSpan.FromSeconds(90);
    public int ChunkSize { get; set; } = ProtocolSerializer.MaxChunkSize;

    public async Task<FileTransferResult> SendFileAsync(
        Device target,
        string localFilePath,
        Guid transferId,
        IProgress<long>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrWhiteSpace(target.IpAddress) || target.Port <= 0)
            throw new ArgumentException($"Device '{target.Name}' has no usable address ({target.IpAddress}:{target.Port}).", nameof(target));

        var file = new FileInfo(localFilePath);
        if (!file.Exists)
            throw new FileNotFoundException("File to send was not found.", localFilePath);
        if (ChunkSize <= 0 || ChunkSize > ProtocolSerializer.MaxChunkSize)
            throw new InvalidOperationException($"ChunkSize must be between 1 and {ProtocolSerializer.MaxChunkSize}.");

        using var client = new TcpFileClient(_loggerFactory?.CreateLogger<TcpFileClient>()) { ConnectTimeout = ConnectTimeout };
        var responses = Channel.CreateUnbounded<ProtocolMessage>();
        client.OnDataReceived += (_, frame) =>
        {
            try
            {
                var message = _serializer.Deserialize(frame);
                if (message != null)
                    responses.Writer.TryWrite(message);
            }
            catch (Exception ex)
            {
                responses.Writer.TryComplete(ex);
            }
        };
        client.OnDisconnected += (_, _) =>
            responses.Writer.TryComplete(new IOException("The receiving device closed the connection."));

        _logger?.LogInformation("Sending {File} ({Size} bytes) to {Device} at {Ip}:{Port}",
            file.Name, file.Length, target.Name, target.IpAddress, target.Port);

        await client.ConnectAsync(target.IpAddress, target.Port, ct);

        try
        {
            await SendAsync(client, MessageType.Handshake, _serializer.BuildHandshakePayload(LocalDeviceName, Array.Empty<byte>()), ct);
            await ExpectAsync(responses.Reader, MessageType.Handshake, ResponseTimeout, ct);

            await SendAsync(client, MessageType.FileRequest,
                _serializer.BuildFileRequestPayload(transferId, file.Name, file.Length, 0, false, 0), ct);
            _logger?.LogInformation("Waiting for {Device} to accept {File}", target.Name, file.Name);
            await ExpectAckAsync(responses.Reader, MessageType.FileRequest, AcceptTimeout, ct);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long offset = 0;
            var chunkIndex = 0;
            var buffer = new byte[ChunkSize];

            await using (var stream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            {
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, ct);
                    if (read == 0)
                        break;

                    hash.AppendData(buffer, 0, read);
                    await SendAsync(client, MessageType.Data, _serializer.BuildDataPayload(chunkIndex++, offset, buffer.AsSpan(0, read)), ct);
                    offset += read;
                    progress?.Report(offset);

                    // The receiver answers only on failure while data flows: stop as soon as it complains.
                    ThrowIfReceiverReportedError(responses.Reader);
                }
            }

            if (offset != file.Length)
                throw new IOException($"The file changed while it was being sent ({offset} bytes read, {file.Length} expected).");

            var sha256 = hash.GetHashAndReset();
            await SendAsync(client, MessageType.TransferComplete, _serializer.BuildTransferCompletePayload(transferId, sha256), ct);
            await ExpectAckAsync(responses.Reader, MessageType.TransferComplete, ResponseTimeout, ct);

            try { await SendAsync(client, MessageType.Disconnect, Array.Empty<byte>(), ct); } catch { /* best effort */ }

            var result = new FileTransferResult(offset, Convert.ToHexString(sha256).ToLowerInvariant());
            _logger?.LogInformation("Sent {File} to {Device}: {Bytes} bytes, SHA-256 confirmed by the receiver", file.Name, target.Name, offset);
            return result;
        }
        catch (IOException ex)
        {
            // If the receiver closed the connection after sending an Error, report its reason instead of a bare socket error.
            ThrowIfReceiverReportedError(responses.Reader);
            throw new IOException($"Transfer to {target.Name} ({target.IpAddress}:{target.Port}) failed: {ex.Message}", ex);
        }
    }

    private Task SendAsync(TcpFileClient client, MessageType type, byte[] payload, CancellationToken ct) =>
        client.SendAsync(_serializer.Serialize(new ProtocolMessage { Type = type, Payload = payload }), ct);

    private async Task<ProtocolMessage> ExpectAsync(ChannelReader<ProtocolMessage> reader, MessageType expected, TimeSpan wait, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wait);
        try
        {
            while (true)
            {
                var message = await reader.ReadAsync(timeout.Token);
                if (message.Type == MessageType.Error)
                {
                    var (code, text) = _serializer.ParseErrorPayload(message.Payload);
                    throw new RemoteProtocolException(code, text);
                }
                if (message.Type == expected)
                    return message;

                _logger?.LogInformation("Ignoring {Type} while waiting for {Expected}", message.Type, expected);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"No {expected} response from the receiving device within {wait.TotalSeconds:0} s.");
        }
        catch (ChannelClosedException ex)
        {
            throw ex.InnerException ?? new IOException("The receiving device closed the connection.");
        }
    }

    private async Task ExpectAckAsync(ChannelReader<ProtocolMessage> reader, MessageType acknowledged, TimeSpan wait, CancellationToken ct)
    {
        while (true)
        {
            var ack = await ExpectAsync(reader, MessageType.Ack, wait, ct);
            var (messageId, status) = _serializer.ParseAckPayload(ack.Payload);
            if (messageId != (ulong)acknowledged)
                continue; // an ACK for something else
            if (status != 1)
                throw new RemoteProtocolException(ProtocolErrorCodes.Internal, $"{acknowledged} was not accepted (status {status}).");
            return;
        }
    }

    private void ThrowIfReceiverReportedError(ChannelReader<ProtocolMessage> reader)
    {
        while (reader.TryRead(out var message))
        {
            if (message.Type == MessageType.Error)
            {
                var (code, text) = _serializer.ParseErrorPayload(message.Payload);
                throw new RemoteProtocolException(code, text);
            }
        }
    }
}
