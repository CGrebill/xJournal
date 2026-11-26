using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XJournal.App.Controls;
using XJournal.Core;
using XJournal.Core.Models;
using XJournal.Core.Questions;

namespace XJournal.App.ViewModels;

/// <summary>An item in the Trends list: "Your writing" (no question) or one check-in question.</summary>
public sealed record TrendQuestion(Question? Question)
{
    public string Text => Question?.Current.Text ?? "Your writing";
    public bool IsArchived => Question?.Archived == true;
    public bool IsWriting => Question is null;
}

public sealed record StatTile(string Label, string Value, string Detail);

public sealed record RangeOption(string Label, int? Days)
{
    public override string ToString() => Label;
}

/// <summary>A past answer to a text question.</summary>
public sealed record TextAnswer(string DateText, string Text, long? EntryId)
{
    public bool HasEntry => EntryId is not null;
}

/// <summary>How a question's answers are shown: a line over time, bars, or a list.</summary>
public enum TrendView
{
    Line,
    Bars,
    List,
}

/// <summary>Charts how answers to each question change over time.</summary>
public sealed partial class TrendsViewModel : ViewModelBase
{
    private readonly Journal _journal;
    private readonly Action<long> _openEntry;
    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    public TrendsViewModel(Journal journal, Action<long> openEntry)
    {
        _journal = journal;
        _openEntry = openEntry;
        Questions = [new TrendQuestion(null), .. journal.Questions.GetAll(includeArchived: true).Select(q => new TrendQuestion(q))];
        SelectedRange = Ranges[1];
        SelectedQuestion = Questions[0];
    }

    /// <summary>"Your writing" first, then every question (archived ones included).</summary>
    public IReadOnlyList<TrendQuestion> Questions { get; }
    public bool HasQuestions => Questions.Count > 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQuestionSelected))]
    public partial bool IsWritingSelected { get; set; }

    public bool IsQuestionSelected => !IsWritingSelected;

    [ObservableProperty] public partial IReadOnlyList<StatTile> WritingTiles { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<BarPoint> WeeklyWords { get; set; } = [];

    public IReadOnlyList<RangeOption> Ranges { get; } =
    [
        new("Past 30 days", 30),
        new("Past 90 days", 90),
        new("Past year", 365),
        new("All time", null),
    ];

    [ObservableProperty] public partial TrendQuestion? SelectedQuestion { get; set; }
    [ObservableProperty] public partial RangeOption SelectedRange { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLine), nameof(IsBars), nameof(IsList))]
    public partial TrendView View { get; set; }

    public bool IsLine => View == TrendView.Line;
    public bool IsBars => View == TrendView.Bars;
    public bool IsList => View == TrendView.List;

    // Line
    [ObservableProperty] public partial IReadOnlyList<TrendPoint> Points { get; set; } = [];
    [ObservableProperty] public partial DateOnly From { get; set; }
    [ObservableProperty] public partial DateOnly To { get; set; }
    [ObservableProperty] public partial double ChartMinimum { get; set; } = 1;
    [ObservableProperty] public partial double ChartMaximum { get; set; } = 5;

    // Bars
    [ObservableProperty] public partial IReadOnlyList<BarPoint> AnswerBars { get; set; } = [];
    [ObservableProperty] public partial double BarsMaximum { get; set; }
    [ObservableProperty] public partial string BarsFormat { get; set; } = "N0";
    [ObservableProperty] public partial string BarsEmptyText { get; set; } = "";

    // List
    [ObservableProperty] public partial IReadOnlyList<TextAnswer> TextAnswers { get; set; } = [];

    [ObservableProperty] public partial string Stats { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<string> Legend { get; set; } = [];
    [ObservableProperty] public partial string VersionNote { get; set; } = "";

    partial void OnSelectedQuestionChanged(TrendQuestion? value) => Reload();
    partial void OnSelectedRangeChanged(RangeOption value) => Reload();

    private void LoadWriting()
    {
        var s = Core.Insights.WritingStats.Compute(_journal, Culture.DateTimeFormat.FirstDayOfWeek);
        WritingTiles =
        [
            new("Current streak", Days(s.CurrentStreak), "Days in a row with an entry"),
            new("Longest streak", Days(s.LongestStreak), "Your best run so far"),
            new("This week", Entries(s.EntriesThisWeek), $"{s.WordsThisWeek:N0} words"),
            new("This month", Entries(s.EntriesThisMonth), $"{s.WordsThisMonth:N0} words"),
            new("All time", Entries(s.TotalEntries), $"{s.TotalWords:N0} words"),
            new("Average entry", $"{s.AverageWords:N0} words", "Per entry"),
        ];
        WeeklyWords = s.RecentWeeks
            .Select(w => new BarPoint(
                w.WeekStart.ToString("MMM d", Culture),
                w.Words,
                $"Week of {w.WeekStart.ToString("MMM d", Culture)}: {w.Words:N0} words · {Entries(w.Entries)}"))
            .ToList();
    }

    private static string Days(int n) => n == 1 ? "1 day" : $"{n:N0} days";
    private static string Entries(int n) => n == 1 ? "1 entry" : $"{n:N0} entries";
    private static string Answers(int n) => n == 1 ? "1 answer" : $"{n:N0} answers";

    public void OpenEntry(TrendPoint point)
    {
        if (point.EntryId is { } id) _openEntry(id);
    }

    [RelayCommand]
    private void OpenTextAnswer(TextAnswer? answer)
    {
        if (answer?.EntryId is { } id) _openEntry(id);
    }

    private void Reload()
    {
        IsWritingSelected = SelectedQuestion?.IsWriting == true;
        if (IsWritingSelected)
        {
            LoadWriting();
            return;
        }
        if (SelectedQuestion?.Question is not { } question || SelectedRange is null)
        {
            Points = [];
            return;
        }

        var current = question.Current;
        var today = _journal.Clock.Today();
        var versions = _journal.Questions.GetVersions(question.Id).ToDictionary(v => v.Id);
        // Answers from before a question changed type (possible after merging) can't share a chart: leave them out.
        var all = _journal.Answers.GetHistory(question.Id);
        var history = all.Where(p => versions[p.VersionId].Type == current.Type).ToList();
        var otherType = all.Count - history.Count;

        DateOnly from = SelectedRange.Days is { } days
            ? today.AddDays(-(days - 1))
            : history.Count > 0 ? history[0].Day : today.AddDays(-29);
        if (from >= today) from = today.AddDays(-1);
        From = from;
        To = today;

        var inRange = history.Where(p => p.Day >= from && p.Day <= today).ToList();
        Legend = [];
        switch (current.Type)
        {
            case QuestionType.YesNo:
                ShowYesNo(inRange, history, from, today);
                break;
            case QuestionType.Choice:
                ShowChoice(current, inRange, versions);
                break;
            case QuestionType.Text:
                ShowText(inRange, versions);
                break;
            default:
                ShowLine(current, inRange, history.TakeWhile(p => p.Day < from).LastOrDefault()?.VersionId, versions);
                break;
        }

        var notes = new List<string>();
        if (versions.Count > 1)
            notes.Add($"This question has been worded {versions.Count} different ways. Hover a point to see the exact wording that was answered.");
        if (otherType > 0)
            notes.Add($"{Answers(otherType)} from when this question was a different type aren't shown.");
        VersionNote = string.Join(" ", notes);
    }

    private void ShowLine(QuestionVersion current, List<AnswerPoint> inRange, long? previousVersion, Dictionary<long, QuestionVersion> versions)
    {
        View = TrendView.Line;
        var points = new List<TrendPoint>();
        foreach (var p in inRange)
        {
            if (p.Value.Number is not { } n) continue;
            var version = versions[p.VersionId];
            points.Add(new TrendPoint(p.Day, n, AnswerRules.Describe(version, p.Value, Culture), version.Text,
                Reworded: previousVersion is not null && previousVersion != p.VersionId, p.EntryId));
            previousVersion = p.VersionId;
        }

        double min, max;
        switch (current.Type)
        {
            case QuestionType.Rating:
                (min, max) = (1, Math.Max(current.Labels.Count, points.Count == 0 ? 0 : points.Max(p => p.Value)));
                Legend = current.Labels.Select((label, i) => $"{i + 1} = {label}").ToList();
                break;
            case QuestionType.Scale:
                min = Math.Min(current.Config.Min ?? 1, points.Count == 0 ? double.MaxValue : points.Min(p => p.Value));
                max = Math.Max(current.Config.Max ?? 10, points.Count == 0 ? double.MinValue : points.Max(p => p.Value));
                Legend = new[]
                    {
                        current.Config.Low is { } low ? $"{current.Config.Min ?? 1} = {low}" : null,
                        current.Config.High is { } high ? $"{current.Config.Max ?? 10} = {high}" : null,
                    }
                    .OfType<string>().ToList();
                break;
            default: // number: fit the axis to the answers
                min = points.Count == 0 ? 0 : Math.Floor(points.Min(p => p.Value));
                max = points.Count == 0 ? 10 : Math.Ceiling(points.Max(p => p.Value));
                if (min > 0 && min <= max / 2) min = 0; // start at zero unless that squashes the line
                if (max <= min) max = min + 1;
                if (!string.IsNullOrEmpty(current.Config.Unit)) Legend = [$"In {current.Config.Unit}"];
                break;
        }
        ChartMinimum = min;
        ChartMaximum = max;
        Points = points;

        if (points.Count == 0)
        {
            Stats = "No answers in this period.";
            return;
        }
        var latest = inRange.Last(p => p.Value.Number is not null);
        var unit = current.Type == QuestionType.Number && current.Config.Unit is { } u ? " " + u : "";
        Stats = $"{Answers(points.Count)} · average {points.Average(p => p.Value).ToString("#,0.#", Culture)}{unit} · " +
                $"latest {AnswerRules.Format(versions[latest.VersionId], latest.Value, Culture)} ({latest.Day.ToString("MMM d", Culture)})";
    }

    private void ShowYesNo(List<AnswerPoint> inRange, List<AnswerPoint> history, DateOnly from, DateOnly today)
    {
        View = TrendView.Bars;
        BarsMaximum = 100;
        BarsFormat = "0'%'";
        BarsEmptyText = inRange.Count == 0 ? "No answers in this period" : "No \"yes\" answers in this period";

        // Weekly bars for short ranges, monthly for long ones.
        var monthly = today.DayNumber - from.DayNumber > 120;
        var first = monthly ? new DateOnly(from.Year, from.Month, 1) : WeekStart(from);
        var bars = new List<BarPoint>();
        for (var start = first; start <= today; start = monthly ? start.AddMonths(1) : start.AddDays(7))
        {
            var end = monthly ? start.AddMonths(1) : start.AddDays(7);
            var period = inRange.Where(p => p.Day >= start && p.Day < end).ToList();
            var yes = period.Count(p => p.Value.Bool == true);
            var name = monthly ? start.ToString("MMMM yyyy", Culture) : $"Week of {start.ToString("MMM d", Culture)}";
            bars.Add(new BarPoint(
                start.ToString(monthly ? "MMM" : "MMM d", Culture),
                period.Count == 0 ? 0 : 100.0 * yes / period.Count,
                period.Count == 0 ? $"{name}: no answers" : $"{name}: yes on {yes} of {Days(period.Count)}"));
        }
        AnswerBars = bars;

        var yesCount = inRange.Count(p => p.Value.Bool == true);
        // Current streak: consecutive days up to today (or yesterday, if today isn't answered yet) answered "yes".
        var byDay = history.ToDictionary(p => p.Day, p => p.Value.Bool == true);
        var day = byDay.ContainsKey(today) ? today : today.AddDays(-1);
        var streak = 0;
        while (byDay.TryGetValue(day, out var yes) && yes)
        {
            streak++;
            day = day.AddDays(-1);
        }

        Stats = inRange.Count == 0
            ? "No answers in this period."
            : $"Yes on {yesCount} of {Days(inRange.Count)} ({100.0 * yesCount / inRange.Count:0}%)" +
              (streak > 0 ? $" · current streak: {Days(streak)} of yes" : "");
        Legend = [monthly ? "Bars show the share of \"yes\" answers each month." : "Bars show the share of \"yes\" answers each week."];
    }

    private static DateOnly WeekStart(DateOnly day)
    {
        var diff = ((int)day.DayOfWeek - (int)Culture.DateTimeFormat.FirstDayOfWeek + 7) % 7;
        return day.AddDays(-diff);
    }

    private void ShowChoice(QuestionVersion current, List<AnswerPoint> inRange, Dictionary<long, QuestionVersion> versions)
    {
        View = TrendView.Bars;
        BarsMaximum = 0;
        BarsFormat = "N0";
        BarsEmptyText = "No answers in this period";

        // Current options first, then any that only older wordings had.
        var options = current.Options
            .Concat(versions.Values.OrderBy(v => v.Id).SelectMany(v => v.Options))
            .Distinct()
            .ToList();
        var counts = options.ToDictionary(o => o, _ => 0);
        foreach (var p in inRange)
            foreach (var choice in p.Value.Choices)
                counts[choice] = counts.GetValueOrDefault(choice) + 1;

        AnswerBars = counts
            .Where(kv => current.Options.Contains(kv.Key) || kv.Value > 0)
            .Select(kv => new BarPoint(kv.Key, kv.Value, $"{kv.Key}: picked on {Days(kv.Value)} of {Days(inRange.Count)}"))
            .ToList();
        Stats = inRange.Count == 0 ? "No answers in this period." : $"{Answers(inRange.Count)} in this period";
        Legend = [current.Config.AllowsMultiple ? "How often each option was picked (several can be picked per day)." : "How often each option was picked."];
    }

    private void ShowText(List<AnswerPoint> inRange, Dictionary<long, QuestionVersion> versions)
    {
        View = TrendView.List;
        TextAnswers = inRange
            .OrderByDescending(p => p.Day)
            .Select(p => new TextAnswer(p.Day.ToString("ddd, MMM d, yyyy", Culture), AnswerRules.Format(versions[p.VersionId], p.Value, Culture), p.EntryId))
            .ToList();
        Stats = inRange.Count == 0 ? "No answers in this period." : $"{Answers(inRange.Count)} in this period, newest first";
        if (TextAnswers.Any(a => a.HasEntry)) Legend = ["Double-click an answer to open the entry from that day."];
    }
}
