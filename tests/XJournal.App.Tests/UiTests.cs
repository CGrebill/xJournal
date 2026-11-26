using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using XJournal.App.Services;
using XJournal.App.ViewModels;
using XJournal.App.Views;
using XJournal.Core;
using XJournal.Core.Accounts;
using XJournal.Core.Questions;

namespace XJournal.App.Tests;

/// <summary>Drives the real windows headlessly. Set XJOURNAL_SCREENSHOTS to a folder to save renders.</summary>
public sealed class UiTests : IDisposable
{
    private const string Password = "correct horse";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xjournal-ui-tests", Guid.NewGuid().ToString("N"));
    private readonly AccountStore _accounts;

    public UiTests() => _accounts = new AccountStore(_root);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Screenshot(Window window, string name)
    {
        var folder = Environment.GetEnvironmentVariable("XJOURNAL_SCREENSHOTS");
        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder);
        Pump();
        window.CaptureRenderedFrame()?.Save(Path.Combine(folder, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static (MainWindow Window, MainViewModel ViewModel) OpenMain(Journal journal)
    {
        var window = new MainWindow();
        var vm = new MainViewModel(journal, new WindowDialogs(window), window);
        window.DataContext = vm;
        window.Show();
        Pump();
        return (window, vm);
    }

    private static void ImportSampleQuestions(Journal journal)
    {
        using var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://xJournal/Assets/sample-questions.json"));
        var parsed = QuestionFileParser.Parse(new StreamReader(stream).ReadToEnd());
        Assert.True(parsed.IsValid, string.Join("\n", parsed.Errors));
        journal.QuestionImporter.Apply(journal.QuestionImporter.Plan(parsed.Questions));
    }

    [AvaloniaFact]
    public async Task Creating_an_account_signs_in()
    {
        var window = new LoginWindow();
        var vm = new LoginViewModel(_accounts, new WindowDialogs(window));
        window.DataContext = vm;
        window.Show();
        Assert.True(vm.IsCreateMode); // no accounts yet
        Screenshot(window, "01-login-create");

        Journal? signedIn = null;
        vm.SignedIn += j => signedIn = j;
        vm.Username = "caleb";
        vm.Password = "short";
        vm.ConfirmPassword = "short";
        await vm.SubmitCommand.ExecuteAsync(null);
        Assert.NotNull(vm.Error);
        Screenshot(window, "02-login-error");

        vm.Password = vm.ConfirmPassword = Password;
        await vm.SubmitCommand.ExecuteAsync(null);
        Assert.NotNull(signedIn);
        signedIn!.Dispose();

        var again = new LoginViewModel(_accounts, new WindowDialogs(window));
        Assert.False(again.IsCreateMode);
        Assert.Equal("caleb", again.Username);
        window.DataContext = again;
        Screenshot(window, "03-login-signin");
        window.Close();
    }

    [AvaloniaFact]
    public void Typing_in_the_editor_saves_automatically()
    {
        var journal = _accounts.Create("caleb", Password);
        var (window, vm) = OpenMain(journal);
        Assert.False(vm.Session.Current.IsPersisted);
        Screenshot(window, "04-main-empty");

        var editor = window.FindControl<TextBox>("Editor")!;
        editor.Focus();
        window.KeyTextInput("Today I started a journal.");
        Pump();
        Assert.Equal("Today I started a journal.", vm.Body);
        Assert.True(vm.Session.IsDirty);

        // Leaving the window saves immediately (the typing-pause timer does too).
        vm.SaveNow();
        Assert.True(vm.Session.Current.IsPersisted);
        Assert.Equal(1, journal.Entries.Count());

        window.FindControl<TextBox>("SummaryBox")!.Text = "A fresh start";
        editor.Text = "Today I started a journal. It saves itself while I type, and the time of every entry is recorded precisely.\n\nTomorrow I'll set up my check-in questions.";
        vm.SaveNow();
        Screenshot(window, "05-main-writing");

        vm.NewEntryCommand.Execute(null);
        Assert.False(vm.Session.Current.IsPersisted);
        Assert.Equal("", editor.Text);
        Assert.Equal("A fresh start", journal.Entries.GetLatest()!.Summary);
        window.Close();
        Assert.Throws<ObjectDisposedException>(() => journal.Entries.Count()); // closing saves and closes the journal
    }

    [AvaloniaFact]
    public void Search_finds_and_opens_entries()
    {
        var journal = _accounts.Create("caleb", Password);
        var (window, vm) = OpenMain(journal);
        foreach (var (summary, body) in new[]
                 {
                     ("Hike", "Went hiking in the mountains. The view from the ridge was unreal."),
                     ("Work", "Long day of meetings. Need to plan the mountain trip with Sam."),
                     ("", "Quiet evening, read a book and went to bed early."),
                 })
        {
            vm.Summary = summary;
            vm.Body = body;
            vm.NewEntryCommand.Execute(null);
        }

        vm.ToggleSearchCommand.Execute(null);
        Pump();
        Assert.Equal(3, vm.Search.Results.Count);
        vm.Search.Query = "mount";
        vm.Search.Run();
        Pump();
        Assert.Equal(2, vm.Search.Results.Count);
        Screenshot(window, "06-search");

        var hike = vm.Search.Results.Single(r => r.Summary == "Hike");
        vm.Search.OpenCommand.Execute(hike);
        Pump();
        Assert.False(vm.IsSearchOpen);
        Assert.Equal("Hike", vm.Summary);
        window.Close();
    }

    [AvaloniaFact]
    public void A_pending_search_after_signing_out_is_ignored()
    {
        var journal = _accounts.Create("caleb", Password);
        var (window, vm) = OpenMain(journal);
        vm.ToggleSearchCommand.Execute(null);
        vm.Search.Query = "anything"; // schedules a search
        vm.SignOutCommand.Execute(null);
        Assert.True(journal.IsDisposed);
        vm.Search.Run(); // would previously hit the closed database
        vm.Search.Query = "more";
        Pump();
        window.Close();
    }

    [AvaloniaFact]
    public void Questions_window_saves_answers_and_clears_the_reminder()
    {
        var journal = _accounts.Create("caleb", Password);
        ImportSampleQuestions(journal);
        var (main, mainVm) = OpenMain(journal);
        Assert.True(mainVm.ShowReminder);
        Assert.True(mainVm.ShowQuestionBadge);
        Screenshot(main, "07-main-reminder");

        var window = new QuestionsWindow();
        var vm = new QuestionsViewModel(journal, new WindowDialogs(window), () => null);
        vm.AnswersChanged += mainVm.RefreshQuestionState;
        window.DataContext = vm;
        window.Show();
        Pump();
        Assert.Equal(5, vm.Items.Count);

        // Click the "4" choice of the first question.
        var choice = window.GetVisualDescendants().OfType<RadioButton>().ElementAt(3);
        choice.IsChecked = true;
        Pump();
        Assert.Equal(4, vm.Items[0].Value);
        vm.Items[1].Value = 2;
        vm.Items[4].Value = 5;
        Pump();
        Screenshot(window, "08-questions");

        var saved = journal.Answers.GetForDay(journal.Clock.Today())!;
        Assert.Equal(3, saved.Values.Count);
        Assert.Equal(4, saved.Values[vm.Items[0].Version.Id]);
        Assert.False(mainVm.ShowReminder);
        window.Close();
        main.Close();
    }

    [AvaloniaFact]
    public void Trends_and_settings_render()
    {
        var journal = _accounts.Create("caleb", Password);
        ImportSampleQuestions(journal);
        var today = journal.Clock.Today();
        var mood = journal.Questions.Get("mood")!.Current.Id;
        var random = new Random(4);
        for (var i = 40; i >= 1; i--)
        {
            if (i % 6 == 0) continue;
            journal.Answers.SaveForDay(today.AddDays(-i), null,
                new Dictionary<long, int> { [mood] = Math.Clamp(3 + random.Next(-2, 3), 1, 5) }, journal.Clock.Now);
        }
        // Reword the question partway through.
        var reworded = QuestionFileParser.Parse("""{"questions":[{"id":"mood","text":"Overall, I was in a good mood."}]}""");
        journal.QuestionImporter.Apply(journal.QuestionImporter.Plan(reworded.Questions, archiveMissing: false));
        var v2 = journal.Questions.Get("mood")!.Current.Id;
        journal.Answers.SaveForDay(today, null, new Dictionary<long, int> { [v2] = 4 }, journal.Clock.Now);

        var (main, _) = OpenMain(journal);
        var trends = new TrendsWindow
        {
            DataContext = new TrendsViewModel(journal, _ => { }),
        };
        trends.Show();
        var trendsVm = (TrendsViewModel)trends.DataContext;
        trendsVm.SelectedQuestion = trendsVm.Questions.Single(q => q.Question.Id == "mood");
        trendsVm.SelectedRange = trendsVm.Ranges[0];
        Pump();
        Assert.Contains(trendsVm.Points, p => p.Reworded);
        Assert.Equal("Overall, I was in a good mood.", trendsVm.Points[^1].Wording);
        Assert.Equal("My mood was generally good.", trendsVm.Points[0].Wording);
        Screenshot(trends, "09-trends");
        trends.Close();

        var settings = new SettingsWindow { DataContext = new SettingsViewModel(journal, new WindowDialogs(main)) };
        settings.Show();
        Screenshot(settings, "10-settings");
        settings.Close();
        main.Close();
    }

    /// <summary>Shows a dialog's content in a plain window so it can be screenshotted.</summary>
    private static XJournal.App.Controls.JournalWindow Host(Control content, string title)
    {
        var window = new XJournal.App.Controls.JournalWindow { Width = 600, Height = 640 };
        window.Content = new Border
        {
            Classes = { "card" }, Margin = new Avalonia.Thickness(24, 44, 24, 24), Padding = new Avalonia.Thickness(24),
            Child = new StackPanel
            {
                Spacing = 16,
                Children = { new TextBlock { Text = title, Classes = { "subtitle" } }, content },
            },
        };
        window.Show();
        Pump();
        return window;
    }

    [AvaloniaFact]
    public async Task Merging_another_computers_journal_updates_the_open_entry()
    {
        var home = _accounts.Create("caleb", Password);
        var session = new XJournal.Core.Sessions.EntrySession(home);
        session.SetText("Shared", "Written at home.");
        session.Leave();
        var (main, mainVm) = OpenMain(home); // resumes the recent entry
        Assert.Equal("Written at home.", mainVm.Body);
        Assert.False(mainVm.ShowRevisionsCommand.CanExecute(null));

        // The laptop starts from a backup, then writes more.
        var laptopAccounts = new AccountStore(Path.Combine(_root, "laptop"));
        var start = Path.Combine(_root, "start.xjbackup");
        XJournal.Core.Backup.JournalTransfer.ExportEncrypted(home, start);
        using (var laptop = new XJournal.Core.Backup.JournalTransfer(laptopAccounts).ImportBackup(start, Password, "caleb", null, false))
        {
            Thread.Sleep(20);
            var other = new XJournal.Core.Sessions.EntrySession(laptop);
            other.Begin();
            other.SetText("Shared", "Written at home. Finished on the laptop.");
            other.StartNew();
            other.SetText("Train", "Notes from the train.");
            other.Leave();
            XJournal.Core.Backup.JournalTransfer.ExportEncrypted(laptop, Path.Combine(_root, "laptop.xjbackup"));
        }

        var merge = new MergeJournalViewModel(home, Path.Combine(_root, "laptop.xjbackup"));
        Assert.False(await merge.ValidateAsync());
        merge.BackupPassword = "wrong password";
        await merge.UnlockCommand.ExecuteAsync(null);
        Assert.NotNull(merge.Error);
        merge.BackupPassword = Password;
        Assert.True(await merge.ValidateAsync());
        Assert.Contains("1 new entry", merge.PreviewLines);
        Assert.Contains(merge.PreviewLines, l => l.StartsWith("1 entry updated", StringComparison.Ordinal));
        var dialog = Host(new MergeJournalView { DataContext = merge }, "Merge in a journal");
        Screenshot(dialog, "13-merge");
        dialog.Close();

        mainVm.SaveNow();
        merge.Run();
        mainVm.OnJournalMerged();
        Assert.Equal("Written at home. Finished on the laptop.", mainVm.Body);
        Assert.Equal(1, mainVm.RevisionCount);
        Assert.True(mainVm.ShowRevisionsCommand.CanExecute(null));
        Assert.Single(Directory.GetFiles(_accounts.SafetyBackupsDirectory, "*-before-merge-*.xjbackup"));

        var revisions = new RevisionsViewModel(home.Entries.GetRevisions(mainVm.Session.Current.Id));
        Assert.False(revisions.Validate());
        revisions.Selected = revisions.Items[0];
        var revisionsDialog = Host(new RevisionsView { DataContext = revisions }, "Previous versions");
        Screenshot(revisionsDialog, "14-revisions");
        revisionsDialog.Close();

        mainVm.Session.RestoreRevision(revisions.Selected.Revision.Id);
        Assert.Equal("Written at home.", mainVm.Body);
        Assert.Equal(2, mainVm.RevisionCount); // the laptop text is kept too
        main.Close();
    }

    [AvaloniaFact]
    public void Dark_theme_renders()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XJOURNAL_SCREENSHOTS"))) return;
        var journal = _accounts.Create("caleb", Password);
        ImportSampleQuestions(journal);
        AppShell.ApplyTheme("dark");
        try
        {
            var (main, vm) = OpenMain(journal);
            vm.Summary = "Late night thoughts";
            vm.Body = "Writing in dark mode. Everything should stay readable and calm.";
            Screenshot(main, "11-main-dark");
            var window = new QuestionsWindow { DataContext = new QuestionsViewModel(journal, new WindowDialogs(main), () => null) };
            window.Show();
            ((QuestionsViewModel)window.DataContext).Items[0].Value = 3;
            Screenshot(window, "12-questions-dark");
            window.Close();
            main.Close();
        }
        finally
        {
            AppShell.ApplyTheme("system");
        }
    }
}

public sealed class ShellTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xjournal-ui-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [AvaloniaFact]
    public async Task Sign_in_write_sign_out_and_back_in()
    {
        var accounts = new AccountStore(_root);
        var desktop = new Avalonia.Controls.ApplicationLifetimes.ClassicDesktopStyleApplicationLifetime
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };
        var shell = new AppShell(desktop, accounts);

        shell.ShowLogin();
        var login = Assert.IsType<LoginWindow>(desktop.MainWindow);
        var loginVm = (LoginViewModel)login.DataContext!;
        loginVm.Username = "caleb";
        loginVm.Password = loginVm.ConfirmPassword = "correct horse";
        await loginVm.SubmitCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        var main = Assert.IsType<MainWindow>(desktop.MainWindow);
        Assert.False(login.IsVisible);
        var mainVm = (MainViewModel)main.DataContext!;
        mainVm.Body = "Written before signing out.";
        mainVm.SignOutCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var loginAgain = Assert.IsType<LoginWindow>(desktop.MainWindow);
        Assert.False(main.IsVisible);
        var againVm = (LoginViewModel)loginAgain.DataContext!;
        Assert.False(againVm.IsCreateMode);
        againVm.Password = "correct horse";
        await againVm.SubmitCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        // The unsaved text was saved on sign-out, and is resumed because it's recent.
        var resumed = (MainViewModel)Assert.IsType<MainWindow>(desktop.MainWindow).DataContext!;
        Assert.Equal("Written before signing out.", resumed.Body);
        resumed.Close();
    }
}
