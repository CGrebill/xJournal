using XJournal.Core;
using XJournal.Core.Accounts;

namespace XJournal.Core.Tests;

/// <summary>A controllable clock in a fixed UTC-5 zone.</summary>
public sealed class FakeClock : IClock
{
    public static readonly TimeZoneInfo Zone =
        TimeZoneInfo.CreateCustomTimeZone("Test/UTC-5", TimeSpan.FromHours(-5), "Test", "Test");

    public FakeClock(DateTimeOffset? start = null) =>
        Now = start ?? new DateTimeOffset(2026, 11, 25, 14, 30, 15, 123, TimeSpan.FromHours(-5));

    public DateTimeOffset Now { get; set; }
    public TimeZoneInfo TimeZone => Zone;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>An isolated app-data folder (a stand-in for one computer).</summary>
public sealed class TestEnv : IDisposable
{
    public const string Password = "correct horse";

    public TestEnv(FakeClock? clock = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "xjournal-tests", Guid.NewGuid().ToString("N"));
        Clock = clock ?? new FakeClock();
        Accounts = new AccountStore(Root, Clock);
    }

    public string Root { get; }
    public FakeClock Clock { get; }
    public AccountStore Accounts { get; }

    public Journal CreateJournal(string username = "caleb") => Accounts.Create(username, Password);

    public string TempFile(string name) => Path.Combine(Root, "files", name);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}
