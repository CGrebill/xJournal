using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XJournal.App.Services;
using XJournal.Core;
using XJournal.Core.Backup;
using XJournal.Core.Sessions;

namespace XJournal.App.ViewModels;

public enum MainPane
{
    Editor,
    Search,
    Calendar,
}

/// <summary>
/// The journal screen. Typing is saved automatically after a short pause, and whenever you switch
/// entries, leave the window, sign out or close the app.
/// </summary>
public sealed partial class MainViewModel : ViewModelBase
{
    public static readonly TimeSpan AutosaveDelay = TimeSpan.FromSeconds(1.5);

    private readonly IDialogs _dialogs;
    private readonly Window _owner;
    private readonly DispatcherTimer _autosave;
    private readonly DispatcherTimer _clock;
    private readonly DispatcherTimer _idleCheck;
    private DateTimeOffset _lastActivity;
    private bool _loadingEntry;
    private bool _closed;
    private Window? _questionsWindow, _trendsWindow, _settingsWindow;

    public MainViewModel(Journal journal, IDialogs dialogs, Window owner)
    {
        Journal = journal;
        _dialogs = dialogs;
        _owner = owner;
        Session = new EntrySession(journal);
        Search = new SearchViewModel(journal, OpenEntry, () => Session.Current.Id);
        Calendar = new CalendarViewModel(journal, OpenEntry, () => Session.Current.Id);

        _autosave = new DispatcherTimer { Interval = AutosaveDelay };
        _autosave.Tick += (_, _) => SaveNow();
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateClock();
        _clock.Start();

        _lastActivity = journal.Clock.Now;
        Controls.JournalWindow.Activity += ReportActivity;
        _idleCheck = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _idleCheck.Tick += (_, _) => CheckIdle(Journal.Clock.Now);
        _idleCheck.Start();

        Session.CurrentChanged += (_, _) => LoadCurrentEntry();
        Session.Saved += (_, _) =>
        {
            if (!_wroteToday) RefreshStreak(); // the first entry of the day extends the streak
        };
        Session.Begin();
        UpdateClock();
        RefreshQuestionState();
    }

    public Journal Journal { get; }
    public EntrySession Session { get; }
    public SearchViewModel Search { get; }
    public CalendarViewModel Calendar { get; }
    public string Username => Journal.Username;

    /// <summary>Raised after signing out, with an optional notice for the login screen.</summary>
    public event Action<string?>? SignedOut;

    // ------------------------------------------------------------------ editor

    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial string Body { get; set; } = "";
    [ObservableProperty] public partial string SaveStatus { get; set; } = "";
    [ObservableProperty] public partial string WordCount { get; set; } = "";

    partial void OnSummaryChanged(string value) => OnTextEdited();
    partial void OnBodyChanged(string value) => OnTextEdited();

    private void OnTextEdited()
    {
        if (_loadingEntry) return;
        Session.SetText(Summary, Body);
        UpdateWordCount();
        UpdateEntryStamp();
        OnPropertyChanged(nameof(ShowMemory));
        if (Session.IsDirty)
        {
            SaveStatus = "Editing…";
            _autosave.Stop();
            _autosave.Start();
        }
    }

    /// <summary>Saves pending changes immediately (also called when the window loses focus).</summary>
    [RelayCommand]
    public void SaveNow()
    {
        _autosave.Stop();
        if (_closed) return;
        try
        {
            if (Session.Save()) SaveStatus = $"Saved {Journal.Clock.LocalNow():T}";
            else if (!Session.Current.IsPersisted) SaveStatus = "";
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "autosave");
            SaveStatus = "Couldn't save: " + ex.Message;
        }
    }

    private void LoadCurrentEntry()
    {
        _loadingEntry = true;
        Summary = Session.Current.Summary;
        Body = Session.Current.Body;
        _loadingEntry = false;
        SaveStatus = Session.Current.IsPersisted ? $"Last saved {Session.Current.UpdatedAt.LocalDateTime:g}" : "";
        UpdateWordCount();
        UpdateEntryStamp();
        UpdateRevisions();
        RefreshInsights();
        EditorFocusRequested?.Invoke();
    }

    // ------------------------------------------------------------------ previous versions

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShowRevisionsCommand))]
    public partial int RevisionCount { get; set; }

    public string RevisionsHeader => RevisionCount == 0 ? "Previous versions" : $"Previous versions ({RevisionCount})…";

    partial void OnRevisionCountChanged(int value) => OnPropertyChanged(nameof(RevisionsHeader));

    private void UpdateRevisions() =>
        RevisionCount = Session.Current.IsPersisted ? Journal.Entries.RevisionCount(Session.Current.Id) : 0;

    [RelayCommand(CanExecute = nameof(HasRevisions))]
    private async Task ShowRevisionsAsync()
    {
        SaveNow();
        var vm = new RevisionsViewModel(Journal.Entries.GetRevisions(Session.Current.Id));
        var view = new Views.RevisionsView { DataContext = vm };
        if (!await _dialogs.ShowContentAsync("Previous versions", view, "Restore", "Cancel", () => Task.FromResult(vm.Validate()))) return;
        if (_closed) return;
        try
        {
            Session.RestoreRevision(vm.Selected!.Revision.Id);
        }
        catch (Exception ex) when (Errors.IsExpected(ex))
        {
            await _dialogs.ShowMessageAsync("Couldn't restore", ex.Message);
        }
    }

    private bool HasRevisions() => RevisionCount > 0;

    /// <summary>Called after a merge changed the journal underneath the editor.</summary>
    public void OnJournalMerged()
    {
        if (_closed) return;
        Session.Reload();
        UpdateRevisions();
        RefreshQuestionState();
        if (IsSearchOpen) Search.Run();
        if (IsCalendarOpen) Calendar.Load();
    }

    /// <summary>Asks the view to put the caret in the editor.</summary>
    public event Action? EditorFocusRequested;

    private void UpdateWordCount()
    {
        var words = Core.Insights.WordCounter.Count(Body);
        WordCount = words == 1 ? "1 word" : $"{words:N0} words";
    }

    // ------------------------------------------------------------------ date / time card

    [ObservableProperty] public partial string ClockTime { get; set; } = "";
    [ObservableProperty] public partial string ClockDate { get; set; } = "";
    [ObservableProperty] public partial string EntryStamp { get; set; } = "";
    [ObservableProperty] public partial bool IsPastEntry { get; set; }

    private void UpdateClock()
    {
        var now = Journal.Clock.LocalNow();
        ClockTime = now.ToString("t", System.Globalization.CultureInfo.CurrentCulture);
        ClockDate = now.ToString("dddd, MMMM d", System.Globalization.CultureInfo.CurrentCulture);
        UpdateEntryStamp();
    }

    private void UpdateEntryStamp()
    {
        var entry = Session.Current;
        var created = entry.CreatedAt;
        if (!entry.IsPersisted && entry.IsEmpty)
        {
            EntryStamp = "New entry · not started yet";
            IsPastEntry = false;
            return;
        }

        IsPastEntry = Journal.Clock.LocalDay(created) != Journal.Clock.Today();
        EntryStamp = IsPastEntry
            ? $"Written {created:ddd, MMM d, yyyy} at {created:T}"
            : $"Started at {created:T}";
    }

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private void NewEntry()
    {
        _autosave.Stop();
        Session.StartNew();
        ActivePane = MainPane.Editor;
    }

    private void OpenEntry(long id)
    {
        _autosave.Stop();
        try
        {
            Session.Open(id);
        }
        catch (JournalException ex)
        {
            _ = _dialogs.ShowMessageAsync("Can't open entry", ex.Message);
        }
        ActivePane = MainPane.Editor;
    }

    [RelayCommand]
    private async Task DeleteEntryAsync()
    {
        if (!Session.Current.IsPersisted && Session.Current.IsEmpty) return;
        var ok = await _dialogs.ConfirmAsync("Delete this entry?",
            "This entry will be permanently deleted. This can't be undone.", "Delete");
        if (!ok || _closed) return;
        _autosave.Stop();
        try
        {
            Session.DeleteCurrent();
        }
        catch (Exception ex) when (Errors.IsExpected(ex))
        {
            await _dialogs.ShowMessageAsync("Couldn't delete the entry", ex.Message);
        }
    }

    // ------------------------------------------------------------------ panes (editor / search / calendar)

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditorOpen), nameof(IsSearchOpen), nameof(IsCalendarOpen), nameof(ShowMemory))]
    public partial MainPane ActivePane { get; set; }

    public bool IsEditorOpen => ActivePane == MainPane.Editor;

    public bool IsSearchOpen
    {
        get => ActivePane == MainPane.Search;
        set => ActivePane = value ? MainPane.Search : MainPane.Editor;
    }

    public bool IsCalendarOpen
    {
        get => ActivePane == MainPane.Calendar;
        set => ActivePane = value ? MainPane.Calendar : MainPane.Editor;
    }

    partial void OnActivePaneChanged(MainPane value)
    {
        if (value == MainPane.Editor) return;
        SaveNow();
        if (value == MainPane.Search) Search.Run();
        else Calendar.Load();
    }

    [RelayCommand]
    private void ToggleSearch() => IsSearchOpen = !IsSearchOpen;

    [RelayCommand]
    private void ToggleCalendar() => IsCalendarOpen = !IsCalendarOpen;

    /// <summary>Opens an entry picked from search results or the calendar.</summary>
    public void OpenResult(SearchResultItem item) => OpenEntry(item.Id);

    // ------------------------------------------------------------------ on this day & streak

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMemory), nameof(MemoryTitle), nameof(MemoryText))]
    public partial Core.Insights.Memory? Memory { get; set; }

    [ObservableProperty] public partial string StreakText { get; set; } = "";

    /// <summary>Shown only on a fresh, unstarted entry, so it never gets in the way of writing.</summary>
    public bool ShowMemory => Memory is not null && IsEditorOpen && !Session.Current.IsPersisted && Session.Current.IsEmpty;

    public string MemoryTitle => Memory is { } m ? $"On this day · {m.Ago}" : "";

    public string MemoryText => Memory is { } m
        ? string.IsNullOrWhiteSpace(m.Entry.Summary)
            ? Core.Search.FtsQuery.Preview("", m.Entry.Body, 160)
            : m.Entry.Summary.Trim()
        : "";

    private void RefreshInsights()
    {
        if (_closed) return;
        var today = Journal.Clock.Today();
        var dismissed = Journal.Settings.Get("memory_dismissed_day");
        Memory = dismissed == today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            ? null
            : Core.Insights.OnThisDay.Find(Journal, today) is [var latest, ..] ? latest : null;

        RefreshStreak();
    }

    private bool _wroteToday;

    private void RefreshStreak()
    {
        var stats = Core.Insights.WritingStats.Compute(Journal,
            System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek, weeks: 1);
        var today = Journal.Clock.Today();
        _wroteToday = Journal.Entries.GetDayActivity(today, today).Count > 0;
        StreakText = stats.CurrentStreak >= 2 ? $"{stats.CurrentStreak}-day streak" : "";
    }

    [RelayCommand]
    private void OpenMemory()
    {
        if (Memory is { } memory) OpenEntry(memory.Entry.Id);
    }

    [RelayCommand]
    private void DismissMemory()
    {
        Journal.Settings.Set("memory_dismissed_day",
            Journal.Clock.Today().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        Memory = null;
    }

    // ------------------------------------------------------------------ questions

    [ObservableProperty] public partial bool HasQuestions { get; set; }
    [ObservableProperty] public partial bool AnsweredToday { get; set; }
    [ObservableProperty] public partial bool ReminderDismissed { get; set; }

    public bool ShowReminder => HasQuestions && !AnsweredToday && !ReminderDismissed;
    public bool ShowQuestionBadge => HasQuestions && !AnsweredToday;

    partial void OnHasQuestionsChanged(bool value) => NotifyQuestionState();
    partial void OnAnsweredTodayChanged(bool value) => NotifyQuestionState();
    partial void OnReminderDismissedChanged(bool value) => NotifyQuestionState();

    private void NotifyQuestionState()
    {
        OnPropertyChanged(nameof(ShowReminder));
        OnPropertyChanged(nameof(ShowQuestionBadge));
    }

    public void RefreshQuestionState()
    {
        if (_closed) return;
        HasQuestions = Journal.Questions.Any();
        AnsweredToday = Journal.Answers.HasAnswered(Journal.Clock.Today());
    }

    [RelayCommand]
    private void DismissReminder() => ReminderDismissed = true;

    [RelayCommand]
    private void OpenQuestions()
    {
        SaveNow();
        ShowChild(ref _questionsWindow, () =>
        {
            var window = new Views.QuestionsWindow();
            var vm = new QuestionsViewModel(Journal, new WindowDialogs(window),
                () => Session.Current.IsPersisted ? Session.Current.Id : null);
            vm.AnswersChanged += RefreshQuestionState;
            window.DataContext = vm;
            return window;
        });
    }

    [RelayCommand]
    private void OpenTrends()
    {
        SaveNow();
        ShowChild(ref _trendsWindow, () =>
        {
            var window = new Views.TrendsWindow();
            window.DataContext = new TrendsViewModel(Journal, entryId => { OpenEntry(entryId); _owner.Activate(); });
            return window;
        });
    }

    [RelayCommand]
    private void OpenSettings()
    {
        SaveNow();
        ShowChild(ref _settingsWindow, () =>
        {
            var window = new Views.SettingsWindow();
            var vm = new SettingsViewModel(Journal, new WindowDialogs(window));
            vm.QuestionsChanged += RefreshQuestionState;
            vm.SignOutRequested += () => SignOutCommand.Execute(null);
            vm.MergeStarting += SaveNow;
            vm.Merged += OnJournalMerged;
            window.DataContext = vm;
            return window;
        });
    }

    private void ShowChild(ref Window? field, Func<Window> create)
    {
        if (field is { IsVisible: true })
        {
            field.Activate();
            return;
        }
        var window = create();
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_questionsWindow, window)) _questionsWindow = null;
            if (ReferenceEquals(_trendsWindow, window)) _trendsWindow = null;
            if (ReferenceEquals(_settingsWindow, window)) _settingsWindow = null;
            RefreshQuestionState();
        };
        field = window;
        window.Show(_owner);
    }

    // ------------------------------------------------------------------ auto-lock

    /// <summary>Records user input (any key or pointer activity in any xJournal window).</summary>
    public void ReportActivity() => _lastActivity = Journal.Clock.Now;

    /// <summary>Signs out if there's been no input for the auto-lock time.</summary>
    /// <returns>True if it signed out.</returns>
    public bool CheckIdle(DateTimeOffset now)
    {
        if (_closed) return false;
        var minutes = Journal.Settings.AutoLockMinutes;
        if (minutes <= 0 || now - _lastActivity < TimeSpan.FromMinutes(minutes)) return false;
        SignOut($"You were signed out after {minutes} minute{(minutes == 1 ? "" : "s")} without activity. Everything was saved.");
        return true;
    }

    // ------------------------------------------------------------------ closing

    [RelayCommand]
    private void SignOut() => SignOut(null);

    private void SignOut(string? reason)
    {
        var notice = Close();
        SignedOut?.Invoke(notice ?? reason);
    }

    /// <summary>
    /// Saves, runs the automatic backup and closes the journal. Safe to call more than once.
    /// </summary>
    /// <returns>A message to show if the automatic backup failed.</returns>
    public string? Close()
    {
        if (_closed) return null;
        _autosave.Stop();
        _clock.Stop();
        _idleCheck.Stop();
        Controls.JournalWindow.Activity -= ReportActivity;
        Search.Stop();
        string? notice = null;
        try
        {
            Session.Leave();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "save on close");
            notice = "Your last changes couldn't be saved: " + ex.Message;
        }

        try
        {
            JournalTransfer.RunAutoBackup(Journal);
        }
        catch (JournalException ex)
        {
            notice ??= ex.Message;
        }

        _closed = true;
        foreach (var child in new[] { _questionsWindow, _trendsWindow, _settingsWindow }) child?.Close();
        Journal.Dispose();
        return notice;
    }
}
