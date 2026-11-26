using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using XJournal.App.ViewModels;
using XJournal.App.Views;
using XJournal.Core;
using XJournal.Core.Accounts;

namespace XJournal.App.Services;

/// <summary>Moves between the login screen and the journal, and shuts the app down when the last one closes.</summary>
public sealed class AppShell(IClassicDesktopStyleApplicationLifetime desktop, AccountStore accounts)
{
    private Window? _current;

    public AccountStore Accounts => accounts;

    public void ShowLogin(string? notice = null)
    {
        ApplyTheme("system");
        var window = new LoginWindow();
        var viewModel = new LoginViewModel(accounts, new WindowDialogs(window)) { Notice = notice };
        viewModel.SignedIn += ShowJournal;
        window.DataContext = viewModel;
        Swap(window);
    }

    public void ShowJournal(Journal journal)
    {
        ApplyTheme(journal.Settings.Theme);
        var window = new MainWindow();
        var viewModel = new MainViewModel(journal, new WindowDialogs(window), window);
        viewModel.SignedOut += notice => ShowLogin(notice);
        window.DataContext = viewModel;
        Swap(window);
    }

    public static void ApplyTheme(string theme)
    {
        if (Application.Current is null) return;
        Application.Current.RequestedThemeVariant = theme switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    private void Swap(Window next)
    {
        var previous = _current;
        _current = next;
        next.Closed += (_, _) =>
        {
            if (ReferenceEquals(_current, next)) desktop.Shutdown();
        };
        desktop.MainWindow = next;
        next.Show();
        previous?.Close();
    }
}
