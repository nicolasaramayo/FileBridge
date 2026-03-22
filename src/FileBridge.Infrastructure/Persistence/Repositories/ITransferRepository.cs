namespace FileBridge.Infrastructure.Persistence.Repositories;

using FileBridge.Domain.Entities;
using FileBridge.Domain.Enums;

public interface ITransferRepository
{
    Task<IEnumerable<TransferJob>> GetAllAsync(CancellationToken ct = default);
    Task<IEnumerable<TransferJob>> GetByStatusAsync(TransferStatus status, CancellationToken ct = default);
    Task<TransferJob?> GetByIdAsync(string id, CancellationToken ct = default);
    Task AddAsync(TransferJob job, CancellationToken ct = default);
    Task UpdateAsync(TransferJob job, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task<IEnumerable<TransferJob>> GetHistoryAsync(int limit = 50, CancellationToken ct = default);
    Task UpdateProgressAsync(string jobId, long transferredBytes, int lastChunkIndex, CancellationToken ct = default);
}
