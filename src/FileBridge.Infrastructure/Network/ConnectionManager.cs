namespace FileBridge.Infrastructure.Network;

using System.IO;
using System.Net.Sockets;
using FileBridge.Domain.Entities;
using FileBridge.Domain.Enums;
using FileBridge.Domain.Interfaces;
using Microsoft.Extensions.Logging;

public sealed class ConnectionManager : IDisposable
{
    private readonly TcpServer _server;
    private readonly TcpFileClient _client;
    private readonly Dictionary<string, TcpClient> _activeConnections = new();
    private readonly Dictionary<string, Device> _devices = new();
    private readonly ProtocolSerializer _serializer;
    private readonly ITransferService? _transferService;
    private readonly ILogger? _logger;
    private readonly TransferProtocolHandler _handler;
    private readonly string _localDeviceName;
    private readonly Func<TcpClient, byte[], CancellationToken, Task> _frameHandler;
    private readonly object _lock = new();
    private bool _disposed;

    public IReadOnlyList<Device> ConnectedDevices
    {
        get
        {
            lock (_lock)
            {
                return _devices.Values.ToList();
            }
        }
    }

    public ConnectionManager(
        TcpServer server,
        TcpFileClient client,
        FileReceiver receiver,
        ITransferService? transferService = null,
        ILogger<ConnectionManager>? logger = null,
        FileBridgeOptions? options = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentNullException.ThrowIfNull(receiver);
        _transferService = transferService;
        _logger = logger;
        _localDeviceName = options?.DeviceName ?? Environment.MachineName;
        _serializer = new ProtocolSerializer();
        _handler = new TransferProtocolHandler(_serializer, _server, receiver, _transferService, _logger, _localDeviceName);

        _server.OnClientConnected += OnServerClientConnected;
        _server.OnClientDisconnected += OnServerClientDisconnected;
        // Incoming frames are processed in order, one at a time per connection (see TcpServer.FrameHandler).
        _frameHandler = HandleServerFrameAsync;
        _server.FrameHandler = _frameHandler;
        // Replies to our outbound connection (handshake answer, ACKs, errors) were silently dropped before.
        _client.OnDataReceived += OnClientDataReceived;
    }

    public async Task<bool> ConnectToDeviceAsync(Device device, CancellationToken ct = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ConnectionManager));

        try
        {
            await _client.ConnectAsync(device.IpAddress, device.Port, ct);

            // Send handshake
            var handshakePayload = _serializer.BuildHandshakePayload(_localDeviceName, Array.Empty<byte>());
            var handshakeMsg = new ProtocolMessage
            {
                Type = MessageType.Handshake,
                Payload = handshakePayload
            };

            var data = _serializer.Serialize(handshakeMsg);
            await _client.SendAsync(data, ct);

            lock (_lock)
            {
                _devices[device.Id.ToString()] = device;
                // Store the underlying TcpClient for outbound connection tracking
                var underlyingClient = _client.GetClient();
                if (underlyingClient != null)
                {
                    _activeConnections[device.Id.ToString()] = underlyingClient;
                }
            }

            _logger?.LogInformation("Connected to device: {Name}", device.Name);
            OnDeviceConnected?.Invoke(this, new DeviceConnectedEventArgs(device));
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to connect to device {Name} at {Ip}:{Port}", device.Name, device.IpAddress, device.Port);
            return false;
        }
    }

    public async Task DisconnectAsync(string deviceId)
    {
        TcpClient? client;
        Device? device;

        lock (_lock)
        {
            if (!_activeConnections.TryGetValue(deviceId, out client))
                return;
            _activeConnections.Remove(deviceId);
            device = _devices.GetValueOrDefault(deviceId);
            _devices.Remove(deviceId);
        }

        if (device != null)
        {
            try
            {
                var msg = new ProtocolMessage { Type = MessageType.Disconnect };
                await SendFrameAsync(client, _serializer.Serialize(msg), CancellationToken.None);
            }
            catch { /* ignore */ }
        }

        try { client?.Close(); } catch { /* ignore */ }

        if (device != null)
        {
            OnDeviceDisconnected?.Invoke(this, new DeviceDisconnectedEventArgs(device));
            _logger?.LogInformation("Disconnected from device: {Id}", deviceId);
        }
    }

    public async Task SendMessageAsync(string deviceId, ProtocolMessage message, CancellationToken ct = default)
    {
        TcpClient? client;
        lock (_lock)
        {
            if (!_activeConnections.TryGetValue(deviceId, out client))
                throw new InvalidOperationException($"No active connection to device {deviceId}");
        }

        await SendFrameAsync(client, _serializer.Serialize(message), ct);
    }

    /// <summary>
    /// Outbound connections are owned by <see cref="TcpFileClient"/> (its send lock); inbound ones by
    /// <see cref="TcpServer"/>. Writing to an outbound socket through the server (as before) skipped both
    /// the length prefix and the lock.
    /// </summary>
    private Task SendFrameAsync(TcpClient client, byte[] frame, CancellationToken ct) =>
        ReferenceEquals(client, _client.GetClient())
            ? _client.SendAsync(frame, ct)
            : _server.SendToClientAsync(client, frame, ct);

    public IReadOnlyList<Device> GetConnectedDevices()
    {
        lock (_lock)
        {
            return _devices.Values.ToList();
        }
    }

    private void OnServerClientConnected(object? sender, ClientConnectedEventArgs e)
    {
        _logger?.LogInformation("Incoming connection from {Endpoint}", e.Client.Client.RemoteEndPoint);
    }

    private void OnServerClientDisconnected(object? sender, ClientDisconnectedEventArgs e)
    {
        _handler.OnConnectionClosed(e.Client);

        string? deviceId;
        Device? device;

        lock (_lock)
        {
            deviceId = _activeConnections.FirstOrDefault(kv => kv.Value == e.Client).Key;
            if (string.IsNullOrEmpty(deviceId))
                return;

            device = _devices.GetValueOrDefault(deviceId);
            _activeConnections.Remove(deviceId);
            _devices.Remove(deviceId);
        }

        if (device != null)
        {
            OnDeviceDisconnected?.Invoke(this, new DeviceDisconnectedEventArgs(device));
        }
    }

    private async Task HandleServerFrameAsync(TcpClient client, byte[] frame, CancellationToken ct)
    {
        ProtocolMessage? message;
        try
        {
            message = _serializer.Deserialize(frame);
        }
        catch (InvalidDataException ex)
        {
            _logger?.LogWarning("Dropping invalid frame from {Endpoint}: {Reason}", client.Client?.RemoteEndPoint, ex.Message);
            return;
        }

        if (message == null)
        {
            _logger?.LogWarning("Dropping malformed frame ({Length} bytes)", frame.Length);
            return;
        }

        RaiseMessageReceived(client, message);

        // Always handled now (before, nothing ran when ITransferService was not registered in DI).
        await _handler.HandleMessageAsync(message, client, ct);
    }

    private void OnClientDataReceived(object? sender, byte[] frame)
    {
        try
        {
            var message = _serializer.Deserialize(frame);
            if (message == null)
            {
                _logger?.LogWarning("Dropping malformed frame from remote server ({Length} bytes)", frame.Length);
                return;
            }

            var outbound = _client.GetClient();
            if (outbound != null)
                RaiseMessageReceived(outbound, message);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error processing data from remote server");
        }
    }

    private void RaiseMessageReceived(TcpClient client, ProtocolMessage message)
    {
        string? deviceId;
        Device? device;
        lock (_lock)
        {
            deviceId = _activeConnections.FirstOrDefault(kv => kv.Value == client).Key;
            device = deviceId != null ? _devices.GetValueOrDefault(deviceId) : null;
        }

        OnMessageReceived?.Invoke(this, new MessageReceivedEventArgs(deviceId ?? string.Empty, device, message));
    }

    public event EventHandler<DeviceConnectedEventArgs>? OnDeviceConnected;
    public event EventHandler<DeviceDisconnectedEventArgs>? OnDeviceDisconnected;
    public event EventHandler<MessageReceivedEventArgs>? OnMessageReceived;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _server.OnClientConnected -= OnServerClientConnected;
        _server.OnClientDisconnected -= OnServerClientDisconnected;
        if (ReferenceEquals(_server.FrameHandler, _frameHandler))
            _server.FrameHandler = null;
        _client.OnDataReceived -= OnClientDataReceived;

        _server.Dispose();
        _client.Dispose();
    }
}

public sealed class DeviceConnectedEventArgs : EventArgs
{
    public Device Device { get; }
    public DeviceConnectedEventArgs(Device device) => Device = device;
}

public sealed class DeviceDisconnectedEventArgs : EventArgs
{
    public Device Device { get; }
    public DeviceDisconnectedEventArgs(Device device) => Device = device;
}

public sealed class MessageReceivedEventArgs : EventArgs
{
    public string DeviceId { get; }
    public Device? Device { get; }
    public ProtocolMessage Message { get; }

    public MessageReceivedEventArgs(string deviceId, Device? device, ProtocolMessage message)
    {
        DeviceId = deviceId;
        Device = device;
        Message = message;
    }
}
