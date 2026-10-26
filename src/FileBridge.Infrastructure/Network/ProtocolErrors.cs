namespace FileBridge.Infrastructure.Network;

/// <summary>Error codes carried in <c>MessageType.Error</c> payloads (HTTP-like, for readability).</summary>
public static class ProtocolErrorCodes
{
    public const ushort BadRequest = 400;
    public const ushort Conflict = 409;
    public const ushort IntegrityFailed = 422;
    public const ushort Internal = 500;
    public const ushort NotImplemented = 501;
    public const ushort InsufficientStorage = 507;
}

/// <summary>A local protocol violation; the handler turns it into an Error message for the peer.</summary>
public sealed class ProtocolException : Exception
{
    public ushort Code { get; }

    public ProtocolException(ushort code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }
}

/// <summary>The remote device answered with an Error message.</summary>
public sealed class RemoteProtocolException : Exception
{
    public ushort Code { get; }
    public string RemoteMessage { get; }

    public RemoteProtocolException(ushort code, string remoteMessage)
        : base($"The receiving device rejected the transfer [{code}]: {remoteMessage}")
    {
        Code = code;
        RemoteMessage = remoteMessage;
    }
}
