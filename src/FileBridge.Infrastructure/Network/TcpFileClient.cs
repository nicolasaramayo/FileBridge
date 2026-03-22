namespace FileBridge.Infrastructure.Network;

using System.Net.Sockets;

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

    public string? Host { get; private set; }
    public int Port { get; private set; }
    public bool IsConnected => _client?.Connected ?? false;

    public event EventHandler? OnConnected;
    public event EventHandler? OnDisconnected;
    public event EventHandler<byte[]>? OnDataReceived;

    public TcpFileClient(ILogger? logger = null)
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
        _client.SendTimeout = 30000;
        _client.ReceiveTimeout = 30000;

        await _client.ConnectAsync(Host!, Port, ct);
        _stream = _client.GetStream();

        _cts = new CancellationTokenSource();

        _logger?.LogInformation("Connected to {Host}:{Port}", Host, Port);
        OnConnected?.Invoke(this, EventArgs.Empty);

        _ = ReceiveLoopAsync(_cts.Token);
    }

    public async Task ReconnectAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(Host))
            throw new InvalidOperationException("No host configured");

        _reconnectDelayMs = Math.Min(_reconnectDelayMs * 2, MaxReconnectDelayMs);
        _logger?.LogWarning("Reconnecting in {_reconnectDelayMs}ms...");
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

    public async Task DisconnectAsync()
    {
        _reconnectDelayMs = 1000;
        CleanupConnection();
        _logger?.LogInformation("Disconnected from {Host}:{Port}", Host, Port);
        OnDisconnected?.Invoke(this, EventArgs.Empty);
    }

    public async Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            if (_stream == null || !IsConnected)
                throw new IOException("Not connected");

            // Write length prefix (4 bytes, little-endian)
            var lengthPrefix = BitConverter.GetBytes(data.Length);
            await _stream.WriteAsync(lengthPrefix.AsMemory(), ct);
            await _stream.WriteAsync(data.AsMemory(), ct);
            await _stream.FlushAsync(ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<byte[]?> ReceiveAsync(CancellationToken ct = default)
    {
        if (_stream == null || !IsConnected)
            return null;

        // Read 4-byte length prefix
        var lengthBuffer = new byte[4];
        var read = await _stream.ReadAsync(lengthBuffer.AsMemory(), ct);
        if (read == 0)
            return null;

        if (read < 4)
        {
            var remaining = new byte[4 - read];
            var r = await _stream.ReadAsync(remaining.AsMemory(), ct);
            if (r == 0) return null;
            Array.Copy(remaining, 0, lengthBuffer, read, r);
        }

        var length = BitConverter.ToInt32(lengthBuffer, 0);
        if (length <= 0 || length > 10 * 1024 * 1024)
            return null;

        var payload = new byte[length];
        var totalRead = 0;
        while (totalRead < length)
        {
            var chunk = await _stream.ReadAsync(payload.AsMemory(totalRead, length - totalRead), ct);
            if (chunk == 0)
                return null;
            totalRead += chunk;
        }

        return payload;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && IsConnected)
            {
                var data = await ReceiveAsync(ct);
                if (data == null)
                    break;

                OnDataReceived?.Invoke(this, data);
            }
        }
        catch (OperationCanceledException) { }
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
                _logger?.LogWarning("Connection lost unexpectedly");
                OnDisconnected?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void CleanupConnection()
    {
        _cts?.Cancel();
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
