using System.Text.Json;
using XJournal.Core.Storage;

namespace XJournal.Core.Accounts;

/// <summary>
/// Local accounts. Each account is one encrypted journal file named after the username:
/// <c>&lt;root&gt;/users/&lt;username&gt;.xjdb</c>.
/// </summary>
public sealed class AccountStore
{
    public const string JournalExtension = ".xjdb";
    public const string BackupExtension = ".xjbackup";
    public const int MinPasswordLength = 6;
    public const int MaxUsernameLength = 32;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public AccountStore(string? rootDirectory = null, IClock? clock = null)
    {
        RootDirectory = rootDirectory ?? DefaultRootDirectory;
        Clock = clock ?? SystemClock.Instance;
        DeleteStaleTempFiles();
    }

    /// <summary>Removes leftovers of imports that were interrupted (e.g. by a crash or power cut).</summary>
    private void DeleteStaleTempFiles()
    {
        if (!Directory.Exists(UsersDirectory)) return;
        foreach (var file in Directory.EnumerateFiles(UsersDirectory, ".import-*.tmp"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-1)) File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort; it will be retried next time.
            }
        }
    }

    public static string DefaultRootDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "xJournal");

    public string RootDirectory { get; }
    public IClock Clock { get; }
    public string UsersDirectory => Path.Combine(RootDirectory, "users");

    /// <summary>Copies of journals kept automatically before an import replaces them.</summary>
    public string SafetyBackupsDirectory => Path.Combine(RootDirectory, "replaced");

    private string StatePath => Path.Combine(RootDirectory, "state.json");

    public IReadOnlyList<string> ListAccounts() =>
        Directory.Exists(UsersDirectory)
            ? Directory.EnumerateFiles(UsersDirectory, "*" + JournalExtension)
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

    /// <summary>Finds an account case-insensitively and returns its exact name.</summary>
    public string? FindAccount(string username) =>
        ListAccounts().FirstOrDefault(a => string.Equals(a, username.Trim(), StringComparison.OrdinalIgnoreCase));

    public string PathFor(string username) => Path.Combine(UsersDirectory, username + JournalExtension);

    /// <returns>An error message, or null if the username is acceptable.</returns>
    public static string? ValidateUsername(string? username)
    {
        var name = username?.Trim() ?? "";
        if (name.Length == 0) return "Enter a username.";
        if (name.Length > MaxUsernameLength) return $"Usernames can be at most {MaxUsernameLength} characters.";
        if (!name.All(c => char.IsLetterOrDigit(c) || c is ' ' or '_' or '-' or '.'))
            return "Usernames can only contain letters, numbers, spaces, '_', '-' and '.'.";
        if (name.EndsWith('.') || name.StartsWith('.')) return "Usernames can't start or end with '.'.";
        if (ReservedNames.Contains(name)) return "That username is reserved by the system.";
        return null;
    }

    /// <returns>An error message, or null if the password is acceptable.</returns>
    public static string? ValidateNewPassword(string? password, string? confirmation)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
            return $"Use a password of at least {MinPasswordLength} characters.";
        if (password != confirmation) return "The passwords don't match.";
        return null;
    }

    public Journal Create(string username, string password)
    {
        username = username.Trim();
        if (ValidateUsername(username) is { } error) throw new AccountException(error);
        if (FindAccount(username) is not null) throw new AccountException("An account with that name already exists.");
        var database = JournalDatabase.Create(PathFor(username), password);
        LastUsername = username;
        return new Journal(username, database, Clock, SafetyBackupsDirectory);
    }

    /// <exception cref="AccountException">No such account.</exception>
    /// <exception cref="InvalidPasswordException">Wrong password.</exception>
    public Journal SignIn(string username, string password)
    {
        var name = FindAccount(username) ?? throw new AccountException("There's no account with that username.");
        var database = JournalDatabase.Open(PathFor(name), password);
        LastUsername = name;
        return new Journal(name, database, Clock, SafetyBackupsDirectory);
    }

    /// <summary>
    /// Moves a prepared journal file into place as <paramref name="username"/>'s journal.
    /// When replacing, the existing journal is first copied to <see cref="SafetyBackupsDirectory"/>.
    /// </summary>
    /// <returns>The path of the safety copy, if one was made.</returns>
    internal string? Install(string preparedFile, string username, bool replaceExisting)
    {
        var existing = FindAccount(username);
        string? safetyCopy = null;
        if (existing is not null)
        {
            if (!replaceExisting) throw new AccountException("An account with that name already exists.");
            Directory.CreateDirectory(SafetyBackupsDirectory);
            safetyCopy = Path.Combine(SafetyBackupsDirectory,
                $"{existing}-replaced-{Clock.LocalNow():yyyyMMdd-HHmmss}{BackupExtension}");
            File.Copy(PathFor(existing), safetyCopy, overwrite: true);
        }

        // Replace in one step, so there is never a moment without a journal file.
        // (An existing account keeps its original spelling; FindAccount ignores case.)
        Directory.CreateDirectory(UsersDirectory);
        File.Move(preparedFile, PathFor(existing ?? username), overwrite: true);
        return safetyCopy;
    }

    internal string NewTempPath() => Path.Combine(UsersDirectory, $".import-{Guid.NewGuid():N}.tmp");

    /// <summary>The username last signed in on this computer (not sensitive; stored unencrypted).</summary>
    public string? LastUsername
    {
        get
        {
            try
            {
                return File.Exists(StatePath)
                    ? JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath))?.LastUsername
                    : null;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }
        set
        {
            try
            {
                Directory.CreateDirectory(RootDirectory);
                File.WriteAllText(StatePath, JsonSerializer.Serialize(new State { LastUsername = value }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Remembering the username is a convenience only.
            }
        }
    }

    private sealed class State
    {
        public string? LastUsername { get; set; }
    }
}
