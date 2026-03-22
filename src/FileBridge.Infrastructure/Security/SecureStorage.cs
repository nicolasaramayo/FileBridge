namespace FileBridge.Infrastructure.Security;

using System.Security.Cryptography;
using FileBridge.Domain.Interfaces;

#if WINDOWS
public sealed class SecureStorage : ISecureStorage
{
    private readonly string _basePath;
    private readonly object _lock = new();

    public SecureStorage()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(appData, "FileBridge", "Secure");
        Directory.CreateDirectory(dir);
        _basePath = dir;
    }

    public Task<byte[]?> GetKeyAsync(Guid deviceId)
    {
        var path = GetPath(deviceId);
        if (!File.Exists(path))
            return Task.FromResult<byte[]?>(null);

        try
        {
            var encrypted = File.ReadAllBytes(path);
            var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Task.FromResult<byte[]?>(decrypted);
        }
        catch (CryptographicException)
        {
            return Task.FromResult<byte[]?>(null);
        }
    }

    public Task SetKeyAsync(Guid deviceId, byte[] key)
    {
        var path = GetPath(deviceId);
        var encrypted = ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser);

        lock (_lock)
        {
            File.WriteAllBytes(path, encrypted);
        }

        return Task.CompletedTask;
    }

    public Task DeleteKeyAsync(Guid deviceId)
    {
        var path = GetPath(deviceId);
        if (File.Exists(path))
        {
            lock (_lock)
            {
                File.Delete(path);
            }
        }
        return Task.CompletedTask;
    }

    public Task<bool> ContainsKeyAsync(Guid deviceId)
    {
        var path = GetPath(deviceId);
        return Task.FromResult(File.Exists(path));
    }

    private string GetPath(Guid deviceId) => Path.Combine(_basePath, $"fb_key_{deviceId}.key");
}
#endif
