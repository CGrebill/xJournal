using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XJournal.Core;
using XJournal.Core.Search;

namespace XJournal.App.ViewModels;

public sealed partial class CalendarDay(DateOnly date, bool inMonth, bool isToday, int entries, int words, bool checkedIn)
    : ViewModelBase
{
    public DateOnly Date { get; } = date;
    public string Number { get; } = date.Day.ToString(CultureInfo.CurrentCulture);
    public bool IsInMonth { get; } = inMonth;
    public bool IsToday { get; } = isToday;
    public int Entries { get; } = entries;
    public bool HasEntries => Entries > 0;
    public bool CheckedIn { get; } = checkedIn;

    /// <summary>The activity dot grows a little with the number of entries.</summary>
    public double DotSize => Entries switch { 0 => 0, 1 => 6, 2 => 8, _ => 10 };

    public string Tooltip
    {
        get
        {
            var parts = new List<string> { Date.ToString("D", CultureInfo.CurrentCulture) };
            if (Entries > 0) parts.Add($"{Entries} entr{(Entries == 1 ? "y" : "ies")} · {words:N0} words");
            if (CheckedIn) parts.Add("Checked in");
            return string.Join("\n", parts);
        }
    }

    [ObservableProperty] public partial bool IsSelected { get; set; }
}

/// <summary>A month at a glance: which days have entries and check-ins, and what was written on each.</summary>
public sealed partial class CalendarViewModel : ViewModelBase
{
    private readonly Journal _journal;
    private readonly Action<long> _open;
    private readonly Func<long> _currentEntryId;
    private DateOnly _selected;

    public CalendarViewModel(Journal journal, Action<long> open, Func<long> currentEntryId)
    {
        _journal = journal;
        _open = open;
        _currentEntryId = currentEntryId;
        var culture = CultureInfo.CurrentCulture;
        var first = (int)culture.DateTimeFormat.FirstDayOfWeek;
        Weekdays = Enumerable.Range(0, 7).Select(i => culture.DateTimeFormat.AbbreviatedDayNames[(first + i) % 7]).ToList();
        _selected = journal.Clock.Today();
        Month = new DateOnly(_selected.Year, _selected.Month, 1);
    }

    public IReadOnlyList<string> Weekdays { get; }
    public ObservableCollection<CalendarDay> Days { get; } = [];
    public ObservableCollection<SearchResultItem> DayEntries { get; } = [];

    [ObservableProperty] public partial DateOnly Month { get; set; }
    [ObservableProperty] public partial string MonthTitle { get; set; } = "";
    [ObservableProperty] public partial string MonthSummary { get; set; } = "";
    [ObservableProperty] public partial string SelectedTitle { get; set; } = "";
    [ObservableProperty] public partial bool SelectedIsEmpty { get; set; }

    /// <summary>Reloads the shown month (call when the pane opens).</summary>
    public void Load()
    {
        var culture = CultureInfo.CurrentCulture;
        MonthTitle = Month.ToString("MMMM yyyy", culture);
        var firstDayOfWeek = culture.DateTimeFormat.FirstDayOfWeek;
        var start = Month.AddDays(-(((int)Month.DayOfWeek - (int)firstDayOfWeek + 7) % 7));
        var end = start.AddDays(41); // six full weeks
        var activity = _journal.Entries.GetDayActivity(start, end);
        var answered = _journal.Answers.GetAnsweredDays(start, end);
        var today = _journal.Clock.Today();

        Days.Clear();
        for (var day = start; day <= end; day = day.AddDays(1))
        {
            activity.TryGetValue(day, out var a);
            Days.Add(new CalendarDay(day, day.Month == Month.Month, day == today, a?.Entries ?? 0, a?.Words ?? 0, answered.Contains(day))
            {
                IsSelected = day == _selected,
            });
        }

        var inMonth = Days.Where(d => d.IsInMonth).ToList();
        var written = inMonth.Count(d => d.HasEntries);
        var checkIns = inMonth.Count(d => d.CheckedIn);
        MonthSummary = $"Wrote on {written} day{(written == 1 ? "" : "s")} · checked in on {checkIns} day{(checkIns == 1 ? "" : "s")}";
        LoadSelectedDay();
    }

    [RelayCommand]
    private void PreviousMonth() => ShowMonth(Month.AddMonths(-1));

    [RelayCommand]
    private void NextMonth() => ShowMonth(Month.AddMonths(1));

    [RelayCommand]
    private void ThisMonth()
    {
        _selected = _journal.Clock.Today();
        ShowMonth(new DateOnly(_selected.Year, _selected.Month, 1));
    }

    private void ShowMonth(DateOnly month)
    {
        Month = month;
        Load();
    }

    [RelayCommand]
    private void SelectDay(CalendarDay? day)
    {
        if (day is null) return;
        if (!day.IsInMonth)
        {
            _selected = day.Date;
            ShowMonth(new DateOnly(day.Date.Year, day.Date.Month, 1));
            return;
        }
        _selected = day.Date;
        foreach (var d in Days) d.IsSelected = d.Date == _selected;
        LoadSelectedDay();
    }

    [RelayCommand]
    private void Open(SearchResultItem? item)
    {
        if (item is not null) _open(item.Id);
    }

    private void LoadSelectedDay()
    {
        SelectedTitle = _selected.ToString("dddd, MMMM d", CultureInfo.CurrentCulture);
        DayEntries.Clear();
        var current = _currentEntryId();
        foreach (var entry in _journal.Entries.GetForDay(_selected))
            DayEntries.Add(SearchResultItem.From(entry, FtsQuery.Preview(entry.Summary, entry.Body), current));
        SelectedIsEmpty = DayEntries.Count == 0;
    }
}
