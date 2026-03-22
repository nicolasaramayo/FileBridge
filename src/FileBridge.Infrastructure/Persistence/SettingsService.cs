namespace FileBridge.Infrastructure.Persistence;

public interface ISettingsService
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, CancellationToken ct = default);
    Task<bool> GetBoolAsync(string key, bool defaultValue = false, CancellationToken ct = default);
    Task SetBoolAsync(string key, bool value, CancellationToken ct = default);
    Task<int> GetIntAsync(string key, int defaultValue = 0, CancellationToken ct = default);
    Task SetIntAsync(string key, int value, CancellationToken ct = default);
    Task<string?> GetStringAsync(string key, string? defaultValue = null, CancellationToken ct = default);
    Task SetStringAsync(string key, string? value, CancellationToken ct = default);
    Task<string[]> GetStringArrayAsync(string key, CancellationToken ct = default);
    Task SetStringArrayAsync(string key, string[] value, CancellationToken ct = default);
}

public class SettingsService : ISettingsService
{
    private readonly Repositories.ISettingsRepository _repository;
    private readonly Dictionary<string, object?> _cache = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SettingsService(Repositories.ISettingsRepository repository)
    {
        _repository = repository;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        var value = await _repository.GetAsync(key, ct);
        if (value == null) return default;

        if (typeof(T) == typeof(string))
            return (T)(object)value;
        if (typeof(T) == typeof(int))
            return (T)(object)int.Parse(value);
        if (typeof(T) == typeof(long))
            return (T)(object)long.Parse(value);
        if (typeof(T) == typeof(bool))
            return (T)(object)bool.Parse(value);
        if (typeof(T) == typeof(double))
            return (T)(object)double.Parse(value);

        return System.Text.Json.JsonSerializer.Deserialize<T>(value);
    }

    public async Task SetAsync<T>(string key, T value, CancellationToken ct = default)
    {
        string stringValue;
        if (typeof(T) == typeof(string))
            stringValue = (string)(object)value!;
        else if (typeof(T) == typeof(int) || typeof(T) == typeof(long) || 
                 typeof(T) == typeof(bool) || typeof(T) == typeof(double))
            stringValue = value?.ToString() ?? string.Empty;
        else
            stringValue = System.Text.Json.JsonSerializer.Serialize(value);

        await _lock.WaitAsync(ct);
        try
        {
            _cache[key] = value;
        }
        finally
        {
            _lock.Release();
        }

        await _repository.SetAsync(key, stringValue, ct);
    }

    public async Task<bool> GetBoolAsync(string key, bool defaultValue = false, CancellationToken ct = default)
    {
        var result = await GetAsync<string>(key, ct);
        return bool.TryParse(result, out var value) ? value : defaultValue;
    }

    public async Task SetBoolAsync(string key, bool value, CancellationToken ct = default)
    {
        await SetAsync(key, value.ToString(), ct);
    }

    public async Task<int> GetIntAsync(string key, int defaultValue = 0, CancellationToken ct = default)
    {
        var result = await GetAsync<string>(key, ct);
        return int.TryParse(result, out var value) ? value : defaultValue;
    }

    public async Task SetIntAsync(string key, int value, CancellationToken ct = default)
    {
        await SetAsync(key, value.ToString(), ct);
    }

    public async Task<string?> GetStringAsync(string key, string? defaultValue = null, CancellationToken ct = default)
    {
        var result = await GetAsync<string>(key, ct);
        return result ?? defaultValue;
    }

    public async Task SetStringAsync(string key, string? value, CancellationToken ct = default)
    {
        await SetAsync(key, value ?? string.Empty, ct);
    }

    public async Task<string[]> GetStringArrayAsync(string key, CancellationToken ct = default)
    {
        var result = await GetAsync<string>(key, ct);
        if (string.IsNullOrEmpty(result))
            return Array.Empty<string>();
        
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<string[]>(result) ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public async Task SetStringArrayAsync(string key, string[] value, CancellationToken ct = default)
    {
        await SetAsync(key, System.Text.Json.JsonSerializer.Serialize(value), ct);
    }
}
