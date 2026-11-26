using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XJournal.App.Services;
using XJournal.Core;
using XJournal.Core.Accounts;

namespace XJournal.App.ViewModels;

public sealed partial class LoginViewModel : ViewModelBase
{
    private readonly AccountStore _accounts;
    private readonly IDialogs _dialogs;

    public LoginViewModel(AccountStore accounts, IDialogs dialogs)
    {
        _accounts = accounts;
        _dialogs = dialogs;
        Accounts = new ObservableCollection<string>(accounts.ListAccounts());
        IsCreateMode = Accounts.Count == 0;
        Username = accounts.LastUsername is { } last && Accounts.Contains(last) ? last : Accounts.FirstOrDefault() ?? "";
    }

    /// <summary>Raised on the UI thread with the opened journal.</summary>
    public event Action<Journal>? SignedIn;

    public ObservableCollection<string> Accounts { get; }

    [ObservableProperty] public partial string Username { get; set; }
    [ObservableProperty] public partial string Password { get; set; } = "";
    [ObservableProperty] public partial string ConfirmPassword { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading), nameof(PrimaryText), nameof(SwitchModeText))]
    public partial bool IsCreateMode { get; set; }

    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial string? Notice { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand), nameof(ImportJournalCommand))]
    public partial bool IsBusy { get; set; }

    public string Heading => IsCreateMode ? "Create your journal" : "Welcome back";
    public string PrimaryText => IsCreateMode ? "Create journal" : "Sign in";
    public string SwitchModeText => IsCreateMode ? "I already have an account" : "Create a new account";

    [RelayCommand]
    private void SwitchMode()
    {
        IsCreateMode = !IsCreateMode;
        Error = null;
        Password = ConfirmPassword = "";
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task SubmitAsync()
    {
        Error = null;
        var username = Username?.Trim() ?? "";
        var password = Password;
        if (IsCreateMode)
        {
            if (AccountStore.ValidateUsername(username) is { } usernameError) { Error = usernameError; return; }
            if (AccountStore.ValidateNewPassword(password, ConfirmPassword) is { } passwordError) { Error = passwordError; return; }
        }
        else if (username.Length == 0 || password.Length == 0)
        {
            Error = "Enter your username and password.";
            return;
        }

        IsBusy = true;
        try
        {
            // Deriving the encryption key is deliberately slow, so keep the UI responsive.
            var createMode = IsCreateMode;
            var journal = await Task.Run(() => createMode
                ? _accounts.Create(username, password)
                : _accounts.SignIn(username, password));
            Password = ConfirmPassword = "";
            SignedIn?.Invoke(journal);
        }
        catch (JournalException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ImportJournalAsync()
    {
        var path = await _dialogs.PickOpenFileAsync("Import a journal", FileTypes.AnyJournal, FileTypes.Backup, FileTypes.JournalJson);
        if (path is null) return;

        var import = new ImportJournalViewModel(_accounts, path, Username);
        var view = new Views.ImportJournalView { DataContext = import };
        var accepted = await _dialogs.ShowContentAsync("Import journal", view, "Import", "Cancel", canClose: import.ValidateAsync);
        if (!accepted) return;

        IsBusy = true;
        try
        {
            var journal = await Task.Run(import.Run);
            SignedIn?.Invoke(journal);
        }
        catch (JournalException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex) when (Errors.IsExpected(ex))
        {
            Error = "The import failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool IsIdle() => !IsBusy;
}
