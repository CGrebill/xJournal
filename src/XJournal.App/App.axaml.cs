using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using XJournal.App.Services;
using XJournal.Core.Accounts;

namespace XJournal.App;

public partial class App : Application
{
    public AppShell? Shell { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            CrashHandler.Install();
            Shell = new AppShell(desktop, new AccountStore());
            Shell.ShowLogin();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
