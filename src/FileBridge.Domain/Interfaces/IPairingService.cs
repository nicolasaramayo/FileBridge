using FileBridge.Domain.Entities;

namespace FileBridge.Domain.Interfaces;

public class PairingEventArgs : EventArgs
{
    public string Code { get; init; } = string.Empty;
    public Device Device { get; init; } = null!;
}

public interface IPairingService
{
    Task<string> GeneratePairingCodeAsync();
    Task<bool> ValidatePairingCodeAsync(string code, Device device);
    Task<byte[]> GetSharedSecretAsync(Device device);
    event EventHandler<PairingEventArgs>? OnPairingRequested;
    event EventHandler<Device>? OnPairingCompleted;
}
