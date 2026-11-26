using CommunityToolkit.Mvvm.ComponentModel;
using XJournal.App.Controls;
using XJournal.Core;
using XJournal.Core.Models;

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

/// <summary>Charts how answers to each question change over time.</summary>
public sealed partial class TrendsViewModel : ViewModelBase
{
    private readonly Journal _journal;
    private readonly Action<long> _openEntry;

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
    [ObservableProperty] public partial IReadOnlyList<TrendPoint> Points { get; set; } = [];
    [ObservableProperty] public partial DateOnly From { get; set; }
    [ObservableProperty] public partial DateOnly To { get; set; }
    [ObservableProperty] public partial string Stats { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<string> Legend { get; set; } = [];
    [ObservableProperty] public partial string VersionNote { get; set; } = "";

    partial void OnSelectedQuestionChanged(TrendQuestion? value) => Reload();
    partial void OnSelectedRangeChanged(RangeOption value) => Reload();

    private void LoadWriting()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        var s = Core.Insights.WritingStats.Compute(_journal, culture.DateTimeFormat.FirstDayOfWeek);
        static string Days(int n) => n == 1 ? "1 day" : $"{n:N0} days";
        static string Entries(int n) => n == 1 ? "1 entry" : $"{n:N0} entries";
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
                w.WeekStart.ToString("MMM d", culture),
                w.Words,
                $"Week of {w.WeekStart.ToString("MMM d", culture)}: {w.Words:N0} words · {Entries(w.Entries)}"))
            .ToList();
    }

    public void OpenEntry(TrendPoint point)
    {
        if (point.EntryId is { } id) _openEntry(id);
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

        var today = _journal.Clock.Today();
        var history = _journal.Answers.GetHistory(question.Id);
        DateOnly from = SelectedRange.Days is { } days
            ? today.AddDays(-(days - 1))
            : history.Count > 0 ? history[0].Day : today.AddDays(-29);
        if (from >= today) from = today.AddDays(-1);

        var versions = _journal.Questions.GetVersions(question.Id).ToDictionary(v => v.Id);
        var inRange = history.Where(p => p.Day >= from && p.Day <= today).ToList();
        long? previousVersion = history.TakeWhile(p => p.Day < from).LastOrDefault()?.VersionId;
        var points = new List<TrendPoint>();
        foreach (var p in inRange)
        {
            var version = versions[p.VersionId];
            var label = p.Value >= 1 && p.Value <= version.Scale.Count ? version.Scale[p.Value - 1] : "";
            points.Add(new TrendPoint(p.Day, p.Value, label, version.Text,
                Reworded: previousVersion is not null && previousVersion != p.VersionId, p.EntryId));
            previousVersion = p.VersionId;
        }

        From = from;
        To = today;
        Points = points;

        Legend = question.Current.Scale.Select((label, i) => $"{i + 1} = {label}").ToList();
        VersionNote = versions.Count > 1
            ? $"This question has been worded {versions.Count} different ways. Hover a point to see the exact wording that was answered."
            : "";
        Stats = points.Count == 0
            ? "No answers in this period."
            : $"{points.Count} answer{(points.Count == 1 ? "" : "s")} · average {points.Average(p => p.Value):0.0} · latest {points[^1].Value} ({points[^1].Day:MMM d})";
    }
}
