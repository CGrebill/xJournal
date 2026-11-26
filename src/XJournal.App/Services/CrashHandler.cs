using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace XJournal.App.Services;

/// <summary>
/// Last line of defence: unexpected errors are logged and explained to the user instead of the app
/// disappearing. The journal itself is safe either way — every change is saved as it's made.
/// </summary>
public static class CrashHandler
{
    private static bool _showing;

    public static void Install()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            Handle(e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write(e.ExceptionObject as Exception, "fatal");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Write(e.Exception, "background task");
            e.SetObserved();
        };
    }

    /// <summary>Logs an unexpected UI error and tells the user (one message at a time).</summary>
    public static void Handle(Exception exception)
    {
        AppLog.Write(exception, "ui");
        if (_showing || ActiveWindow() is not { } window) return;
        _showing = true;
        _ = ShowAsync(window).ContinueWith(_ => _showing = false, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static Window? ActiveWindow() =>
        Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow
            : null;

    private static async Task ShowAsync(Window window)
    {
        var openFolder = new HyperlinkButton { Content = "Open the log folder", Padding = new Avalonia.Thickness(0) };
        openFolder.Click += async (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(AppLog.Folder);
                await window.Launcher.LaunchUriAsync(new Uri(AppLog.Folder + Path.DirectorySeparatorChar));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UriFormatException)
            {
                // Nothing more we can do; the path is shown in the message.
            }
        };
        var content = new StackPanel
        {
            Spacing = 10,
            MaxWidth = 420,
            Children =
            {
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "xJournal ran into an unexpected problem. Your journal is safe: everything you write is saved as you go.\n\n" +
                           "Technical details (never any journal text) were written to a log file in:\n" + AppLog.Folder,
                },
                openFolder,
            },
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        try
        {
            await new WindowDialogs(window).ShowContentAsync("Something went wrong", content, "OK", closeText: null);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "crash dialog");
        }
    }
}
