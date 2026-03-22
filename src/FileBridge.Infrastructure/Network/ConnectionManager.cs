namespace FileBridge.Infrastructure.Network;

using System.IO;
using System.Net.Sockets;
using FileBridge.Domain.Entities;
using FileBridge.Domain.Enums;
using FileBridge.Domain.Interfaces;

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

    public ConnectionManager(TcpServer server, TcpFileClient client, ITransferService? transferService = null, ILogger? logger = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _transferService = transferService;
        _logger = logger;
        _serializer = new ProtocolSerializer();
        _handler = new TransferProtocolHandler(_serializer, _transferService, _logger);

        _server.OnClientConnected += OnServerClientConnected;
        _server.OnClientDisconnected += OnServerClientDisconnected;
        _server.OnDataReceived += OnServerDataReceived;
    }

    public async Task<bool> ConnectToDeviceAsync(Device device, CancellationToken ct = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ConnectionManager));

        try
        {
            await _client.ConnectAsync(device.IpAddress, device.Port, ct);

            // Send handshake
            var handshakePayload = _serializer.BuildHandshakePayload(
                Environment.MachineName,
                Array.Empty<byte>());
            var handshakeMsg = new ProtocolMessage
            {
                Type = MessageType.Handshake,
                Payload = handshakePayload,
                Length = handshakePayload.Length
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
            _logger?.LogWarning(ex, "Failed to connect to device {Name}", device.Name);
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
                var msg = new ProtocolMessage { Type = MessageType.Disconnect, Length = 0 };
                var data = _serializer.Serialize(msg);
                await _client.SendAsync(data);
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

        message.Length = message.Payload.Length;
        var data = _serializer.Serialize(message);
        await _server.SendToClientAsync(client, data, ct);
    }

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

    private async void OnServerDataReceived(object? sender, DataReceivedEventArgs e)
    {
        try
        {
            var message = _serializer.Deserialize(e.Data);
            if (message == null)
                return;

            // Find device for this client
            string? deviceId;
            Device? device;
            lock (_lock)
            {
                deviceId = _activeConnections.FirstOrDefault(kv => kv.Value == e.Client).Key;
                device = deviceId != null ? _devices.GetValueOrDefault(deviceId) : null;
            }

            // Emit the message
            OnMessageReceived?.Invoke(this, new MessageReceivedEventArgs(deviceId ?? string.Empty, device, message));

            // Handle protocol message
            if (_transferService != null)
            {
                await _handler.HandleMessageAsync(message, e.Client, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error processing received data");
        }
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
        _server.OnDataReceived -= OnServerDataReceived;

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
