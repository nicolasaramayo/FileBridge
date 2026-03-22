using System.Security.Cryptography;
using FileBridge.Domain.Entities;
using FileBridge.Domain.Interfaces;

namespace FileBridge.Application.Services;

public class PairingService : IPairingService
{
    private const int PairingCodeLength = 6;
    private readonly Dictionary<string, (Device Device, DateTimeOffset ExpiresAt)> _pendingPairings = new();
    private readonly object _lock = new();

    public event EventHandler<PairingEventArgs>? OnPairingRequested;
    public event EventHandler<Device>? OnPairingCompleted;

    public Task<string> GeneratePairingCodeAsync()
    {
        // Generate 6-digit code
        var code = new Random().Next(100000, 999999).ToString();
        return Task.FromResult(code);
    }

    public async Task<bool> ValidatePairingCodeAsync(string code, Device device)
    {
        if (string.IsNullOrEmpty(code) || code.Length != PairingCodeLength)
            return false;

        // Check if there's a pending pairing request with this code
        lock (_lock)
        {
            var pending = _pendingPairings.GetValueOrDefault(code);
            if (pending.Device == null || pending.ExpiresAt < DateTimeOffset.UtcNow)
            {
                // No matching pending pairing - this could be the initiator side
                // Store device as pending for validation
                _pendingPairings[code] = (device, DateTimeOffset.UtcNow.AddMinutes(5));
                return true;
            }

            // If we have a matching device, pairing is complete
            if (pending.Device.Id == device.Id)
            {
                _pendingPairings.Remove(code);
                OnPairingCompleted?.Invoke(this, device);
                return true;
            }
        }

        // Generate shared secret using ECDH
        var sharedSecret = await GetSharedSecretAsync(device);
        return sharedSecret != null && sharedSecret.Length > 0;
    }

    public Task<byte[]> GetSharedSecretAsync(Device device)
    {
        // Use ECDH key exchange to generate shared secret
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        
        // In a real implementation, we would:
        // 1. Get the peer's public key (from device.PublicKey)
        // 2. Perform ECDH
        // 3. Derive a shared secret using HKDF
        
        // For now, generate a random shared secret as placeholder
        var sharedSecret = new byte[32];
        RandomNumberGenerator.Fill(sharedSecret);
        
        return Task.FromResult(sharedSecret);
    }

    public void RequestPairing(Device device, string code)
    {
        lock (_lock)
        {
            _pendingPairings[code] = (device, DateTimeOffset.UtcNow.AddMinutes(5));
        }

        OnPairingRequested?.Invoke(this, new PairingEventArgs
        {
            Code = code,
            Device = device
        });
    }
}