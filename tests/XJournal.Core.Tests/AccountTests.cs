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
