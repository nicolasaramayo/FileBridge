namespace FileBridge.Infrastructure.Network;

using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using FileBridge.Domain.Enums;
using FileBridge.Domain.Interfaces;
using Microsoft.Extensions.Logging;

/// <summary>
/// Server-side protocol handling (the device that receives). Every reply is a complete serialized
/// <see cref="ProtocolMessage"/> sent through <see cref="TcpServer"/>, i.e. with the same framing the
/// client reads. Incoming files are written by <see cref="FileReceiver"/>.
/// </summary>
public sealed class TransferProtocolHandler
{
    private readonly ProtocolSerializer _serializer;
    private readonly TcpServer _server;
    private readonly FileReceiver _receiver;
    private readonly ITransferService? _transferService;
    private readonly ILogger? _logger;
    private readonly string _localDeviceName;
    private readonly ConcurrentDictionary<TcpClient, string> _peerNames = new();

    public TransferProtocolHandler(
        ProtocolSerializer serializer,
        TcpServer server,
        FileReceiver receiver,
        ITransferService? transferService = null,
        ILogger? logger = null,
        string? localDeviceName = null)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
        _transferService = transferService;
        _logger = logger;
        _localDeviceName = string.IsNullOrWhiteSpace(localDeviceName) ? Environment.MachineName : localDeviceName;
    }

    /// <summary>Name the peer sent in its Handshake, if any.</summary>
    public string? GetPeerName(TcpClient client) => _peerNames.TryGetValue(client, out var name) ? name : null;

    public async Task HandleMessageAsync(ProtocolMessage message, TcpClient client, CancellationToken ct = default)
    {
        try
        {
            switch (message.Type)
            {
                case MessageType.Handshake:
                    await HandleHandshakeAsync(message, client, ct);
                    break;
                case MessageType.Connect:
                    await HandleConnectAsync(message, client, ct);
                    break;
                case MessageType.ListFiles:
                    await SendErrorAsync(client, ProtocolErrorCodes.NotImplemented, "ListFiles is not implemented yet.", ct);
                    break;
                case MessageType.FileRequest:
                    await HandleFileRequestAsync(message, client, ct);
                    break;
                case MessageType.Data:
                    await HandleDataAsync(message, client, ct);
                    break;
                case MessageType.TransferComplete:
                    await HandleTransferCompleteAsync(message, client, ct);
                    break;
                case MessageType.Pause:
                    HandlePause(message);
                    break;
                case MessageType.Resume:
                    HandleResume(message);
                    break;
                case MessageType.Ack:
                    HandleAck(message);
                    break;
                case MessageType.Error:
                    HandleError(message);
                    break;
                case MessageType.Disconnect:
                    _logger?.LogInformation("Disconnect message received");
                    try { client.Close(); } catch { /* ignore */ }
                    break;
                default:
                    _logger?.LogWarning("Unknown message type: {Type}", message.Type);
                    break;
            }
        }
        catch (ProtocolException ex)
        {
            // The transfer on this connection is no longer valid: tell the sender why and drop it.
            _logger?.LogWarning("Protocol error [{Code}] on {Type}: {Message}", ex.Code, message.Type, ex.Message);
            _receiver.Abort(client, ex.Message);
            await TrySendErrorAsync(client, ex.Code, ex.Message, ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentException)
        {
            _logger?.LogWarning(ex, "Malformed {Type} message", message.Type);
            _receiver.Abort(client, $"Malformed {message.Type} message");
            await TrySendErrorAsync(client, ProtocolErrorCodes.BadRequest, $"Malformed {message.Type} message.", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anything else: drop this transfer, tell the sender, keep the connection usable.
            _logger?.LogError(ex, "Unexpected error handling {Type}", message.Type);
            _receiver.Abort(client, $"internal error: {ex.Message}");
            await TrySendErrorAsync(client, ProtocolErrorCodes.Internal, "Internal error on the receiving device.", ct);
        }
    }

    /// <summary>Called by the owner when the TCP connection ends.</summary>
    public void OnConnectionClosed(TcpClient client)
    {
        _peerNames.TryRemove(client, out _);
        _receiver.Abort(client, "connection closed before the transfer completed");
    }

    private async Task HandleHandshakeAsync(ProtocolMessage message, TcpClient client, CancellationToken ct)
    {
        var (deviceName, _) = _serializer.ParseHandshakePayload(message.Payload);
        _peerNames[client] = deviceName;
        _logger?.LogInformation("Handshake received from {Name}", deviceName);

        // Respond with own handshake
        await SendAsync(client, MessageType.Handshake, _serializer.BuildHandshakePayload(_localDeviceName, Array.Empty<byte>()), ct);
    }

    private async Task HandleConnectAsync(ProtocolMessage message, TcpClient client, CancellationToken ct)
    {
        if (message.Payload.Length < 16)
            throw new InvalidDataException("Connect payload is too short.");

        var deviceId = new Guid(message.Payload.AsSpan(0, 16));
        var pairingToken = Encoding.UTF8.GetString(message.Payload, 16, message.Payload.Length - 16);
        _logger?.LogInformation("Connect request from device {Id}", deviceId);

        // NOTE: pairing tokens are not validated yet (see PairingService).
        await SendAckAsync(client, MessageType.Connect, ct);
    }

    private async Task HandleFileRequestAsync(ProtocolMessage message, TcpClient client, CancellationToken ct)
    {
        var request = _serializer.ParseFileRequestPayload(message.Payload);
        _logger?.LogInformation("FileRequest: {FileName} ({Size} bytes), resume={Resume}", request.FileName, request.FileSize, request.Resume);

        if (request.Resume || request.ResumeOffset != 0)
            throw new ProtocolException(ProtocolErrorCodes.NotImplemented, "Resuming a transfer is not implemented yet.");

        // May wait for the user to accept the file (FileReceiver.ApproveIncoming); throws ProtocolException(403) if declined.
        await _receiver.BeginAsync(client, request.FileItemId, request.FileName, request.FileSize, GetPeerName(client), ct);
        await SendAckAsync(client, MessageType.FileRequest, ct); // "accepted, send the data"
    }

    private async Task HandleDataAsync(ProtocolMessage message, TcpClient client, CancellationToken ct)
    {
        var (chunkIndex, offset, data, checksum) = _serializer.ParseDataPayload(message.Payload);

        if (Crc32.Compute(data) != checksum)
            throw new ProtocolException(ProtocolErrorCodes.IntegrityFailed, $"Data chunk {chunkIndex} checksum mismatch.");

        // No per-chunk ACK: TCP already orders and acknowledges the bytes, and the final SHA-256 check
        // in TransferComplete verifies the whole file.
        await _receiver.WriteChunkAsync(client, offset, data, ct);
    }

    private async Task HandleTransferCompleteAsync(ProtocolMessage message, TcpClient client, CancellationToken ct)
    {
        var (fileItemId, sha256) = _serializer.ParseTransferCompletePayload(message.Payload);
        await _receiver.CompleteAsync(client, fileItemId, sha256, ct);
        await SendAckAsync(client, MessageType.TransferComplete, ct);
    }

    private void HandlePause(ProtocolMessage message)
    {
        var (jobId, bytesTransferred) = _serializer.ParsePausePayload(message.Payload);
        _logger?.LogInformation("Pause request for job {Id} at {Bytes} bytes", jobId, bytesTransferred);
        if (_transferService != null)
            _ = _transferService.PauseAsync(jobId.ToString());
    }

    private void HandleResume(ProtocolMessage message)
    {
        var (jobId, bytesTransferred) = _serializer.ParsePausePayload(message.Payload); // same layout as Pause
        _logger?.LogInformation("Resume request for job {Id} at {Bytes} bytes", jobId, bytesTransferred);
        if (_transferService != null)
            _ = _transferService.ResumeAsync(jobId.ToString());
    }

    private void HandleAck(ProtocolMessage message)
    {
        var (messageId, status) = _serializer.ParseAckPayload(message.Payload);
        _logger?.LogInformation("ACK received for {Id}: status={Status}", messageId, status);
    }

    private void HandleError(ProtocolMessage message)
    {
        var (code, errorMessage) = _serializer.ParseErrorPayload(message.Payload);
        _logger?.LogWarning("Error received: [{Code}] {Message}", code, errorMessage);
    }

    // ACK convention: MessageId = the MessageType being acknowledged, Status 1 = OK.
    private Task SendAckAsync(TcpClient client, MessageType acknowledged, CancellationToken ct) =>
        SendAsync(client, MessageType.Ack, _serializer.BuildAckPayload((ulong)acknowledged, 1), ct);

    private Task SendErrorAsync(TcpClient client, ushort code, string text, CancellationToken ct) =>
        SendAsync(client, MessageType.Error, _serializer.BuildErrorPayload(code, text), ct);

    private async Task TrySendErrorAsync(TcpClient client, ushort code, string text, CancellationToken ct)
    {
        try { await SendErrorAsync(client, code, text, ct); }
        catch (Exception ex) { _logger?.LogInformation("Could not send Error to peer: {Reason}", ex.Message); }
    }

    private Task SendAsync(TcpClient client, MessageType type, byte[] payload, CancellationToken ct)
    {
        var message = new ProtocolMessage { Type = type, Payload = payload };
        return _server.SendToClientAsync(client, _serializer.Serialize(message), ct);
    }
}
