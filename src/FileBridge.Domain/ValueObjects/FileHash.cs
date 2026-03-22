namespace FileBridge.Domain.ValueObjects;

public sealed class FileHash : ValueObject
{
    public string Value { get; }
    
    public FileHash(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Hash cannot be empty", nameof(value));
        Value = value;
    }
    
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }
}
