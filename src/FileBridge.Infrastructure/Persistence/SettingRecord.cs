namespace FileBridge.Infrastructure.Persistence;

using SQLite;

[Table("Settings")]
public class SettingRecord
{
    [PrimaryKey]
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
