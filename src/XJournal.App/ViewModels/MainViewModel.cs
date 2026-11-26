using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XJournal.App.Services;
using XJournal.Core;
using XJournal.Core.Backup;
using XJournal.Core.Sessions;

namespace XJournal.App.ViewModels;

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

        _autosave = new DispatcherTimer { Interval = AutosaveDelay };
        _autosave.Tick += (_, _) => SaveNow();
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateClock();
        _clock.Start();

        Session.CurrentChanged += (_, _) => LoadCurrentEntry();
        Session.Begin();
        UpdateClock();
        RefreshQuestionState();
    }

    public Journal Journal { get; }
    public EntrySession Session { get; }
    public SearchViewModel Search { get; }
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
        EditorFocusRequested?.Invoke();
    }

    /// <summary>Asks the view to put the caret in the editor.</summary>
    public event Action? EditorFocusRequested;

    private void UpdateWordCount()
    {
        var words = Body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
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
        ClockTime = now.ToString("t");
        ClockDate = now.ToString("dddd, MMMM d");
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
        IsSearchOpen = false;
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
        IsSearchOpen = false;
    }

    [RelayCommand]
    private async Task DeleteEntryAsync()
    {
        if (!Session.Current.IsPersisted && Session.Current.IsEmpty) return;
        var ok = await _dialogs.ConfirmAsync("Delete this entry?",
            "This entry will be permanently deleted. This can't be undone.", "Delete", destructive: true);
        if (!ok) return;
        _autosave.Stop();
        Session.DeleteCurrent();
    }

    [ObservableProperty] public partial bool IsSearchOpen { get; set; }

    partial void OnIsSearchOpenChanged(bool value)
    {
        if (!value) return;
        SaveNow();
        Search.Run();
    }

    [RelayCommand]
    private void ToggleSearch() => IsSearchOpen = !IsSearchOpen;

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

    // ------------------------------------------------------------------ closing

    [RelayCommand]
    private void SignOut()
    {
        var notice = Close();
        SignedOut?.Invoke(notice);
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
        string? notice = null;
        try
        {
            Session.Leave();
        }
        catch (Exception ex)
        {
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
