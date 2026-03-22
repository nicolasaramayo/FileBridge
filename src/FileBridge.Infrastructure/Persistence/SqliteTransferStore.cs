namespace FileBridge.Infrastructure.Persistence;

using FileBridge.Domain.Entities;
using FileBridge.Domain.Enums;
using FileBridge.Domain.Interfaces;
using FileBridge.Infrastructure.Persistence.Repositories;

public class SqliteTransferStore : ITransferStore
{
    private readonly FileBridgeDbContext _context;
    private readonly ITransferRepository _repository;

    public SqliteTransferStore(FileBridgeDbContext context)
    {
        _context = context;
        _repository = new TransferRepository(context);
    }

    public async Task<int> SaveJobAsync(TransferJob job, CancellationToken ct = default)
    {
        await _repository.AddAsync(job, ct);
        return 1;
    }

    public async Task<int> UpdateJobAsync(TransferJob job, CancellationToken ct = default)
    {
        await _repository.UpdateAsync(job, ct);
        return 1;
    }

    public async Task<TransferJob?> GetJobAsync(string id, CancellationToken ct = default)
    {
        return await _repository.GetByIdAsync(id, ct);
    }

    public async Task<IEnumerable<TransferJob>> GetActiveJobsAsync(CancellationToken ct = default)
    {
        var inProgress = await _repository.GetByStatusAsync(TransferStatus.InProgress, ct);
        var paused = await _repository.GetByStatusAsync(TransferStatus.Paused, ct);
        return inProgress.Concat(paused);
    }

    public async Task<IEnumerable<TransferJob>> GetHistoryAsync(int limit = 50, CancellationToken ct = default)
    {
        return await _repository.GetHistoryAsync(limit, ct);
    }

    public async Task<int> UpdateProgressAsync(string jobId, long transferredBytes, int lastChunkIndex, CancellationToken ct = default)
    {
        await _repository.UpdateProgressAsync(jobId, transferredBytes, lastChunkIndex, ct);
        return 1;
    }

    public async Task<int> DeleteJobAsync(string id, CancellationToken ct = default)
    {
        await _repository.DeleteAsync(id, ct);
        return 1;
    }
}
