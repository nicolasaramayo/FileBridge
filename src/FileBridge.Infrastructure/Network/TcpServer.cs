namespace FileBridge.Infrastructure.Network;

using System.IO;
using System.Net;
using System.Net.Sockets;
using FileBridge.Domain.Entities;

public sealed class TcpServer : IDisposable
{
    private System.Net.Sockets.TcpListener? _listener;
    private readonly List<TcpClient> _clients = new();
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private readonly object _clientsLock = new();
    private readonly ILogger? _logger;

    public int Port { get; private set; }
    public bool IsRunning { get; private set; }

    public event EventHandler<ClientConnectedEventArgs>? OnClientConnected;
    public event EventHandler<ClientDisconnectedEventArgs>? OnClientDisconnected;
    public event EventHandler<DataReceivedEventArgs>? OnDataReceived;

    public TcpServer(ILogger? logger = null)
    {
        _logger = logger;
    }

    public async Task StartAsync(int port = 0, CancellationToken ct = default)
    {
        if (IsRunning)
            throw new InvalidOperationException("Server already running");

        _listener = new System.Net.Sockets.TcpListener(IPAddress.Any, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        IsRunning = true;

        _logger?.LogInformation("TCP server started on port {Port}", Port);
        _ = AcceptClientsAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        if (!IsRunning)
            return;

        IsRunning = false;
        _cts?.Cancel();

        List<TcpClient> clientsToClose;
        lock (_clientsLock)
        {
            clientsToClose = new List<TcpClient>(_clients);
            _clients.Clear();
        }

        foreach (var client in clientsToClose)
        {
            try { client.Close(); } catch { /* ignore */ }
        }

        _listener?.Stop();
        _cts?.Dispose();
        _cts = null;
        _listener = null;

        _logger?.LogInformation("TCP server stopped");
    }

    public async Task BroadcastAsync(byte[] data, CancellationToken ct = default)
    {
        List<TcpClient> clients;
        lock (_clientsLock)
        {
            clients = new List<TcpClient>(_clients);
        }

        var deadClients = new List<TcpClient>();
        foreach (var client in clients)
        {
            try
            {
                await SendToClientInternalAsync(client, data, ct);
            }
            catch
            {
                deadClients.Add(client);
            }
        }

        RemoveDeadClients(deadClients);
    }

    public async Task SendToClientAsync(TcpClient client, byte[] data, CancellationToken ct = default)
    {
        await SendToClientInternalAsync(client, data, ct);
    }

    private async Task SendToClientInternalAsync(TcpClient client, byte[] data, CancellationToken ct)
    {
        if (!client.Connected)
            throw new IOException("Client is not connected");

        var stream = client.GetStream();
        await stream.WriteAsync(data.AsMemory(), ct);
        await stream.FlushAsync(ct);
    }

    private async Task AcceptClientsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                client.NoDelay = true; // Disable Nagle for lower latency

                lock (_clientsLock)
                {
                    _clients.Add(client);
                }

                OnClientConnected?.Invoke(this, new ClientConnectedEventArgs(client));
                _logger?.LogInformation("Client connected from {Endpoint}", client.Client.RemoteEndPoint);

                _ = HandleClientAsync(client, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error accepting client");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var remoteEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        try
        {
            var stream = client.GetStream();
            var buffer = new byte[8192];

            while (!ct.IsCancellationRequested && client.Connected)
            {
                // Read length prefix (4 bytes)
                var lengthBuffer = await ReadExactAsync(stream, 4, ct);
                if (lengthBuffer == null)
                    break;

                var length = BitConverter.ToInt32(lengthBuffer, 0);
                if (length <= 0 || length > 10 * 1024 * 1024) // Max 10MB
                    break;

                // Read payload
                var payload = await ReadExactAsync(stream, length, ct);
                if (payload == null)
                    break;

                OnDataReceived?.Invoke(this, new DataReceivedEventArgs(client, payload));
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error handling client {Endpoint}", remoteEndpoint);
        }
        finally
        {
            lock (_clientsLock)
            {
                _clients.Remove(client);
            }

            try { client.Close(); } catch { /* ignore */ }
            OnClientDisconnected?.Invoke(this, new ClientDisconnectedEventArgs(client));
            _logger?.LogInformation("Client disconnected: {Endpoint}", remoteEndpoint);
        }
    }

    private static async Task<byte[]?> ReadExactAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var totalRead = 0;

        while (totalRead < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, count - totalRead), ct);
            if (read == 0)
                return null; // Connection closed

            totalRead += read;
        }

        return buffer;
    }

    private void RemoveDeadClients(List<TcpClient> deadClients)
    {
        if (deadClients.Count == 0)
            return;

        lock (_clientsLock)
        {
            foreach (var client in deadClients)
            {
                _clients.Remove(client);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopAsync().GetAwaiter().GetResult();
    }
}

public sealed class ClientConnectedEventArgs : EventArgs
{
    public TcpClient Client { get; }

    public ClientConnectedEventArgs(TcpClient client)
    {
        Client = client;
    }
}

public sealed class ClientDisconnectedEventArgs : EventArgs
{
    public TcpClient Client { get; }

    public ClientDisconnectedEventArgs(TcpClient client)
    {
        Client = client;
    }
}

public sealed class DataReceivedEventArgs : EventArgs
{
    public TcpClient Client { get; }
    public byte[] Data { get; }

    public DataReceivedEventArgs(TcpClient client, byte[] data)
    {
        Client = client;
        Data = data;
    }
}

public interface ILogger
{
    void LogInformation(string message, params object[] args);
    void LogWarning(Exception? ex, string message, params object[] args);
    void LogWarning(string message, params object[] args);
}
