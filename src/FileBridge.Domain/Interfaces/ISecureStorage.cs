namespace FileBridge.Domain.Interfaces;

public interface ISecureStorage
{
    Task<byte[]?> GetKeyAsync(Guid deviceId);
    Task SetKeyAsync(Guid deviceId, byte[] key);
    Task DeleteKeyAsync(Guid deviceId);
    Task<bool> ContainsKeyAsync(Guid deviceId);
}
