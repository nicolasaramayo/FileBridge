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

    /// <summary>After a decline (or an unanswered prompt), further offers from that IP are refused without asking for this long.</summary>
    public TimeSpan DeclineCooldown { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Only one offer is asked at a time. Another offer waits this long for the current question to be
    /// answered before it is refused as "busy" (so two quick, automatically accepted offers do not collide).
    /// </summary>
    public TimeSpan BusyWaitTimeout { get; set; } = TimeSpan.FromSeconds(2);
}
