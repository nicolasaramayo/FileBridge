namespace FileBridge.Infrastructure.Security;

using FileBridge.Domain.Interfaces;

#if ANDROID
using Android.Content;
using Java.Security;
using Javax.Crypto;
using Org.Json;

public sealed class AndroidSecureStorage : ISecureStorage
{
    private const string PrefFileName = "FileBridge.SecureStorage";

    private readonly ISharedPreferences _prefs;

    public AndroidSecureStorage()
    {
        _prefs = Android.App.Application.Context.GetSharedPreferences(PrefFileName, FileCreationMode.Private);
    }

    public Task<byte[]?> GetKeyAsync(Guid deviceId)
    {
        var keyName = GetKeyName(deviceId);
        var encoded = _prefs.GetString(keyName, null);
        if (string.IsNullOrEmpty(encoded))
            return Task.FromResult<byte[]?>(null);

        try
        {
            var encrypted = Convert.FromBase64String(encoded);
            // Decode with XOR using machine-derived key
            var key = MachineXorDecode(encrypted);
            return Task.FromResult<byte[]?>(key);
        }
        catch
        {
            return Task.FromResult<byte[]?>(null);
        }
    }

    public Task SetKeyAsync(Guid deviceId, byte[] key)
    {
        var keyName = GetKeyName(deviceId);
        // Encode with XOR using machine-derived key
        var encoded = Convert.ToBase64String(MachineXorEncode(key));
        _prefs.Edit().PutString(keyName, encoded).Apply();
        return Task.CompletedTask;
    }

    public Task DeleteKeyAsync(Guid deviceId)
    {
        var keyName = GetKeyName(deviceId);
        _prefs.Edit().Remove(keyName).Apply();
        return Task.CompletedTask;
    }

    public Task<bool> ContainsKeyAsync(Guid deviceId)
    {
        var keyName = GetKeyName(deviceId);
        return Task.FromResult(_prefs.Contains(keyName));
    }

    private static string GetKeyName(Guid deviceId) => $"fb_key_{deviceId}";

    private static byte[] MachineXorEncode(byte[] data)
    {
        var machineKey = GetMachineKey();
        var result = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
            result[i] = (byte)(data[i] ^ machineKey[i % machineKey.Length]);
        return result;
    }

    private static byte[] MachineXorDecode(byte[] data)
    {
        // XOR is symmetric - same operation decodes
        return MachineXorEncode(data);
    }

    private static byte[] GetMachineKey()
    {
        // Derive a consistent machine-specific key
        var context = Android.App.Application.Context;
        var packageName = context.PackageName ?? "FileBridge";
        var filesDir = context.FilesDir?.AbsolutePath ?? "";

        using var md5 = System.Security.Cryptography.MD5.Create();
        var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes($"{packageName}:{filesDir}"));
        return hash;
    }
}
#endif
