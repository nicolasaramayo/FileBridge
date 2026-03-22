namespace FileBridge.Infrastructure.Security;

using System.Security.Cryptography;

public interface IKeyExchangeService
{
    (byte[] PublicKey, byte[] PrivateKey) GenerateKeyPair();
    byte[] ComputeSharedSecret(byte[] ourPrivateKey, byte[] peerPublicKey);
    byte[] DeriveMasterKey(byte[] sharedSecret, byte[] salt);
    byte[] ComputeKeyConfirmation(byte[] sharedSecret);
    bool VerifyKeyConfirmation(byte[] expected, byte[] actual);
}

public sealed class DiffieHellmanKeyExchange : IKeyExchangeService
{
    private const string HkdfInfo = "FileBridge-v1";
    private const string ConfirmInfo = "FileBridge-Confirm";
    private const int MasterKeyLength = 32; // 256-bit

    public (byte[] PublicKey, byte[] PrivateKey) GenerateKeyPair()
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        // Export as ASN.1 DER (standard format for key exchange)
        var privateKey = ecdh.ExportECPrivateKey();
        var publicKey = ecdh.ExportSubjectPublicKeyInfo();

        return (publicKey, privateKey);
    }

    public byte[] ComputeSharedSecret(byte[] ourPrivateKey, byte[] peerPublicKey)
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        ecdh.ImportECPrivateKey(ourPrivateKey, out _);

        using var peerKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        peerKey.ImportSubjectPublicKeyInfo(peerPublicKey, out _);

        return ecdh.DeriveKeyMaterial(peerKey.PublicKey);
    }

    public byte[] DeriveMasterKey(byte[] sharedSecret, byte[] salt)
    {
        // HKDF-SHA256: extract then expand
        // Extract: PRK = HMAC-SHA256(salt, IKM)
        using var hmac = new HMACSHA256(salt);
        var prk = hmac.ComputeHash(sharedSecret);

        // Expand: derive subkeys
        var infoBytes = System.Text.Encoding.UTF8.GetBytes(HkdfInfo);
        var okm = new byte[MasterKeyLength];

        // T(1) = HMAC-SHA256(PRK, T(0) || info || 0x01)
        using var hmac2 = new HMACSHA256(prk);
        var t0 = Array.Empty<byte>();
        var t1Input = new byte[t0.Length + infoBytes.Length + 1];
        Buffer.BlockCopy(t0, 0, t1Input, 0, t0.Length);
        Buffer.BlockCopy(infoBytes, 0, t1Input, t0.Length, infoBytes.Length);
        t1Input[t1Input.Length - 1] = 0x01;

        var t1 = hmac2.ComputeHash(t1Input);
        Buffer.BlockCopy(t1, 0, okm, 0, Math.Min(t1.Length, MasterKeyLength));
        return okm;
    }

    public byte[] ComputeKeyConfirmation(byte[] sharedSecret)
    {
        using var hmac = new HMACSHA256(sharedSecret);
        var infoBytes = System.Text.Encoding.UTF8.GetBytes(ConfirmInfo);
        var hash = hmac.ComputeHash(infoBytes);

        // Return first 8 bytes (truncated as per design)
        var result = new byte[8];
        Buffer.BlockCopy(hash, 0, result, 0, 8);
        return result;
    }

    public bool VerifyKeyConfirmation(byte[] expected, byte[] actual)
    {
        if (expected.Length != actual.Length)
            return false;

        var mismatch = 0;
        for (var i = 0; i < expected.Length; i++)
            mismatch |= expected[i] ^ actual[i];

        return mismatch == 0;
    }
}
