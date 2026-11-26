using System.Globalization;
using Dapper;

namespace XJournal.Core.Storage;

/// <summary>Per-journal settings (stored encrypted inside the journal).</summary>
public sealed class SettingsRepository(JournalDatabase db)
{
    public static readonly TimeSpan DefaultResumeWindow = TimeSpan.FromMinutes(30);
    public const int DefaultAutoBackupKeep = 10;
    public const int DefaultAutoLockMinutes = 15;

    public string? Get(string key) =>
        db.Connection.ExecuteScalar<string?>("SELECT value FROM settings WHERE key = $key", new { key });

    public void Set(string key, string? value)
    {
        if (value is null)
            db.Connection.Execute("DELETE FROM settings WHERE key = $key", new { key });
        else
            db.Connection.Execute(
                "INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
                new { key, value });
    }

    public IReadOnlyDictionary<string, string> GetAll() =>
        db.Connection.Query<(string Key, string Value)>("SELECT key, value FROM settings ORDER BY key")
            .ToDictionary(r => r.Key, r => r.Value);

    /// <summary>How recently the last entry must have been edited for it to reopen on sign-in.</summary>
    public TimeSpan ResumeWindow
    {
        get => int.TryParse(Get("resume_window_minutes"), CultureInfo.InvariantCulture, out var m) && m >= 0
            ? TimeSpan.FromMinutes(m)
            : DefaultResumeWindow;
        set => Set("resume_window_minutes", ((int)value.TotalMinutes).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Folder for automatic encrypted backups on sign-out; null disables them.</summary>
    public string? AutoBackupFolder
    {
        get => Get("auto_backup_folder") is { Length: > 0 } folder ? folder : null;
        set => Set("auto_backup_folder", string.IsNullOrWhiteSpace(value) ? null : value);
    }

    /// <summary>How many automatic backups to keep in <see cref="AutoBackupFolder"/>.</summary>
    public int AutoBackupKeep
    {
        get => int.TryParse(Get("auto_backup_keep"), CultureInfo.InvariantCulture, out var n) && n > 0 ? n : DefaultAutoBackupKeep;
        set => Set("auto_backup_keep", Math.Max(1, value).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Sign out automatically after this many minutes without any input; 0 turns it off.</summary>
    public int AutoLockMinutes
    {
        get => int.TryParse(Get("auto_lock_minutes"), CultureInfo.InvariantCulture, out var m) && m >= 0 ? m : DefaultAutoLockMinutes;
        set => Set("auto_lock_minutes", Math.Max(0, value).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>"system", "light" or "dark".</summary>
    public string Theme
    {
        get => Get("theme") ?? "system";
        set => Set("theme", value);
    }
}
