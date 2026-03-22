namespace FileBridge.Infrastructure.Persistence.Repositories;

public class SettingsRepository : ISettingsRepository
{
    private readonly FileBridgeDbContext _context;

    public SettingsRepository(FileBridgeDbContext context)
    {
        _context = context;
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var record = await _context.Settings
            .Where(s => s.Key == key)
            .FirstOrDefaultAsync();
        return record?.Value;
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        var record = new SettingRecord
        {
            Key = key,
            Value = value,
            UpdatedAt = DateTime.UtcNow
        };
        await _context.InsertOrReplaceAsync(record);
    }

    public async Task<IEnumerable<(string Key, string Value)>> GetAllAsync(CancellationToken ct = default)
    {
        var records = await _context.Settings.ToListAsync();
        return records.Select(r => (r.Key, r.Value));
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var record = await _context.Settings
            .Where(s => s.Key == key)
            .FirstOrDefaultAsync();
        if (record != null)
        {
            await _context.DeleteAsync(record);
        }
    }
}
