namespace FileBridge.Infrastructure.Persistence.Repositories;

using FileBridge.Domain.Entities;

public interface IDeviceRepository
{
    Task<IEnumerable<Device>> GetAllAsync(CancellationToken ct = default);
    Task<IEnumerable<Device>> GetAllPairedAsync(CancellationToken ct = default);
    Task<Device?> GetByIdAsync(string id, CancellationToken ct = default);
    Task AddAsync(Device device, CancellationToken ct = default);
    Task UpdateAsync(Device device, CancellationToken ct = default);
    Task UpdateLastSeenAsync(string id, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
}
