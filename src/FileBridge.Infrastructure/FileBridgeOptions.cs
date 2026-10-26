namespace FileBridge.Infrastructure;

/// <summary>
/// Runtime settings for networking and file reception. Registered as a singleton;
/// the app (MauiProgram) fills in the platform-specific values.
/// </summary>
public sealed class FileBridgeOptions
{
    /// <summary>
    /// TCP port of the transfer server. Fixed by default so firewall rules and manual
    /// "connect by IP" are predictable; if it is taken, a random free port is used instead.
    /// </summary>
    public int Port { get; set; } = 45678;

    /// <summary>Name shown to other devices (mDNS + handshake). On Android, Environment.MachineName is "localhost".</summary>
    public string DeviceName { get; set; } = Environment.MachineName;

    /// <summary>Identity advertised over mDNS for this process; also used to hide ourselves from discovery results.</summary>
    public Guid DeviceId { get; set; } = Guid.NewGuid();

    /// <summary>Folder where incoming files are written.</summary>
    public string ReceivedFilesDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "FileBridge", "Received");

    /// <summary>Largest incoming file accepted.</summary>
    public long MaxIncomingFileSize { get; set; } = 20L * 1024 * 1024 * 1024;

    /// <summary>How long an incoming file waits for the user to accept it before it is declined.</summary>
    public TimeSpan IncomingApprovalTimeout { get; set; } = TimeSpan.FromSeconds(60);
}
