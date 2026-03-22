namespace FileBridge.Infrastructure.Persistence;

using SQLite;

public class FileBridgeDbContext : IDisposable
{
    private readonly SQLiteAsyncConnection _db;
    private bool _disposed;

    public FileBridgeDbContext(string dbPath)
    {
        var syncDb = new SQLiteConnection(dbPath);
        syncDb.CreateTable<TransferJobRecord>();
        syncDb.CreateTable<DeviceRecord>();
        syncDb.CreateTable<SettingRecord>();
        syncDb.Close();

        _db = new SQLiteAsyncConnection(dbPath);
    }

    public AsyncTableQuery<TransferJobRecord> TransferJobs => _db.Table<TransferJobRecord>();
    public AsyncTableQuery<DeviceRecord> Devices => _db.Table<DeviceRecord>();
    public AsyncTableQuery<SettingRecord> Settings => _db.Table<SettingRecord>();

    public AsyncTableQuery<T> Table<T>() where T : class, new() => _db.Table<T>();

    public Task<int> InsertAsync<T>(T entity) => _db.InsertAsync(entity);
    public Task<int> InsertOrReplaceAsync<T>(T entity) => _db.InsertOrReplaceAsync(entity);
    public Task<int> UpdateAsync<T>(T entity) => _db.UpdateAsync(entity);
    public Task<int> DeleteAsync<T>(T entity) => _db.DeleteAsync(entity);
    public Task<T?> FindAsync<T>(object pk) where T : class, new() => _db.FindAsync<T>(pk);
    public Task<int> ExecuteAsync(string query, params object[] args) => _db.ExecuteAsync(query, args);
    public Task<DateTime> GetDatabaseTimeAsync() => _db.ExecuteScalarAsync<DateTime>("SELECT datetime('now')");

    public static string GetDefaultDatabasePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dbFolder = Path.Combine(appData, "FileBridge");
        Directory.CreateDirectory(dbFolder);
        return Path.Combine(dbFolder, "filebridge.db");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _db.CloseAsync().Wait();
        _disposed = true;
    }
}
