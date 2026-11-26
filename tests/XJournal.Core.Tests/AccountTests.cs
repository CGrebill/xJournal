using XJournal.Core.Accounts;

namespace XJournal.Core.Tests;

public class AccountTests
{
    [Theory]
    [InlineData("caleb", true)]
    [InlineData("Caleb G-2_x.y", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("a/b", false)]
    [InlineData("..", false)]
    [InlineData("CON", false)]
    [InlineData("name.", false)]
    [InlineData("this-username-is-way-too-long-to-be-accepted", false)]
    public void Usernames_are_validated(string name, bool valid) =>
        Assert.Equal(valid, AccountStore.ValidateUsername(name) is null);

    [Fact]
    public void Passwords_are_validated()
    {
        Assert.NotNull(AccountStore.ValidateNewPassword("short", "short"));
        Assert.NotNull(AccountStore.ValidateNewPassword("long enough", "different"));
        Assert.Null(AccountStore.ValidateNewPassword("long enough", "long enough"));
    }

    [Fact]
    public void Accounts_are_listed_and_matched_case_insensitively()
    {
        using var env = new TestEnv();
        env.CreateJournal("Caleb").Dispose();
        env.CreateJournal("alex").Dispose();

        Assert.Equal(["alex", "Caleb"], env.Accounts.ListAccounts());
        Assert.Throws<AccountException>(() => env.Accounts.Create("caleb", TestEnv.Password));
        using var journal = env.Accounts.SignIn("CALEB", TestEnv.Password);
        Assert.Equal("Caleb", journal.Username);
        Assert.Equal("Caleb", env.Accounts.LastUsername);
    }

    [Fact]
    public void Unknown_account_is_reported()
    {
        using var env = new TestEnv();
        var ex = Assert.Throws<AccountException>(() => env.Accounts.SignIn("nobody", TestEnv.Password));
        Assert.Contains("no account", ex.Message);
    }
}

public class AccountMaintenanceTests
{
    [Fact]
    public void Stale_import_leftovers_are_cleaned_up_but_recent_ones_are_kept()
    {
        using var env = new TestEnv();
        env.CreateJournal().Dispose();
        var stale = Path.Combine(env.Accounts.UsersDirectory, ".import-old.tmp");
        var recent = Path.Combine(env.Accounts.UsersDirectory, ".import-new.tmp");
        File.WriteAllText(stale, "x");
        File.WriteAllText(recent, "x");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));

        _ = new Accounts.AccountStore(env.Root, env.Clock);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(recent)); // might belong to an import in progress
        Assert.Equal(["caleb"], env.Accounts.ListAccounts());
    }

    [Fact]
    public void Replacing_an_account_keeps_its_original_spelling()
    {
        using var env = new TestEnv();
        string backup;
        using (var source = env.CreateJournal("source"))
        {
            backup = env.TempFile("b.xjbackup");
            Backup.JournalTransfer.ExportEncrypted(source, backup);
        }
        env.CreateJournal("Caleb").Dispose();

        using var imported = new Backup.JournalTransfer(env.Accounts)
            .ImportBackup(backup, TestEnv.Password, "caleb", null, replaceExisting: true);

        Assert.Equal("Caleb", imported.Username);
        Assert.Equal(["Caleb", "source"], env.Accounts.ListAccounts());
    }

    [Fact]
    public void Disposing_a_journal_marks_it_closed()
    {
        using var env = new TestEnv();
        var journal = env.CreateJournal();
        Assert.False(journal.IsDisposed);
        journal.Dispose();
        Assert.True(journal.IsDisposed);
    }
}
