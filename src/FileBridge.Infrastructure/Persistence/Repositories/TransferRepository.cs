namespace FileBridge.Infrastructure.Persistence.Repositories;

using FileBridge.Domain.Entities;
using FileBridge.Domain.Enums;

public class TransferRepository : ITransferRepository
{
    private readonly FileBridgeDbContext _context;

    public TransferRepository(FileBridgeDbContext context)
    {
        _context = context;
    }

    public async Task<IEnumerable<TransferJob>> GetAllAsync(CancellationToken ct = default)
    {
        var records = await _context.TransferJobs.ToListAsync();
        return records.Select(MapToDomain);
    }

    public async Task<IEnumerable<TransferJob>> GetByStatusAsync(TransferStatus status, CancellationToken ct = default)
    {
        var records = await _context.TransferJobs
            .Where(t => t.Status == (int)status)
            .ToListAsync();
        return records.Select(MapToDomain);
    }

    public async Task<TransferJob?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        var record = await _context.TransferJobs
            .Where(t => t.Id == id)
            .FirstOrDefaultAsync();
        return record != null ? MapToDomain(record) : null;
    }

    public async Task AddAsync(TransferJob job, CancellationToken ct = default)
    {
        var record = MapToRecord(job);
        await _context.InsertAsync(record);
    }

    public async Task UpdateAsync(TransferJob job, CancellationToken ct = default)
    {
        var record = MapToRecord(job);
        await _context.UpdateAsync(record);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        var record = await _context.TransferJobs
            .Where(t => t.Id == id)
            .FirstOrDefaultAsync();
        if (record != null)
        {
            await _context.DeleteAsync(record);
        }
    }

    public async Task<IEnumerable<TransferJob>> GetHistoryAsync(int limit = 50, CancellationToken ct = default)
    {
        var records = await _context.TransferJobs
            .OrderByDescending(t => t.CreatedAt)
            .Take(limit)
            .ToListAsync();
        return records.Select(MapToDomain);
    }

    public async Task UpdateProgressAsync(string jobId, long transferredBytes, int lastChunkIndex, CancellationToken ct = default)
    {
        var record = await _context.TransferJobs
            .Where(t => t.Id == jobId)
            .FirstOrDefaultAsync();

        if (record != null)
        {
            record.TransferredBytes = transferredBytes;
            record.ResumeToken = Convert.ToBase64String(BitConverter.GetBytes(transferredBytes));
            record.Status = (int)TransferStatus.Paused;
            await _context.UpdateAsync(record);
        }
    }

    private static TransferJob MapToDomain(TransferJobRecord record)
    {
        return new TransferJob
        {
            Id = Guid.Parse(record.Id),
            FileItemId = Guid.Parse(record.FileItemId),
            DeviceId = Guid.Parse(record.DeviceId),
            Status = (TransferStatus)record.Status,
            Direction = (TransferDirection)record.Direction,
            TotalBytes = record.TotalBytes,
            TransferredBytes = record.TransferredBytes,
            ChunkSize = record.ChunkSize,
            IsEncrypted = record.IsEncrypted,
            ResumeToken = record.ResumeToken,
            LocalFilePath = record.LocalFilePath,
            RemoteFilePath = record.RemoteFilePath,
            ErrorMessage = record.ErrorMessage,
            CreatedAt = new DateTimeOffset(record.CreatedAt),
            CompletedAt = record.CompletedAt.HasValue 
                ? new DateTimeOffset(record.CompletedAt.Value) 
                : null
        };
    }

    private static TransferJobRecord MapToRecord(TransferJob job)
    {
        return new TransferJobRecord
        {
            Id = job.Id.ToString(),
            FileItemId = job.FileItemId.ToString(),
            DeviceId = job.DeviceId.ToString(),
            Status = (int)job.Status,
            Direction = (int)job.Direction,
            TotalBytes = job.TotalBytes,
            TransferredBytes = job.TransferredBytes,
            ChunkSize = job.ChunkSize,
            IsEncrypted = job.IsEncrypted,
            ResumeToken = job.ResumeToken,
            LocalFilePath = job.LocalFilePath,
            RemoteFilePath = job.RemoteFilePath,
            ErrorMessage = job.ErrorMessage,
            CreatedAt = job.CreatedAt.UtcDateTime,
            CompletedAt = job.CompletedAt?.UtcDateTime
        };
    }
}
