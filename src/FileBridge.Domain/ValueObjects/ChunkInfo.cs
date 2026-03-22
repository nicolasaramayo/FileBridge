namespace FileBridge.Domain.ValueObjects;

public readonly record struct ChunkInfo
{
    public int Index { get; init; }
    public long Offset { get; init; }
    public int Size { get; init; }
    public byte[] Checksum { get; init; }

    public ChunkInfo(int index, long offset, int size, byte[] checksum)
    {
        if (checksum.Length != 4)
            throw new ArgumentException("CRC32 checksum must be 4 bytes.", nameof(checksum));

        Index = index;
        Offset = offset;
        Size = size;
        Checksum = checksum;
    }

    public uint ChecksumValue => BitConverter.ToUInt32(Checksum);
}
