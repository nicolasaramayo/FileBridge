namespace FileBridge.Infrastructure.Network;

using System.Buffers.Binary;
using System.IO;

/// <summary>
/// The only framing used on the wire: [length: int32 little-endian][frame], where a frame is one
/// serialized <see cref="ProtocolMessage"/>. Every TCP read and write goes through here, so both
/// directions can no longer disagree (before, some paths wrote the length prefix and others did not).
/// </summary>
/// <remarks>Not thread-safe per stream: callers must serialize writes on the same connection.</remarks>
public static class FrameIO
{
    public const int MaxFrameSize = 10 * 1024 * 1024;

    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> frame, CancellationToken ct = default)
    {
        if (frame.Length <= 0 || frame.Length > MaxFrameSize)
            throw new ArgumentOutOfRangeException(nameof(frame), $"Frame size {frame.Length} is out of range.");

        // Prefix and payload in a single buffer/write.
        var buffer = new byte[4 + frame.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, frame.Length);
        frame.CopyTo(buffer.AsMemory(4));
        await stream.WriteAsync(buffer, ct);
        await stream.FlushAsync(ct);
    }

    /// <returns>The next frame, or <c>null</c> if the peer closed the connection between frames.</returns>
    /// <exception cref="InvalidDataException">The length prefix is invalid (peer is not speaking this protocol).</exception>
    /// <exception cref="EndOfStreamException">The connection closed in the middle of a frame.</exception>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, allowCleanEof: true, ct))
            return null;

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxFrameSize)
            throw new InvalidDataException($"Invalid frame length {length}.");

        var frame = new byte[length];
        await ReadExactAsync(stream, frame, allowCleanEof: false, ct);
        return frame;
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, bool allowCleanEof, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0)
            {
                if (read == 0 && allowCleanEof)
                    return false;
                throw new EndOfStreamException($"Connection closed after {read} of {buffer.Length} bytes.");
            }
            read += n;
        }
        return true;
    }
}
