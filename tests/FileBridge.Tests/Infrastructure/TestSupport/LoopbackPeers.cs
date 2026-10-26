using System.Threading.Channels;
using FileBridge.Domain.Entities;
using FileBridge.Domain.Enums;
using FileBridge.Infrastructure;
using FileBridge.Infrastructure.Network;

namespace FileBridge.Tests.Infrastructure.TestSupport;

/// <summary>
/// A real receiving device on 127.0.0.1: TcpServer + ConnectionManager + FileReceiver,
/// wired exactly like the app does, writing into a temporary folder.
/// </summary>
internal sealed class LoopbackReceiver : IAsyncDisposable
{
    private LoopbackReceiver(string directory, TcpServer server, FileReceiver receiver, ConnectionManager connections)
    {
        Directory = directory;
        Server = server;
        Receiver = receiver;
        Connections = connections;
    }

    public string Directory { get; }
    public TcpServer Server { get; }
    public FileReceiver Receiver { get; }
    public ConnectionManager Connections { get; }
    public int Port => Server.Port;

    public static async Task<LoopbackReceiver> StartAsync(string deviceName = "receiver")
    {
        var directory = Path.Combine(Path.GetTempPath(), "filebridge-tests", Guid.NewGuid().ToString("N"));
        var options = new FileBridgeOptions { DeviceName = deviceName, ReceivedFilesDirectory = directory, Port = 0 };
        var server = new TcpServer();
        var receiver = new FileReceiver(options);
        var connections = new ConnectionManager(server, new TcpFileClient(), receiver, options: options);
        await server.StartAsync(0);
        return new LoopbackReceiver(directory, server, receiver, connections);
    }

    public Device AsDevice(string name = "receiver") =>
        new() { Id = Guid.NewGuid(), Name = name, IpAddress = "127.0.0.1", Port = Port };

    public string[] Files() =>
        System.IO.Directory.Exists(Directory) ? System.IO.Directory.GetFiles(Directory) : Array.Empty<string>();

    public ValueTask DisposeAsync()
    {
        Connections.Dispose(); // also disposes the server and its client
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch { /* best effort */ }
        return ValueTask.CompletedTask;
    }
}

/// <summary>Speaks the protocol by hand, to send things a well-behaved sender never would.</summary>
internal sealed class RawProtocolClient : IDisposable
{
    private readonly TcpFileClient _client = new();
    private readonly Channel<ProtocolMessage> _received = Channel.CreateUnbounded<ProtocolMessage>();

    public ProtocolSerializer Serializer { get; } = new();

    public RawProtocolClient()
    {
        _client.OnDataReceived += (_, frame) =>
        {
            var message = Serializer.Deserialize(frame);
            if (message != null)
                _received.Writer.TryWrite(message);
        };
    }

    public Task ConnectAsync(int port) => _client.ConnectAsync("127.0.0.1", port);

    public Task SendAsync(MessageType type, byte[] payload) =>
        _client.SendAsync(Serializer.Serialize(new ProtocolMessage { Type = type, Payload = payload }));

    public async Task<ProtocolMessage> ReceiveAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        return await _received.Reader.ReadAsync(cts.Token);
    }

    public Task CloseAsync() => _client.DisconnectAsync();

    public void Dispose() => _client.Dispose();
}

internal static class TestFiles
{
    public static string CreateRandomFile(long size, string? name = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "filebridge-tests", "src-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name ?? "payload.bin");
        var buffer = new byte[64 * 1024];
        using var stream = File.Create(path);
        for (long written = 0; written < size;)
        {
            Random.Shared.NextBytes(buffer);
            var count = (int)Math.Min(buffer.Length, size - written);
            stream.Write(buffer, 0, count);
            written += count;
        }
        return path;
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(25);
        }
        return condition();
    }
}
