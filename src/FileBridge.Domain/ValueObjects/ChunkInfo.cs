namespace FileBridge.Domain.ValueObjects;

public sealed class ChunkInfo : ValueObject
{
    public int Index { get; }
    public int Size { get; }
    public byte[] Data { get; }
    
    public ChunkInfo(int index, int size, byte[] data)
    {
        if (index < 0)
            throw new ArgumentException("Index cannot be negative", nameof(index));
        if (size <= 0)
            throw new ArgumentException("Size must be positive", nameof(size));
        
        Index = index;
        Size = size;
        Data = data ?? throw new ArgumentNullException(nameof(data));
    }
    
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Index;
        yield return Size;
    }
}
