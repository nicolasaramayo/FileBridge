namespace FileBridge.Infrastructure.Network;

using System.IO;
using System.Net.Sockets;
using System.Text;
using FileBridge.Domain.Entities;
using FileBridge.Domain.Enums;
using FileBridge.Domain.Interfaces;

public sealed class TransferProtocolHandler
{
    private readonly ProtocolSerializer _serializer;
    private readonly ITransferService? _transferService;
    private readonly ILogger? _logger;

    public TransferProtocolHandler(ProtocolSerializer serializer, ITransferService? transferService, ILogger? logger = null)
    {
        _serializer = serializer;
        _transferService = transferService;
        _logger = logger;
    }

    private static async Task SendMessageAsync(TcpClient client, ProtocolMessage message, CancellationToken ct)
    {
        var data = BitConverter.GetBytes(message.Length);
        await client.GetStream().WriteAsync(data.AsMemory(), ct);
        await client.GetStream().WriteAsync(message.Payload.AsMemory(), ct);
        await client.GetStream().FlushAsync(ct);
    }

    public async Task HandleMessageAsync(ProtocolMessage message, TcpClient client, CancellationToken ct = default)
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
                await HandleListFilesAsync(message, client, ct);
                break;
            case MessageType.FileRequest:
                await HandleFileRequestAsync(message, client, ct);
                break;
            case MessageType.Data:
                await HandleDataAsync(message, client, ct);
                break;
            case MessageType.Pause:
                await HandlePauseAsync(message, client, ct);
                break;
            case MessageType.Resume:
                await HandleResumeAsync(message, client, ct);
                break;
            case MessageType.Ack:
                await HandleAckAsync(message, client, ct);
                break;
            case MessageType.Error:
                await HandleErrorAsync(message, client, ct);
                break;
            case MessageType.Disconnect:
                await HandleDisconnectAsync(message, client, ct);
                break;
            default:
                _logger?.LogWarning("Unknown message type: {Type}", message.Type);
                break;
        }
    }

    private async Task HandleHandshakeAsync(ProtocolMessage message, TcpClient client, CancellationToken ct)
    {
        try
        {
            var (deviceName, publicKey) = _serializer.ParseHandshakePayload(message.Payload);
            _logger?.LogInformation("Handshake received from {Name}", deviceName);

            // Respond with own handshake
            var responsePayload = _serializer.BuildHandshakePayload(
                Environment.MachineName,
                Array.Empty<byte>());
            var response = new ProtocolMessage
            {
                Type = MessageType.Handshake,
                Payload = responsePayload
            };
            await SendMessageAsync(client, response, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error processing handshake");
        }
    }

    private async Task HandleConnectAsync(ProtocolMessage message, TcpClient client, CancellationToken ct)
    {
        try
        {
            using var ms = new MemoryStream(message.Payload);
            using var reader = new BinaryReader(ms);

            var deviceId = new Guid(reader.ReadBytes(16));
            var tokenLen = message.Payload.Length - 16;
            var pairingToken = Encoding.UTF8.GetString(reader.ReadBytes(tokenLen));

            _logger?.LogInformation("Connect request from device {Id}", deviceId);

            // Send ACK
            var ackPayload = _serializer.BuildAckPayload(0, 1); // status=1 (success)
            var ackMsg = new ProtocolMessage
            {
                Type = MessageType.Ack,
                Payload = ackPayload
            };
            await SendMessageAsync(client, ackMsg, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error processing connect");
        }
    }

    private Task HandleListFilesAsync(ProtocolMessage message, TcpClient client, CancellationToken ct)
    {
        // This would typically be handled by the application layer
        // For now, return empty file list
        _logger?.LogInformation("ListFiles request received");

        return Task.CompletedTask;
    }

    private Task HandleFileRequestAsync(ProtocolMessage message, TcpClient client, CancellationToken ct)
    {
        try
        {
            using var ms = new MemoryStream(message.Payload);
            using var reader = new BinaryReader(ms);

            var fileItemId = new Guid(reader.ReadBytes(16));
            var fileNameLen = message.Payload.Length - 16 - 8 - 4 - 1 - 8;
            var fileName = Encoding.UTF8.GetString(reader.ReadBytes(fileNameLen > 0 ? fileNameLen : 0));
            var fileSize = reader.ReadInt64();
            var chunkIndex = reader.ReadInt32();
            var resume = reader.ReadByte() != 0;
            var resumeOffset = reader.ReadInt64();

            _logger?.LogInformation("FileRequest: {FileName} ({Size} bytes), resume={Resume}", fileName, fileSize, resume);

            // Start receiving the file
            if (_transferService != null)
            {
                var job = new TransferJob
                {
                    Id = Guid.NewGuid(),
                    FileItemId = fileItemId,
                    Status = TransferStatus.InProgress,
                    Direction = TransferDirection.Receive,
                    TotalBytes = fileSize,
                    ChunkSize = ProtocolSerializer.MaxChunkSize
                };

                _ = _transferService.ReceiveFileAsync(job, ct);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error processing file request");
        }

        return Task.CompletedTask;
    }

    private async Task HandleDataAsync(ProtocolMessage message, TcpClient client, CancellationToken ct)
    {
        try
        {
            var (chunkIndex, offset, data, checksum) = _serializer.ParseDataPayload(message.Payload);

            // Verify checksum
            var computed = Crc32.Compute(data);
            if (computed != checksum)
            {
                _logger?.LogWarning("Data chunk {Index} checksum mismatch", chunkIndex);
                return;
            }

            _logger?.LogInformation("Data chunk {Index} received ({Size} bytes)", chunkIndex, data.Length);

            // Send ACK
            var ackPayload = _serializer.BuildAckPayload((ulong)chunkIndex, 1);
            var ackMsg = new ProtocolMessage
            {
                Type = MessageType.Ack,
                Payload = ackPayload
            };
            await SendMessageAsync(client, ackMsg, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error processing data message");
        }
    }

    private Task HandlePauseAsync(ProtocolMessage message, TcpClient client, CancellationToken ct)
    {
        try
        {
            var (jobId, bytesTransferred) = _serializer.ParsePausePayload(message.Payload);
            _logger?.LogInformation("Pause request for job {Id} at {Bytes} bytes", jobId, bytesTransferred);

            if (_transferService != null)
            {
                _ = _transferService.PauseAsync(jobId.ToString());
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error processing pause");
        }

        return Task.CompletedTask;
    }

    private Task HandleResumeAsync(ProtocolMessage message, TcpClient client, CancellationToken ct)
    {
        try
        {
            using var ms = new MemoryStream(message.Payload);
            using var reader = new BinaryReader(ms);

            var jobId = new Guid(reader.ReadBytes(16));
            var bytesTransferred = reader.ReadInt64();

            _logger?.LogInformation("Resume request for job {Id} at {Bytes} bytes", jobId, bytesTransferred);

            if (_transferService != null)
            {
                _ = _transferService.ResumeAsync(jobId.ToString());
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error processing resume");
        }

        return Task.CompletedTask;
    }

    private Task HandleAckAsync(ProtocolMessage message, TcpClient client, CancellationToken ct)
    {
        try
        {
            var (messageId, status) = _serializer.ParseAckPayload(message.Payload);
            _logger?.LogInformation("ACK received for message {Id}: status={Status}", messageId, status);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error processing ACK");
        }

        return Task.CompletedTask;
    }

    private Task HandleErrorAsync(ProtocolMessage message, TcpClient client, CancellationToken ct)
    {
        try
        {
            using var ms = new MemoryStream(message.Payload);
            using var reader = new BinaryReader(ms);

            var code = reader.ReadUInt16();
            var msgLen = reader.ReadByte();
            var errorMsg = Encoding.UTF8.GetString(reader.ReadBytes(msgLen));

            _logger?.LogWarning("Error received: [{Code}] {Message}", code, errorMsg);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error processing error message");
        }

        return Task.CompletedTask;
    }

    private Task HandleDisconnectAsync(ProtocolMessage message, TcpClient client, CancellationToken ct)
    {
        _logger?.LogInformation("Disconnect message received");
        try { client.Close(); } catch { /* ignore */ }
        return Task.CompletedTask;
    }
}
