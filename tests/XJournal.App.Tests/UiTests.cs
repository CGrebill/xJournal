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
        vm.Username = "jordan";
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
        Assert.Equal("jordan", again.Username);
        window.DataContext = again;
        Screenshot(window, "03-login-signin");
        window.Close();
    }

    [AvaloniaFact]
    public void Typing_in_the_editor_saves_automatically()
    {
        var journal = _accounts.Create("jordan", Password);
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
        var journal = _accounts.Create("jordan", Password);
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
        var journal = _accounts.Create("jordan", Password);
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
    public void Idle_journal_signs_out_after_the_auto_lock_time()
    {
        var journal = _accounts.Create("jordan", Password);
        Assert.Equal(15, journal.Settings.AutoLockMinutes);
        var (window, vm) = OpenMain(journal);
        string? notice = null;
        vm.SignedOut += n => notice = n;
        vm.Body = "Unsaved when the lock kicks in.";

        var now = DateTimeOffset.Now;
        Assert.False(vm.CheckIdle(now.AddMinutes(14)));
        vm.ReportActivity();
        Assert.False(vm.CheckIdle(DateTimeOffset.Now.AddMinutes(10)));
        Assert.True(vm.CheckIdle(DateTimeOffset.Now.AddMinutes(16)));

        Assert.Contains("15 minutes", notice);
        Assert.True(journal.IsDisposed);
        using (var reopened = _accounts.SignIn("jordan", Password))
        {
            Assert.Equal("Unsaved when the lock kicks in.", reopened.Entries.GetLatest()!.Body);
            reopened.Settings.AutoLockMinutes = 0;
        }

        var again = _accounts.SignIn("jordan", Password);
        var (window2, vm2) = OpenMain(again);
        Assert.False(vm2.CheckIdle(DateTimeOffset.Now.AddDays(1))); // 0 = never
        window2.Close();
        window.Close();
    }

    [AvaloniaFact]
    public void Unexpected_errors_are_logged()
    {
        var previous = AppLog.Folder;
        AppLog.Folder = Path.Combine(_root, "logs");
        try
        {
            CrashHandler.Handle(new InvalidOperationException("Something broke"));
            var log = Assert.Single(Directory.GetFiles(AppLog.Folder, "xjournal-*.log"));
            var text = File.ReadAllText(log);
            Assert.Contains("InvalidOperationException: Something broke", text);
            Assert.Contains("[ui]", text);
        }
        finally
        {
            AppLog.Folder = previous;
        }
    }

    [AvaloniaFact]
    public void Questions_window_saves_answers_and_clears_the_reminder()
    {
        var journal = _accounts.Create("jordan", Password);
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
        var journal = _accounts.Create("jordan", Password);
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
        Assert.True(trendsVm.IsWritingSelected); // "Your writing" opens first
        trendsVm.SelectedQuestion = trendsVm.Questions.Single(q => q.Question?.Id == "mood");
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
        var home = _accounts.Create("jordan", Password);
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
        using (var laptop = new XJournal.Core.Backup.JournalTransfer(laptopAccounts).ImportBackup(start, Password, "jordan", null, false))
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

    private sealed class ShiftedClock(TimeSpan offset) : IClock
    {
        public DateTimeOffset Now => DateTimeOffset.Now + offset;
        public TimeZoneInfo TimeZone => TimeZoneInfo.Local;
    }

    [AvaloniaFact]
    public void Writing_stats_show_streaks_and_weekly_words()
    {
        // Write on several past days (via clocks set back in time), then sign in today.
        var daysAgo = new[] { 1, 2, 3, 9, 10, 17, 24, 25, 26, 27, 40, 60 };
        var random = new Random(7);
        foreach (var ago in daysAgo)
        {
            var store = new AccountStore(_root, new ShiftedClock(TimeSpan.FromDays(-ago)));
            using var j = store.FindAccount("jordan") is null ? store.Create("jordan", Password) : store.SignIn("jordan", Password);
            var session = new XJournal.Core.Sessions.EntrySession(j);
            session.SetText("", string.Join(' ', Enumerable.Repeat("word", 40 + random.Next(260))));
            session.Leave();
        }

        var journal = _accounts.SignIn("jordan", Password);
        var (main, mainVm) = OpenMain(journal);
        Assert.Equal("3-day streak", mainVm.StreakText);
        mainVm.Body = "Writing today extends it.";
        mainVm.SaveNow();
        Assert.Equal("4-day streak", mainVm.StreakText);

        var trends = new TrendsWindow { DataContext = new TrendsViewModel(journal, _ => { }) };
        trends.Show();
        var vm = (TrendsViewModel)trends.DataContext;
        Assert.True(vm.IsWritingSelected);
        Assert.Equal("4 days", vm.WritingTiles[0].Value);   // current streak
        Assert.Equal("4 days", vm.WritingTiles[1].Value);   // longest (27–24 days ago is also 4)
        Assert.Equal("13 entries", vm.WritingTiles[4].Value);
        Assert.Equal(12, vm.WeeklyWords.Count);
        Pump();
        Screenshot(trends, "15-writing-stats");
        Screenshot(main, "19-main-streak");
        trends.Close();
        main.Close();
    }

    [AvaloniaFact]
    public void Calendar_shows_activity_and_opens_entries()
    {
        var journal = _accounts.Create("jordan", Password);
        ImportSampleQuestions(journal);
        journal.Answers.SaveForDay(journal.Clock.Today(), null,
            new Dictionary<long, int> { [journal.Questions.Get("mood")!.Current.Id] = 4 }, journal.Clock.Now);
        var (window, vm) = OpenMain(journal);
        foreach (var (summary, body) in new[] { ("Morning", "Coffee and plans."), ("Evening", "Long walk, good talk.") })
        {
            vm.Summary = summary;
            vm.Body = body;
            vm.NewEntryCommand.Execute(null);
        }

        vm.ToggleCalendarCommand.Execute(null);
        Pump();
        Assert.True(vm.IsCalendarOpen);
        var calendar = vm.Calendar;
        Assert.Equal(42, calendar.Days.Count);
        var today = calendar.Days.Single(d => d.IsToday);
        Assert.Equal(2, today.Entries);
        Assert.True(today.CheckedIn);
        Assert.True(today.IsSelected);
        Assert.Equal(["Morning", "Evening"], calendar.DayEntries.Select(e => e.Summary));
        Assert.StartsWith("Wrote on 1 day", calendar.MonthSummary, StringComparison.Ordinal);
        Screenshot(window, "16-calendar");

        var title = calendar.MonthTitle;
        calendar.PreviousMonthCommand.Execute(null);
        Assert.NotEqual(title, calendar.MonthTitle);
        calendar.ThisMonthCommand.Execute(null);
        Assert.Equal(title, calendar.MonthTitle);

        vm.OpenResult(calendar.DayEntries[0]);
        Assert.True(vm.IsEditorOpen);
        Assert.Equal("Morning", vm.Summary);
        window.Close();
    }

    [AvaloniaFact]
    public void On_this_day_card_appears_opens_and_dismisses()
    {
        var lastYear = DateTimeOffset.Now.AddYears(-1);
        if (lastYear.Day != DateTimeOffset.Now.Day) return; // 29 February: no matching date last year
        var past = new AccountStore(_root, new ShiftedClock(lastYear - DateTimeOffset.Now));
        using (var then = past.Create("jordan", Password))
        {
            var session = new XJournal.Core.Sessions.EntrySession(then);
            session.SetText("Last year's trip", "We drove to the coast.");
            session.Leave();
        }

        var journal = _accounts.SignIn("jordan", Password);
        var (window, vm) = OpenMain(journal); // a year later: the old entry isn't resumed
        Assert.True(vm.ShowMemory);
        Assert.Equal("On this day · 1 year ago", vm.MemoryTitle);
        Assert.Equal("Last year's trip", vm.MemoryText);
        Screenshot(window, "17-on-this-day");

        vm.Body = "Typing hides it";
        Assert.False(vm.ShowMemory);
        vm.Body = "";
        Assert.True(vm.ShowMemory);

        vm.OpenMemoryCommand.Execute(null);
        Assert.Equal("We drove to the coast.", vm.Body);
        Assert.False(vm.ShowMemory); // not on a fresh entry any more

        vm.NewEntryCommand.Execute(null);
        Assert.True(vm.ShowMemory);
        vm.DismissMemoryCommand.Execute(null);
        Assert.False(vm.ShowMemory);
        window.Close();

        var (again, againVm) = OpenMain(_accounts.SignIn("jordan", Password));
        Assert.False(againVm.ShowMemory); // dismissed for the rest of the day
        again.Close();
    }

    [AvaloniaFact]
    public void Markdown_export_options_render()
    {
        var options = new MarkdownExportViewModel { FromDate = DateTime.Today, ToDate = DateTime.Today.AddDays(-1) };
        Assert.False(options.Validate());
        options.ToDate = null;
        Assert.True(options.Validate());
        options.OneFilePerEntry = true;
        Assert.False(options.SingleDocument);
        Assert.True(options.ToOptions().OneFilePerEntry);
        var dialog = Host(new MarkdownExportView { DataContext = options }, "Export entries as Markdown");
        Screenshot(dialog, "18-markdown-export");
        dialog.Close();
    }

    [AvaloniaFact]
    public void Windows_draw_only_caption_buttons_in_the_title_bar()
    {
        // Our windows draw their own title text; a drawn system title on top of it was doubled (issue seen on Windows).
        foreach (var window in new Window[] { new LoginWindow(), new MainWindow(), new SettingsWindow(), new QuestionsWindow(), new TrendsWindow() })
        {
            var parts = Avalonia.Controls.Chrome.WindowDrawnDecorations.GetTitleBarDecorations(window);
            Assert.False(parts.HasFlag(Avalonia.Controls.Chrome.TitleBarDecorations.Title));
            Assert.False(parts.HasFlag(Avalonia.Controls.Chrome.TitleBarDecorations.FullScreenButton));
            Assert.True(parts.HasFlag(Avalonia.Controls.Chrome.TitleBarDecorations.CloseButton));
        }
    }

    [AvaloniaFact]
    public void Charts_survive_being_drawn_tiny()
    {
        var points = new[] { new XJournal.App.Controls.TrendPoint(new DateOnly(2026, 1, 1), 3, "Neutral", "Q", false, null) };
        foreach (var size in new[] { 1.0, 30, 80 })
        {
            var window = new Window
            {
                Width = size, Height = size,
                Content = new StackPanel
                {
                    Children =
                    {
                        new XJournal.App.Controls.TrendChart { Points = points, From = new DateOnly(2026, 1, 1), To = new DateOnly(2026, 2, 1), Width = size, Height = size },
                        new XJournal.App.Controls.BarChart { Bars = [new("Jan 1", 10, "10 words")], Width = size, Height = size },
                    },
                },
            };
            window.Show();
            Pump();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Dark_theme_renders()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XJOURNAL_SCREENSHOTS"))) return;
        var journal = _accounts.Create("jordan", Password);
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
        loginVm.Username = "jordan";
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
