namespace FileBridge.Domain.ValueObjects;

public abstract class ValueObject
{
    protected abstract IEnumerable<object?> GetEqualityComponents();
    
    public override bool Equals(object? obj)
    {
        if (obj is null || GetType() != obj.GetType())
            return false;
        
        return GetEqualityComponents()
            .SequenceEqual(((ValueObject)obj).GetEqualityComponents());
    }
    
    public override int GetHashCode()
    {
        return GetEqualityComponents()
            .Select(x => x?.GetHashCode() ?? 0)
            .Aggregate((a, b) => a ^ b);
    }
    
    public static bool operator ==(ValueObject? a, ValueObject? b)
    {
        if (a is null && b is null)
            return true;
        
        return a?.Equals(b) ?? false;
    }
    
    public static bool operator !=(ValueObject? a, ValueObject? b) => !(a == b);
}
