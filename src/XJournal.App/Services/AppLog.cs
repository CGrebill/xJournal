using System.Globalization;
using System.Text;
using XJournal.Core.Accounts;

namespace XJournal.App.Services;

/// <summary>
/// A small local log for unexpected errors. Only technical details are written (error type,
/// message and stack trace) — never journal text, usernames or passwords.
/// </summary>
public static class AppLog
{
    public const int KeepFiles = 10;

    private static readonly Lock Gate = new();

    public static string Folder { get; set; } = Path.Combine(AccountStore.DefaultRootDirectory, "logs");

    /// <summary>Appends an error to today's log. Never throws.</summary>
    /// <returns>The log file's path, or null if it couldn't be written.</returns>
    public static string? Write(Exception? exception, string context)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var path = Path.Combine(Folder, $"xjournal-{DateTime.Now:yyyyMMdd}.log");
                var text = new StringBuilder()
                    .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
                    .Append("  [").Append(context).Append("]  xJournal ")
                    .Append(Core.Backup.JournalTransfer.AppVersion)
                    .Append(" on ").AppendLine(Environment.OSVersion.VersionString)
                    .AppendLine(exception?.ToString() ?? "(no exception details)")
                    .AppendLine();
                File.AppendAllText(path, text.ToString());
                Prune();
                return path;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Prune()
    {
        foreach (var old in Directory.EnumerateFiles(Folder, "xjournal-*.log")
                     .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                     .Skip(KeepFiles))
        {
            File.Delete(old);
        }
    }
}
