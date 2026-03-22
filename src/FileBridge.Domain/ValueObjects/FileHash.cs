namespace FileBridge.Domain.ValueObjects;

public readonly record struct FileHash
{
    public byte[] Value { get; }

    public FileHash(byte[] value)
    {
        if (value.Length != 32)
            throw new ArgumentException("SHA-256 hash must be 32 bytes.", nameof(value));
        Value = value;
    }

    public static FileHash Empty => new(new byte[32]);

    public string ToHexString()
    {
        return Convert.ToHexString(Value).ToLowerInvariant();
    }

    public override string ToString() => ToHexString();

    public static FileHash FromHexString(string hex)
    {
        var bytes = Convert.FromHexString(hex);
        return new FileHash(bytes);
    }

    public string ToBase64()
    {
        return Convert.ToBase64String(Value);
    }

    public static FileHash FromBase64(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        return new FileHash(bytes);
    }
}
