using System.Globalization;
using XJournal.Core.Export;
using XJournal.Core.Insights;
using XJournal.Core.Models;
using XJournal.Core.Questions;
using XJournal.Core.Sessions;

namespace XJournal.Core.Tests;

public class InsightTests
{
    private static JournalEntry WriteAt(TestEnv env, Journal journal, DateTimeOffset at, string body, string summary = "")
    {
        env.Clock.Now = at;
        var session = new EntrySession(journal);
        session.SetText(summary, body);
        session.Leave();
        return session.Current;
    }

    private static DateTimeOffset Local(int y, int m, int d, int h = 12, int min = 0) =>
        new(y, m, d, h, min, 0, TimeSpan.FromHours(-5));

    [Theory]
    [InlineData("", 0)]
    [InlineData("one", 1)]
    [InlineData("  two  words\n\nthree\tfour ", 4)]
    public void Words_are_counted(string text, int words) => Assert.Equal(words, WordCounter.Count(text));

    [Fact]
    public void Day_activity_uses_local_days()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        WriteAt(env, journal, Local(2026, 11, 1, 23, 30), "late night words"); // 04:30 UTC on the 2nd
        WriteAt(env, journal, Local(2026, 11, 2, 0, 30), "just after midnight");
        WriteAt(env, journal, Local(2026, 11, 2, 9), "morning");

        var days = journal.Entries.GetDayActivity(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30));
        Assert.Equal(new DayActivity(new DateOnly(2026, 11, 1), 1, 3), days[new DateOnly(2026, 11, 1)]);
        Assert.Equal(new DayActivity(new DateOnly(2026, 11, 2), 2, 4), days[new DateOnly(2026, 11, 2)]);
        Assert.Equal(2, journal.Entries.GetForDay(new DateOnly(2026, 11, 2)).Count);
        Assert.Empty(journal.Entries.GetDayActivity(new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 30)));
    }

    [Fact]
    public void On_this_day_finds_last_month_and_previous_years()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        WriteAt(env, journal, Local(2023, 11, 25), "three years ago");
        WriteAt(env, journal, Local(2025, 11, 25), "one year ago");
        WriteAt(env, journal, Local(2025, 11, 24), "wrong day");
        WriteAt(env, journal, Local(2026, 10, 25), "last month");
        WriteAt(env, journal, Local(2026, 11, 25, 8), "today, not a memory");

        var memories = OnThisDay.Find(journal, new DateOnly(2026, 11, 25));
        Assert.Equal(["last month", "one year ago", "three years ago"], memories.Select(m => m.Entry.Body));
        Assert.Equal(["1 month ago", "1 year ago", "3 years ago"], memories.Select(m => m.Ago));
    }

    [Fact]
    public void On_this_day_skips_dates_that_dont_exist()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        WriteAt(env, journal, Local(2026, 2, 28), "end of February");
        WriteAt(env, journal, Local(2024, 2, 29), "leap day");

        Assert.Empty(OnThisDay.Find(journal, new DateOnly(2026, 3, 31)));  // no 31 Feb
        Assert.Empty(OnThisDay.Find(journal, new DateOnly(2025, 2, 28)));  // 29 Feb isn't moved to the 28th
        Assert.Equal("leap day", Assert.Single(OnThisDay.Find(journal, new DateOnly(2028, 2, 29))).Entry.Body);
    }

    private static Dictionary<DateOnly, DayActivity> Days(params (int Day, int Words)[] days) =>
        days.ToDictionary(d => new DateOnly(2026, 11, d.Day), d => new DayActivity(new DateOnly(2026, 11, d.Day), 1, d.Words));

    [Fact]
    public void Streaks_count_consecutive_days_ending_today_or_yesterday()
    {
        var today = new DateOnly(2026, 11, 25);
        var days = Days((10, 5), (11, 5), (12, 5), (13, 5), (23, 5), (24, 5));
        var stats = WritingStats.Compute(days, today, DayOfWeek.Monday);
        Assert.Equal(2, stats.CurrentStreak);  // 23–24; today not written yet still counts
        Assert.Equal(4, stats.LongestStreak);  // 10–13

        days[today] = new DayActivity(today, 1, 5);
        Assert.Equal(3, WritingStats.Compute(days, today, DayOfWeek.Monday).CurrentStreak);
        Assert.Equal(0, WritingStats.Compute(Days((20, 5)), today, DayOfWeek.Monday).CurrentStreak);
        Assert.Equal(0, WritingStats.Compute(Days(), today, DayOfWeek.Monday).LongestStreak);
    }

    [Fact]
    public void Weekly_and_monthly_totals_respect_the_first_day_of_the_week()
    {
        var today = new DateOnly(2026, 11, 25); // a Wednesday
        var days = Days((1, 100), (22, 10), (23, 20), (25, 30));
        var monday = WritingStats.Compute(days, today, DayOfWeek.Monday, weeks: 4);
        Assert.Equal(50, monday.WordsThisWeek);   // Mon 23 – Wed 25
        Assert.Equal(160, monday.WordsThisMonth);
        Assert.Equal(new DateOnly(2026, 11, 23), monday.RecentWeeks[^1].WeekStart);
        Assert.Equal(4, monday.RecentWeeks.Count);
        Assert.Equal(40, monday.AverageWords);

        var sunday = WritingStats.Compute(days, today, DayOfWeek.Sunday, weeks: 4);
        Assert.Equal(60, sunday.WordsThisWeek);   // Sun 22 – Wed 25
        Assert.Equal(new DateOnly(2026, 11, 22), sunday.RecentWeeks[^1].WeekStart);
    }

    [Fact]
    public void Markdown_single_document()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        var parsed = QuestionFileParser.Parse("""{"questions":[{"id":"mood","text":"Good | mood"}]}""");
        journal.QuestionImporter.Apply(journal.QuestionImporter.Plan(parsed.Questions));
        var entry = WriteAt(env, journal, Local(2026, 11, 24, 14, 31), "Line one\nline two\n\nNew paragraph", "A title");
        journal.Answers.SaveForDay(new DateOnly(2026, 11, 24), entry.Id,
            new Dictionary<long, int> { [journal.Questions.Get("mood")!.Current.Id] = 4 }, env.Clock.Now);
        WriteAt(env, journal, Local(2026, 11, 25, 9, 5), "Second entry");

        var path = env.TempFile("out/journal.md");
        var count = MarkdownExporter.Export(journal, path, new MarkdownOptions(Culture: CultureInfo.InvariantCulture));

        Assert.Equal(2, count);
        var text = File.ReadAllText(path);
        Assert.StartsWith("# caleb's journal", text);
        Assert.Contains("_Exported from xJournal on", text);
        Assert.Contains("""
            ## Tuesday, November 24, 2026 · 14:31

            *A title*

            Line one  
            line two

            New paragraph

            **Check-in**

            | Question | Answer |
            | --- | --- |
            | Good \| mood | 4 – Somewhat agree |
            """.ReplaceLineEndings(), text.ReplaceLineEndings());
        Assert.True(text.IndexOf("November 24", StringComparison.Ordinal) < text.IndexOf("November 25", StringComparison.Ordinal));
    }

    [Fact]
    public void Markdown_one_file_per_entry_with_date_range()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        WriteAt(env, journal, Local(2026, 10, 1), "Too early");
        WriteAt(env, journal, Local(2026, 11, 24, 14, 31), "Body", "Café trip: day 1!");
        WriteAt(env, journal, Local(2026, 11, 24, 14, 31), "Same minute", "Café trip: day 1!");
        WriteAt(env, journal, Local(2026, 11, 25, 9, 5), "   ");
        WriteAt(env, journal, Local(2026, 11, 25, 9, 6), "No summary here");

        var folder = env.TempFile("md");
        var count = MarkdownExporter.Export(journal, folder,
            new MarkdownOptions(new DateOnly(2026, 11, 1), null, OneFilePerEntry: true, Culture: CultureInfo.InvariantCulture));

        Assert.Equal(3, count);
        var files = Directory.GetFiles(folder).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(
            ["2026-11-24_1431_cafe-trip-day-1-2.md", "2026-11-24_1431_cafe-trip-day-1.md", "2026-11-25_0906_no-summary-here.md"],
            files);
        var first = File.ReadAllText(Path.Combine(folder, "2026-11-24_1431_cafe-trip-day-1.md"));
        Assert.StartsWith("---", first);
        Assert.Contains("created: 2026-11-24T14:31:00.000-05:00", first);
        Assert.Contains("summary: \"Café trip: day 1!\"", first);
        Assert.Contains("# Tuesday, November 24, 2026 · 14:31", first);
    }
}
