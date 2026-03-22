namespace FileBridge.Infrastructure.Network;

using System.IO;
using FileBridge.Domain.ValueObjects;

public sealed class ChunkedTransferStream : Stream
{
    private readonly Stream _innerStream;
    private readonly int _chunkSize;
    private readonly bool _isReading;
    private long _position;
    private long _streamLength;
    private int _currentChunkIndex = -1;
    private byte[]? _currentChunkData;
    private int _currentChunkOffset;
    private readonly bool _canSeek;

    public int CurrentChunkIndex => _currentChunkIndex;

    public ChunkedTransferStream(Stream innerStream, int chunkSize = 65536, bool isReading = true, long length = 0)
    {
        _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
        _chunkSize = chunkSize;
        _isReading = isReading;
        _streamLength = length;
        _canSeek = innerStream.CanSeek;

        if (length == 0 && isReading && innerStream.CanSeek)
            _streamLength = innerStream.Length;
    }

    public override bool CanRead => _isReading;
    public override bool CanWrite => !_isReading;
    public override bool CanSeek => _canSeek && _isReading;

    public override long Length => _streamLength;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (!_isReading)
            throw new NotSupportedException("Reading is not supported on a write stream.");

        var totalRead = 0;

        while (totalRead < count)
        {
            // Refill current chunk if needed
            if (_currentChunkData == null || _currentChunkOffset >= _currentChunkData.Length)
            {
                _currentChunkIndex++;
                _currentChunkOffset = 0;

                var (chunkData, isLast) = await ReadNextChunkAsync(cancellationToken);
                if (chunkData == null || chunkData.Length == 0)
                    break; // End of stream

                _currentChunkData = chunkData;

                if (isLast)
                    _streamLength = _position + chunkData.Length;
            }

            var available = _currentChunkData!.Length - _currentChunkOffset;
            var toCopy = Math.Min(count - totalRead, available);

            Array.Copy(_currentChunkData, _currentChunkOffset, buffer, offset + totalRead, toCopy);
            _currentChunkOffset += toCopy;
            _position += toCopy;
            totalRead += toCopy;
        }

        return totalRead;
    }

    private async Task<(byte[]? Data, bool IsLast)> ReadNextChunkAsync(CancellationToken ct)
    {
        // Read chunk header: [ChunkIndex:4][ChunkSize:4][IsLast:1]
        var header = new byte[9];
        var read = await _innerStream.ReadAsync(header.AsMemory(), ct);
        if (read == 0)
            return (null, false);

        if (read < 9)
            return (null, false);

        var chunkIndex = BitConverter.ToInt32(header, 0);
        var chunkSize = BitConverter.ToInt32(header, 4);
        var isLast = header[8] != 0;

        if (chunkSize <= 0 || chunkSize > ProtocolSerializer.MaxChunkSize)
            return (null, false);

        var data = new byte[chunkSize];
        var totalRead = 0;
        while (totalRead < chunkSize)
        {
            var r = await _innerStream.ReadAsync(data.AsMemory(totalRead, chunkSize - totalRead), ct);
            if (r == 0)
                break;
            totalRead += r;
        }

        // Read and verify CRC32
        var checksumBuffer = new byte[4];
        await _innerStream.ReadAsync(checksumBuffer.AsMemory(), ct);
        var storedChecksum = BitConverter.ToUInt32(checksumBuffer, 0);
        var computedChecksum = Crc32.Compute(data);
        if (storedChecksum != computedChecksum)
            throw new InvalidDataException($"Chunk {chunkIndex} checksum mismatch.");

        return (data, isLast);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_isReading)
            throw new NotSupportedException("Writing is not supported on a read stream.");

        var written = 0;
        while (written < count)
        {
            // We batch writes into chunks
            var toWrite = Math.Min(count - written, _chunkSize);
            await _innerStream.WriteAsync(buffer, offset + written, toWrite, cancellationToken);
            written += toWrite;
            _position += toWrite;
        }

        _streamLength = Math.Max(_streamLength, _position);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        if (!_canSeek)
            throw new NotSupportedException("Underlying stream does not support seeking.");

        var newPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _streamLength + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        if (newPosition < 0)
            throw new IOException("Seek before beginning of stream.");

        _position = newPosition;
        _currentChunkData = null;
        _currentChunkOffset = 0;
        _currentChunkIndex = (int)(_position / _chunkSize);

        if (_innerStream.CanSeek)
            _innerStream.Seek(_position, SeekOrigin.Begin);

        return _position;
    }

    public override void Flush() => _innerStream.Flush();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _innerStream.Dispose();
        }
        base.Dispose(disposing);
    }
}
