namespace FileBridge.Infrastructure.Persistence.Repositories;

public interface ISettingsRepository
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string value, CancellationToken ct = default);
    Task<IEnumerable<(string Key, string Value)>> GetAllAsync(CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}
