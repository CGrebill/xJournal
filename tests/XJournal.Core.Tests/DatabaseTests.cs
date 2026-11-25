using System.Text;
using Dapper;
using XJournal.Core.Sessions;
using XJournal.Core.Storage;

namespace XJournal.Core.Tests;

public class DatabaseTests
{
    private const string Secret = "PURPLEMONKEYDISHWASHER";

    [Fact]
    public void Journal_roundtrips_after_reopening()
    {
        using var env = new TestEnv();
        using (var journal = env.CreateJournal())
        {
            var session = new EntrySession(journal);
            session.Begin();
            session.SetText("A title", "Hello there");
            session.Save();
        }

        using var reopened = env.Accounts.SignIn("caleb", TestEnv.Password);
        var entry = Assert.Single(reopened.Entries.GetAll());
        Assert.Equal("Hello there", entry.Body);
        Assert.Equal("A title", entry.Summary);
    }

    [Fact]
    public void Wrong_password_is_rejected()
    {
        using var env = new TestEnv();
        env.CreateJournal().Dispose();
        Assert.Throws<InvalidPasswordException>(() => env.Accounts.SignIn("caleb", "wrong password"));
        Assert.False(JournalDatabase.CheckPassword(env.Accounts.PathFor("caleb"), "wrong password"));
        Assert.True(JournalDatabase.CheckPassword(env.Accounts.PathFor("caleb"), TestEnv.Password));
    }

    [Fact]
    public void Journal_file_contains_no_plaintext()
    {
        using var env = new TestEnv();
        using (var journal = env.CreateJournal())
        {
            var session = new EntrySession(journal);
            session.SetText(Secret + " summary", "Body with " + Secret + " inside, indexed for search.");
            session.Save();
            Assert.Single(journal.Entries.Search(new(Secret.ToLowerInvariant())));
            journal.Settings.Set("note", Secret);
        }

        var bytes = File.ReadAllBytes(env.Accounts.PathFor("caleb"));
        Assert.DoesNotContain(Secret, Encoding.Latin1.GetString(bytes));
        Assert.DoesNotContain(Secret, Encoding.Unicode.GetString(bytes));
        Assert.DoesNotContain("SQLite format 3", Encoding.Latin1.GetString(bytes));
    }

    [Fact]
    public void Password_can_be_changed()
    {
        using var env = new TestEnv();
        using (var journal = env.CreateJournal())
            journal.Database.ChangePassword("a new password");

        Assert.Throws<InvalidPasswordException>(() => env.Accounts.SignIn("caleb", TestEnv.Password));
        using var reopened = env.Accounts.SignIn("caleb", "a new password");
        Assert.Equal(0, reopened.Entries.Count());
    }

    [Fact]
    public void Unicode_and_quote_passwords_work()
    {
        using var env = new TestEnv();
        const string password = "pä'ss\"wörd 🔒;--";
        env.Accounts.Create("caleb", password).Dispose();
        using var journal = env.Accounts.SignIn("caleb", password);
        Assert.Throws<InvalidPasswordException>(() => env.Accounts.SignIn("caleb", "pä'ss\"wörd"));
    }

    [Fact]
    public void Encrypted_file_that_is_not_a_journal_is_rejected()
    {
        using var env = new TestEnv();
        var path = env.TempFile("other.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var connection = JournalDatabase.OpenKeyed(path, TestEnv.Password, Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate))
            connection.Execute("CREATE TABLE t (x)");

        Assert.Throws<NotAJournalException>(() => JournalDatabase.Open(path, TestEnv.Password));
    }

    [Fact]
    public void Journal_from_newer_version_is_rejected()
    {
        using var env = new TestEnv();
        using (var journal = env.CreateJournal())
            journal.Database.Connection.Execute("PRAGMA user_version = 999");

        Assert.Throws<NewerVersionException>(() => env.Accounts.SignIn("caleb", TestEnv.Password));
    }

    [Fact]
    public void Schema_is_at_current_version()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        Assert.Equal(Schema.CurrentVersion, journal.Database.SchemaVersion);
        Assert.Equal(JournalDatabase.ApplicationId, journal.Database.Connection.ExecuteScalar<int>("PRAGMA application_id"));
    }
}
