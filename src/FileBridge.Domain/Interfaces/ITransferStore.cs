namespace FileBridge.Domain.Interfaces;

using FileBridge.Domain.Entities;

public interface ITransferStore
{
    Task<int> SaveJobAsync(TransferJob job, CancellationToken ct = default);
    Task<int> UpdateJobAsync(TransferJob job, CancellationToken ct = default);
    Task<TransferJob?> GetJobAsync(string id, CancellationToken ct = default);
    Task<IEnumerable<TransferJob>> GetActiveJobsAsync(CancellationToken ct = default);
    Task<IEnumerable<TransferJob>> GetHistoryAsync(int limit = 50, CancellationToken ct = default);
    Task<int> UpdateProgressAsync(string jobId, long transferredBytes, int lastChunkIndex, CancellationToken ct = default);
    Task<int> DeleteJobAsync(string id, CancellationToken ct = default);
}

public interface IDeviceRepository
{
    Task<int> SaveAsync(Domain.Entities.Device device, CancellationToken ct = default);
    Task<int> UpdateAsync(Domain.Entities.Device device, CancellationToken ct = default);
    Task<Domain.Entities.Device?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<IEnumerable<Domain.Entities.Device>> GetAllPairedAsync(CancellationToken ct = default);
    Task<int> UpdateLastSeenAsync(string id, CancellationToken ct = default);
    Task<int> DeleteAsync(string id, CancellationToken ct = default);
}
