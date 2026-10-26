namespace FileBridge.Infrastructure.Network;

using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

public sealed class TcpServer : IDisposable
{
    private System.Net.Sockets.TcpListener? _listener;
    private readonly List<TcpClient> _clients = new();
    private readonly ConcurrentDictionary<TcpClient, SemaphoreSlim> _writeLocks = new();
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private readonly object _clientsLock = new();
    private readonly ILogger? _logger;

    public int Port { get; private set; }
    public bool IsRunning { get; private set; }

    public event EventHandler<ClientConnectedEventArgs>? OnClientConnected;
    public event EventHandler<ClientDisconnectedEventArgs>? OnClientDisconnected;
    public event EventHandler<DataReceivedEventArgs>? OnDataReceived;

    /// <summary>
    /// Processes each received frame. Unlike <see cref="OnDataReceived"/>, it is awaited before the next
    /// frame of the same connection is read, which keeps chunks in order and gives TCP back-pressure
    /// (a slow disk slows the sender down instead of piling chunks up in memory).
    /// </summary>
    public Func<TcpClient, byte[], CancellationToken, Task>? FrameHandler { get; set; }

    public TcpServer(ILogger<TcpServer>? logger = null)
    {
        _logger = logger;
    }

    public Task StartAsync(int port = 0, CancellationToken ct = default)
    {
        if (IsRunning)
            throw new InvalidOperationException("Server already running");

        var listener = new System.Net.Sockets.TcpListener(IPAddress.Any, port);
        listener.Start(); // throws SocketException (AddressAlreadyInUse) if the port is taken
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        IsRunning = true;

        _logger?.LogInformation("TCP server started on port {Port}", Port);
        _ = AcceptClientsAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (!IsRunning)
            return Task.CompletedTask;

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
        return Task.CompletedTask;
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

    /// <summary>Sends one frame (a serialized <see cref="ProtocolMessage"/>) to an accepted client.</summary>
    public async Task SendToClientAsync(TcpClient client, byte[] data, CancellationToken ct = default)
    {
        await SendToClientInternalAsync(client, data, ct);
    }

    private async Task SendToClientInternalAsync(TcpClient client, byte[] data, CancellationToken ct)
    {
        if (!client.Connected)
            throw new IOException("Client is not connected");

        // Same framing as TcpFileClient (length prefix). Writes on one connection are serialized
        // so two replies can never interleave their bytes.
        var writeLock = _writeLocks.GetOrAdd(client, _ => new SemaphoreSlim(1, 1));
        await writeLock.WaitAsync(ct);
        try
        {
            await FrameIO.WriteFrameAsync(client.GetStream(), data, ct);
        }
        finally
        {
            writeLock.Release();
        }
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

                _logger?.LogInformation("Client connected from {Endpoint}", client.Client.RemoteEndPoint);
                OnClientConnected?.Invoke(this, new ClientConnectedEventArgs(client));

                _ = HandleClientAsync(client, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested)
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

            while (!ct.IsCancellationRequested && client.Connected)
            {
                var frame = await FrameIO.ReadFrameAsync(stream, ct);
                if (frame == null)
                    break; // peer closed the connection

                OnDataReceived?.Invoke(this, new DataReceivedEventArgs(client, frame));

                var handler = FrameHandler;
                if (handler != null)
                    await handler(client, frame, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (InvalidDataException ex)
        {
            _logger?.LogWarning("Closing {Endpoint}: {Reason}", remoteEndpoint, ex.Message);
        }
        catch (IOException ex)
        {
            _logger?.LogInformation("Connection {Endpoint} closed: {Reason}", remoteEndpoint, ex.Message);
        }
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
            if (_writeLocks.TryRemove(client, out var writeLock))
                writeLock.Dispose();

            OnClientDisconnected?.Invoke(this, new ClientDisconnectedEventArgs(client));
            _logger?.LogInformation("Client disconnected: {Endpoint}", remoteEndpoint);
        }
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
