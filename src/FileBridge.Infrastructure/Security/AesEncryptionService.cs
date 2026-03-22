namespace FileBridge.Infrastructure.Security;

using System.Security.Cryptography;

public interface IAesEncryptionService
{
    byte[] Encrypt(byte[] data, byte[] key);
    byte[] Decrypt(byte[] data, byte[] key);
    byte[] GenerateKey();
    byte[] GenerateIv();
}

public sealed class AesEncryptionService : IAesEncryptionService
{
    private const int KeySizeBytes = 32; // 256-bit
    private const int IvSizeBytes = 16;  // 128-bit

    public byte[] Encrypt(byte[] data, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key.Length == KeySizeBytes ? key : DeriveKey(key, KeySizeBytes);
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var ciphertext = encryptor.TransformFinalBlock(data, 0, data.Length);

        // Prepend IV: [IV(16)][Ciphertext(N)]
        var result = new byte[IvSizeBytes + ciphertext.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, IvSizeBytes);
        Buffer.BlockCopy(ciphertext, 0, result, IvSizeBytes, ciphertext.Length);
        return result;
    }

    public byte[] Decrypt(byte[] data, byte[] key)
    {
        if (data.Length < IvSizeBytes)
            throw new CryptographicException("Encrypted data too short.");

        var actualKey = key.Length == KeySizeBytes ? key : DeriveKey(key, KeySizeBytes);

        // Extract IV from beginning of data
        var iv = new byte[IvSizeBytes];
        Buffer.BlockCopy(data, 0, iv, 0, IvSizeBytes);

        var ciphertext = new byte[data.Length - IvSizeBytes];
        Buffer.BlockCopy(data, IvSizeBytes, ciphertext, 0, ciphertext.Length);

        using var aes = Aes.Create();
        aes.Key = actualKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
    }

    public byte[] GenerateKey()
    {
        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.GenerateKey();
        return aes.Key;
    }

    public byte[] GenerateIv()
    {
        using var aes = Aes.Create();
        aes.GenerateIV();
        return aes.IV;
    }

    private static byte[] DeriveKey(byte[] masterKey, int keySize)
    {
        using var deriveBytes = new Rfc2898DeriveBytes(
            masterKey,
            Array.Empty<byte>(),
            10000,
            HashAlgorithmName.SHA256);
        return deriveBytes.GetBytes(keySize);
    }
}
