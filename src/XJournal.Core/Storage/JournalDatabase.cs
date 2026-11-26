using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;

namespace XJournal.Core.Storage;

/// <summary>
/// An open, decrypted connection to one user's journal file.
/// The whole file (tables, full-text index, journal) is encrypted with SQLite3 Multiple Ciphers
/// using a key derived from the user's password. No password or hash is stored anywhere else.
/// Not thread-safe: use from one thread (the UI thread) at a time.
/// </summary>
public sealed class JournalDatabase : IDisposable
{
    /// <summary>Written to the SQLite header to identify xJournal files ("xJRN").</summary>
    public const int ApplicationId = 0x784A524E;

    // Cipher parameters. These are not all recorded in the file, so they must never change
    // without a migration path: every journal and backup ever written depends on them.
    private const string CipherSetup = "PRAGMA cipher = 'chacha20'; PRAGMA kdf_iter = 256000;";

    private static int _initialized;

    private SqliteConnection? _connection;

    private JournalDatabase(SqliteConnection connection, string filePath)
    {
        _connection = connection;
        FilePath = filePath;
    }

    public string FilePath { get; }

    internal SqliteConnection Connection =>
        _connection ?? throw new ObjectDisposedException(nameof(JournalDatabase));

    public int SchemaVersion => Connection.ExecuteScalar<int>("PRAGMA user_version");

    /// <summary>Creates a brand-new encrypted journal. Fails if the file already exists.</summary>
    public static JournalDatabase Create(string path, string password)
    {
        if (File.Exists(path)) throw new IOException($"'{path}' already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var connection = OpenKeyed(path, password, SqliteOpenMode.ReadWriteCreate);
        try
        {
            Schema.Migrate(connection);
            return new JournalDatabase(connection, path);
        }
        catch
        {
            connection.Dispose();
            TryDelete(path);
            throw;
        }
    }

    /// <summary>
    /// Opens an existing journal, verifying the password and upgrading the schema if needed.
    /// </summary>
    /// <exception cref="InvalidPasswordException">Wrong password, or not an encrypted SQLite file.</exception>
    /// <exception cref="NotAJournalException">Decrypted, but not an xJournal file.</exception>
    /// <exception cref="NewerVersionException">Written by a newer xJournal.</exception>
    public static JournalDatabase Open(string path, string password)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Journal file not found.", path);
        var connection = OpenKeyed(path, password, SqliteOpenMode.ReadWrite);
        try
        {
            Verify(connection);
            Schema.Migrate(connection);
            return new JournalDatabase(connection, path);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Checks a password against a journal file without keeping it open.</summary>
    public static bool CheckPassword(string path, string password)
    {
        try
        {
            using var connection = OpenKeyed(path, password, SqliteOpenMode.ReadOnly);
            connection.ExecuteScalar<long>("SELECT count(*) FROM sqlite_master");
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 26) // SQLITE_NOTADB
        {
            return false;
        }
        catch (InvalidPasswordException)
        {
            return false;
        }
    }

    public bool CheckPassword(string password) => CheckPassword(FilePath, password);

    /// <summary>Re-encrypts the journal with a new password.</summary>
    public void ChangePassword(string newPassword)
    {
        ValidatePassword(newPassword);
        Connection.Execute($"PRAGMA hexrekey = '{Hex(newPassword)}'");
    }

    /// <summary>
    /// Writes a consistent, encrypted snapshot of the journal to <paramref name="destinationPath"/>
    /// (overwriting it). When <paramref name="password"/> is null the copy uses the journal's
    /// current password; otherwise it is encrypted with the given password.
    /// </summary>
    public void BackupTo(string destinationPath, string? password = null)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temp = fullPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            if (password is null)
            {
                // VACUUM INTO writes a compact copy encrypted with the current key.
                Connection.Execute("VACUUM INTO $path", new { path = temp });
            }
            else
            {
                ValidatePassword(password);
                using var destination = OpenKeyed(temp, password, SqliteOpenMode.ReadWriteCreate);
                Connection.BackupDatabase(destination);
            }
            File.Move(temp, fullPath, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }

    internal static SqliteConnection OpenKeyed(string path, string password, SqliteOpenMode mode)
    {
        EnsureInitialized();
        ValidatePassword(password);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            // hexkey avoids any quoting issues: the password's UTF-8 bytes are the passphrase.
            connection.Execute($"{CipherSetup} PRAGMA hexkey = '{Hex(password)}';");
            connection.Execute("PRAGMA foreign_keys = ON;");
            return connection;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 26)
        {
            connection.Dispose();
            throw new InvalidPasswordException(ex);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void Verify(SqliteConnection connection)
    {
        int applicationId;
        try
        {
            applicationId = connection.ExecuteScalar<int>("PRAGMA application_id");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 26)
        {
            throw new InvalidPasswordException(ex);
        }

        var version = connection.ExecuteScalar<int>("PRAGMA user_version");
        if (applicationId != ApplicationId) throw new NotAJournalException();
        if (version > Schema.CurrentVersion) throw new NewerVersionException(version, Schema.CurrentVersion);
    }

    internal static void ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("Password must not be empty.", nameof(password));
    }

    internal static void EnsureInitialized()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1) return;
        SQLitePCL.Batteries_V2.Init();
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    private static string Hex(string value) => Convert.ToHexString(Encoding.UTF8.GetBytes(value));

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
