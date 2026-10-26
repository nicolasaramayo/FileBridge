namespace FileBridge.Infrastructure.Network;

using System.IO;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

public sealed class TcpFileClient : IDisposable
{
    private System.Net.Sockets.TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ILogger? _logger;
    private bool _disposed;
    private int _reconnectDelayMs = 1000;
    private const int MaxReconnectDelayMs = 30000;

    public string Host { get; private set; } = string.Empty;
    public int Port { get; private set; }
    public bool IsConnected => _client?.Connected ?? false;

    /// <summary>
    /// How long to wait for the TCP connection. Without it, an unreachable LAN address (wrong adapter
    /// IP, device asleep, firewall dropping packets) blocks for the OS default (~20 s or more).
    /// </summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets the underlying TcpClient for connection tracking.
    /// </summary>
    public System.Net.Sockets.TcpClient? GetClient() => _client;

    public event EventHandler? OnConnected;
    public event EventHandler? OnDisconnected;
    public event EventHandler<byte[]>? OnDataReceived;

    public TcpFileClient(ILogger<TcpFileClient>? logger = null)
    {
        _logger = logger;
    }

    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        Host = host;
        Port = port;

        await ConnectInternalAsync(ct);
    }

    private async Task ConnectInternalAsync(CancellationToken ct)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TcpFileClient));

        CleanupConnection();

        _client = new System.Net.Sockets.TcpClient();
        _client.NoDelay = true;

        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(ConnectTimeout);
            try
            {
                await _client.ConnectAsync(Host, Port, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                CleanupConnection();
                throw new TimeoutException($"Could not connect to {Host}:{Port} within {ConnectTimeout.TotalSeconds:0} s.");
            }
        }

        _stream = _client.GetStream();
        _cts = new CancellationTokenSource();

        _logger?.LogInformation("Connected to {Host}:{Port}", Host, Port);
        OnConnected?.Invoke(this, EventArgs.Empty);

        _ = ReceiveLoopAsync(_stream, _cts.Token);
    }

    public async Task ReconnectAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(Host))
            throw new InvalidOperationException("No host configured");

        _reconnectDelayMs = Math.Min(_reconnectDelayMs * 2, MaxReconnectDelayMs);
        _logger?.LogWarning("Reconnecting in {Delay}ms...", _reconnectDelayMs);
        await Task.Delay(_reconnectDelayMs, ct);

        try
        {
            await ConnectInternalAsync(ct);
            _reconnectDelayMs = 1000; // Reset on success
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Reconnection attempt failed");
            throw;
        }
    }

    public Task DisconnectAsync()
    {
        _reconnectDelayMs = 1000;
        CleanupConnection();
        _logger?.LogInformation("Disconnected from {Host}:{Port}", Host, Port);
        OnDisconnected?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    /// <summary>Sends one frame (a serialized <see cref="ProtocolMessage"/>).</summary>
    public async Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            if (_stream == null || !IsConnected)
                throw new IOException("Not connected");

            await FrameIO.WriteFrameAsync(_stream, data, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Reads the next frame, or null if the connection was closed.</summary>
    public async Task<byte[]?> ReceiveAsync(CancellationToken ct = default)
    {
        if (_stream == null || !IsConnected)
            return null;

        return await FrameIO.ReadFrameAsync(_stream, ct);
    }

    private async Task ReceiveLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var data = await FrameIO.ReadFrameAsync(stream, ct);
                if (data == null)
                    break;

                OnDataReceived?.Invoke(this, data);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Receive loop error");
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                // Unexpected disconnect — notify
                _logger?.LogWarning("Connection to {Host}:{Port} lost", Host, Port);
                OnDisconnected?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void CleanupConnection()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        _cts?.Dispose();
        _cts = null;

        try { _stream?.Close(); } catch { /* ignore */ }
        _stream = null;

        try { _client?.Close(); } catch { /* ignore */ }
        _client = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CleanupConnection();
        _sendLock.Dispose();
    }
}
