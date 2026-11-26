using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using XJournal.Core;
using XJournal.Core.Models;

namespace XJournal.App.ViewModels;

public sealed record SearchResultItem(long Id, string DateText, string TimeText, string Summary, string Snippet, bool IsCurrent)
{
    public bool HasSummary => Summary.Length > 0;
}

/// <summary>Search by words and/or dates. Results refresh as you type.</summary>
public sealed partial class SearchViewModel : ViewModelBase
{
    private readonly Journal _journal;
    private readonly Action<long> _open;
    private readonly Func<long> _currentEntryId;
    private readonly DispatcherTimer _debounce;

    public SearchViewModel(Journal journal, Action<long> open, Func<long> currentEntryId)
    {
        _journal = journal;
        _open = open;
        _currentEntryId = currentEntryId;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Run(); };
    }

    public ObservableCollection<SearchResultItem> Results { get; } = [];

    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial DateTime? FromDate { get; set; }
    [ObservableProperty] public partial DateTime? ToDate { get; set; }
    [ObservableProperty] public partial string ResultSummary { get; set; } = "";
    [ObservableProperty] public partial bool HasNoResults { get; set; }

    partial void OnQueryChanged(string value) => Schedule();
    partial void OnFromDateChanged(DateTime? value) => Schedule();
    partial void OnToDateChanged(DateTime? value) => Schedule();

    /// <summary>Cancels any pending search (called when the journal closes).</summary>
    public void Stop()
    {
        _debounce.Stop();
        _stopped = true;
    }

    private bool _stopped;

    private void Schedule()
    {
        if (_stopped) return;
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Quick date ranges: "today", "week", "month", "year" or "all".</summary>
    [RelayCommand]
    private void Preset(string preset)
    {
        var today = _journal.Clock.Today().ToDateTime(TimeOnly.MinValue);
        (FromDate, ToDate) = preset switch
        {
            "today" => (today, today),
            "week" => (today.AddDays(-6), today),
            "month" => (today.AddDays(-29), today),
            "year" => (today.AddDays(-364), today),
            _ => ((DateTime?)null, (DateTime?)null),
        };
    }

    [RelayCommand]
    private void Open(SearchResultItem? item)
    {
        if (item is not null) _open(item.Id);
    }

    public void Run()
    {
        if (_stopped) return;
        var from = FromDate is { } f ? DateOnly.FromDateTime(f) : (DateOnly?)null;
        var to = ToDate is { } t ? DateOnly.FromDateTime(t) : (DateOnly?)null;
        if (from > to) (from, to) = (to, from);

        var results = _journal.Entries.Search(new EntryQuery(Query, from, to));
        var current = _currentEntryId();
        Results.Clear();
        foreach (var r in results)
        {
            var e = r.Entry;
            Results.Add(new SearchResultItem(
                e.Id,
                e.CreatedAt.ToString("ddd, MMM d, yyyy", System.Globalization.CultureInfo.CurrentCulture),
                e.CreatedAt.ToString("t", System.Globalization.CultureInfo.CurrentCulture),
                e.Summary.Trim(),
                r.Snippet,
                e.Id == current));
        }

        var filtered = !string.IsNullOrWhiteSpace(Query) || from is not null || to is not null;
        HasNoResults = Results.Count == 0;
        ResultSummary = Results.Count switch
        {
            0 when !filtered => "No entries yet. Start writing and they'll appear here.",
            0 => "Nothing matches.",
            1 => "1 entry",
            _ => $"{Results.Count} entries",
        };
    }
}
