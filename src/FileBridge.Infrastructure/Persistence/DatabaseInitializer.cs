namespace FileBridge.Infrastructure.Persistence;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(string dbPath, CancellationToken ct = default)
    {
        var context = new FileBridgeDbContext(dbPath);
        
        // Enable WAL mode for better concurrency
        await context.ExecuteAsync("PRAGMA journal_mode=WAL");
        
        // Enable foreign keys
        await context.ExecuteAsync("PRAGMA foreign_keys=ON");
        
        // Create indexes for better query performance
        await context.ExecuteAsync(@"
            CREATE INDEX IF NOT EXISTS idx_transferjobs_status 
            ON TransferJobs(Status)");
        
        await context.ExecuteAsync(@"
            CREATE INDEX IF NOT EXISTS idx_transferjobs_deviceid 
            ON TransferJobs(DeviceId)");
        
        await context.ExecuteAsync(@"
            CREATE INDEX IF NOT EXISTS idx_transferjobs_createdat 
            ON TransferJobs(CreatedAt DESC)");
        
        await context.ExecuteAsync(@"
            CREATE INDEX IF NOT EXISTS idx_devices_lastseen 
            ON Devices(LastSeenAt DESC)");
        
        // Insert default settings if not exist
        await EnsureDefaultSettingsAsync(context);
        
        context.Dispose();
    }

    private static async Task EnsureDefaultSettingsAsync(FileBridgeDbContext context)
    {
        var defaultSettings = new Dictionary<string, string>
        {
            { "default_chunk_size", "65536" },
            { "encryption_enabled", "false" },
            { "default_folders", "[]" },
            { "server_port", "45678" },
            { "auto_discover", "true" }
        };

        foreach (var (key, value) in defaultSettings)
        {
            var existing = await context.Settings
                .Where(s => s.Key == key)
                .FirstOrDefaultAsync();
            
            if (existing == null)
            {
                await context.InsertAsync(new SettingRecord
                {
                    Key = key,
                    Value = value,
                    UpdatedAt = DateTime.UtcNow
                });
            }
        }
    }

    public static string GetDefaultDatabasePath()
    {
        return FileBridgeDbContext.GetDefaultDatabasePath();
    }
}
