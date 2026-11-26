using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using XJournal.App.Services;
using XJournal.App.ViewModels;
using XJournal.App.Views;
using XJournal.Core;
using XJournal.Core.Accounts;
using XJournal.Core.Questions;
using XJournal.Core.Sessions;

namespace XJournal.App.Tests;

/// <summary>
/// Renders the screenshots used in the README from a realistic demo journal.
/// Runs only when XJOURNAL_DOCS_SCREENSHOTS is set to an output folder, e.g.
/// <c>XJOURNAL_DOCS_SCREENSHOTS=docs/screenshots dotnet test --filter DocsScreenshots</c>.
/// </summary>
public sealed class DocsScreenshots : IDisposable
{
    private const string Password = "demo password";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xjournal-docs", Guid.NewGuid().ToString("N"));
    private readonly string? _output = Environment.GetEnvironmentVariable("XJOURNAL_DOCS_SCREENSHOTS");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private sealed class ShiftedClock(TimeSpan offset) : IClock
    {
        public DateTimeOffset Now => DateTimeOffset.Now + offset;
        public TimeZoneInfo TimeZone => TimeZoneInfo.Local;
    }

    /// <summary>The demo's "now": today at 7:42 PM, whatever time the screenshots are rendered.</summary>
    private static TimeSpan ToDemoTime(int daysAgo, int hour = 19, int minute = 42) =>
        new DateTimeOffset(DateTime.Today.AddDays(-daysAgo).AddHours(hour).AddMinutes(minute)) - DateTimeOffset.Now;

    private static readonly string[] Entries =
    [
        "Long walk by the river after work. The light on the water was unreal, and for once I didn't check my phone the whole time.",
        "Tough day. The presentation went fine in the end, but I was wound up all morning. Note to self: prepare the night before.",
        "Cooked dinner for friends. Too much pasta, not enough chairs, lots of laughing.",
        "Slept badly. Felt foggy until lunch, then a second coffee and things picked up.",
        "Started reading a new book on habits. The idea of making good things obvious and easy is stuck in my head.",
        "Rainy Sunday. Cleaned the apartment, called Mum, and finally fixed the wobbly shelf.",
        "Went for a run before work. Short and slow, but it set the tone for the whole day.",
        "Quiet evening. Made tea, wrote a little, went to bed early.",
    ];

    private void BuildDemoJournal()
    {
        // Questions, imported "two months ago".
        var questionsJson = """
            {"questions":[
              {"id":"energy","text":"I had plenty of energy today."},
              {"id":"mood","text":"My mood was generally good."},
              {"id":"focus","text":"I could focus when I needed to."},
              {"id":"sleep","text":"I slept well last night."}
            ]}
            """;
        var random = new Random(11);
        var writtenDaysAgo = new HashSet<int> { 1, 2, 3, 4, 6, 8, 9, 10, 13, 15, 16, 20, 22, 23, 27, 29, 30, 34, 36, 41, 45, 50 };
        var mood = 3.0;
        for (var ago = 55; ago >= 1; ago--)
        {
            var store = new AccountStore(_root, new ShiftedClock(ToDemoTime(ago, hour: 8 + ago % 12)));
            using var journal = store.FindAccount("alex") is null ? store.Create("alex", Password) : store.SignIn("alex", Password);
            if (ago == 55)
                journal.QuestionImporter.Apply(journal.QuestionImporter.Plan(QuestionFileParser.Parse(questionsJson).Questions));
            if (ago == 20) // reword a question partway through
            {
                var reworded = questionsJson.Replace("My mood was generally good.", "Overall, I was in a good mood.");
                journal.QuestionImporter.Apply(journal.QuestionImporter.Plan(QuestionFileParser.Parse(reworded).Questions));
            }

            long? entryId = null;
            if (writtenDaysAgo.Contains(ago))
            {
                var session = new EntrySession(journal);
                var text = Entries[ago % Entries.Length];
                session.SetText(ago % 3 == 0 ? text.Split('.')[0] : "", text);
                session.Leave();
                entryId = session.Current.Id;
            }

            if (ago % 7 == 5) continue; // a few missed check-ins
            mood = Math.Clamp(mood + random.Next(-1, 2) * 0.8, 1, 5);
            var values = journal.Questions.GetAll().ToDictionary(
                q => q.Current.Id,
                q => q.Id == "mood" ? (int)Math.Round(mood) : Math.Clamp((int)Math.Round(mood) + random.Next(-1, 2), 1, 5));
            journal.Answers.SaveForDay(journal.Clock.Today(), entryId, values, journal.Clock.Now);
        }
    }

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private void Save(Window window, string name)
    {
        Pump();
        Directory.CreateDirectory(_output!);
        window.CaptureRenderedFrame()?.Save(Path.Combine(_output!, name + ".png"),
            new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    [AvaloniaFact]
    public void Render_readme_screenshots()
    {
        if (string.IsNullOrEmpty(_output)) return;
        BuildDemoJournal();
        var accounts = new AccountStore(_root, new ShiftedClock(ToDemoTime(0)));

        // Sign-in screen.
        var login = new LoginWindow();
        login.DataContext = new LoginViewModel(accounts, new WindowDialogs(login));
        login.Show();
        Save(login, "login");
        login.Close();

        foreach (var theme in new[] { "light", "dark" })
        {
            AppShell.ApplyTheme(theme);
            var journal = accounts.SignIn("alex", Password);
            var main = new MainWindow();
            var vm = new MainViewModel(journal, new WindowDialogs(main), main);
            main.DataContext = vm;
            main.Show();
            vm.Summary = "A slow, good day";
            vm.Body = "Slept in, then a long breakfast on the balcony. Spent the afternoon at the market and picked up " +
                      "far too many tomatoes.\n\nI've noticed I feel calmer on the days I walk somewhere instead of driving. " +
                      "Trying to keep that going next week.";
            vm.SaveNow();
            vm.DismissReminderCommand.Execute(null);
            Save(main, theme == "light" ? "main" : "main-dark");
            if (theme == "dark")
            {
                main.Close();
                break;
            }

            vm.ToggleSearchCommand.Execute(null);
            vm.Search.Query = "walk";
            vm.Search.Run();
            Save(main, "search");

            vm.ToggleCalendarCommand.Execute(null);
            Save(main, "calendar");
            vm.ActivePane = MainPane.Editor;

            var checkIn = new QuestionsWindow();
            var questions = new QuestionsViewModel(journal, new WindowDialogs(checkIn), () => vm.Session.Current.Id);
            checkIn.DataContext = questions;
            checkIn.Show();
            questions.Items[0].Value = 4;
            questions.Items[1].Value = 4;
            questions.Items[2].Value = 3;
            Save(checkIn, "check-in");
            checkIn.Close();

            var trends = new TrendsWindow();
            var trendsVm = new TrendsViewModel(journal, _ => { });
            trends.DataContext = trendsVm;
            trends.Show();
            Save(trends, "writing-stats");
            trendsVm.SelectedRange = trendsVm.Ranges[3];
            trendsVm.SelectedQuestion = trendsVm.Questions.Single(q => q.Question?.Id == "mood");
            Save(trends, "trends");
            trends.Close();

            var settings = new SettingsWindow { DataContext = new SettingsViewModel(journal, new WindowDialogs(main)) };
            settings.Show();
            Save(settings, "settings");
            settings.Close();
            main.Close();
        }
        AppShell.ApplyTheme("system");
    }
}
