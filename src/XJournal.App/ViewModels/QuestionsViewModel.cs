using System.Collections.ObjectModel;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XJournal.App.Services;
using XJournal.Core;
using XJournal.Core.Models;

namespace XJournal.App.ViewModels;

public sealed partial class ChoiceViewModel(QuestionItemViewModel owner, int value, string label) : ViewModelBase
{
    public int Value { get; } = value;
    public string Label { get; } = label;
    public string Number { get; } = value.ToString(System.Globalization.CultureInfo.CurrentCulture);
    public string Group => owner.Group;

    public bool IsSelected
    {
        get => owner.Value == Value;
        set
        {
            if (value && owner.Value != Value) owner.Value = Value;
        }
    }

    internal void Refresh() => OnPropertyChanged(nameof(IsSelected));
}

public sealed partial class QuestionItemViewModel : ViewModelBase
{
    private readonly Action _changed;

    public QuestionItemViewModel(QuestionVersion version, int? value, Action changed)
    {
        Version = version;
        Value = value;
        _changed = changed;
        Choices = version.Scale.Select((label, i) => new ChoiceViewModel(this, i + 1, label)).ToList();
    }

    public QuestionVersion Version { get; }
    public string Text => Version.Text;
    public string Group => "q" + Version.Id;
    public IReadOnlyList<ChoiceViewModel> Choices { get; }

    [ObservableProperty] public partial int? Value { get; set; }

    partial void OnValueChanged(int? value)
    {
        if (Choices is null) return; // during construction
        foreach (var choice in Choices) choice.Refresh();
        _changed();
    }
}

/// <summary>Today's 1–5 check-in. Each answer is saved as soon as it's picked.</summary>
public sealed partial class QuestionsViewModel : ViewModelBase
{
    private readonly Journal _journal;
    private readonly IDialogs _dialogs;
    private readonly Func<long?> _currentEntryId;
    private readonly DateOnly _day;
    private IReadOnlyDictionary<long, int> _otherAnswers = new Dictionary<long, int>();
    private bool _loading;

    public QuestionsViewModel(Journal journal, IDialogs dialogs, Func<long?> currentEntryId)
    {
        _journal = journal;
        _dialogs = dialogs;
        _currentEntryId = currentEntryId;
        _day = journal.Clock.Today();
        DateText = _day.ToString("dddd, MMMM d, yyyy", System.Globalization.CultureInfo.CurrentCulture);
        Load();
    }

    /// <summary>Raised after answers are saved or questions change.</summary>
    public event Action? AnswersChanged;

    public string DateText { get; }
    public ObservableCollection<QuestionItemViewModel> Items { get; } = [];

    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial string Progress { get; set; } = "";

    public bool HasQuestions => Items.Count > 0;

    private void Load()
    {
        _loading = true;
        var existing = _journal.Answers.GetForDay(_day);
        var questions = _journal.Questions.GetAll();
        Items.Clear();
        foreach (var q in questions)
        {
            int? value = existing?.Values.TryGetValue(q.Current.Id, out var v) == true ? v : null;
            Items.Add(new QuestionItemViewModel(q.Current, value, OnAnswered));
        }
        var shown = Items.Select(i => i.Version.Id).ToHashSet();
        _otherAnswers = existing?.Values.Where(kv => !shown.Contains(kv.Key)).ToDictionary() ?? [];
        Status = existing is null ? "Answers save as you pick them." : $"Saved {existing.AnsweredAt:t}";
        _loading = false;
        UpdateProgress();
        OnPropertyChanged(nameof(HasQuestions));
    }

    private void UpdateProgress()
    {
        var answered = Items.Count(i => i.Value is not null);
        Progress = Items.Count == 0 ? "" : $"{answered} of {Items.Count} answered";
    }

    private void OnAnswered()
    {
        if (_loading) return;
        UpdateProgress();
        // Keep answers to questions not shown (e.g. archived since) — never drop data.
        var values = new Dictionary<long, int>(_otherAnswers);
        foreach (var item in Items)
            if (item.Value is { } value) values[item.Version.Id] = value;

        try
        {
            var now = _journal.Clock.LocalNow();
            _journal.Answers.SaveForDay(_day, _currentEntryId(), values, now);
            Status = $"Saved {now:t}";
        }
        catch (Exception ex) when (Errors.IsExpected(ex))
        {
            Status = "Couldn't save: " + ex.Message;
        }
        AnswersChanged?.Invoke();
    }

    [RelayCommand]
    private async Task ImportQuestionsAsync()
    {
        if (await QuestionImportFlow.RunAsync(_journal, _dialogs))
        {
            Load();
            AnswersChanged?.Invoke();
        }
    }

    [RelayCommand]
    private Task ExportQuestionsAsync() => QuestionImportFlow.ExportAsync(_journal, _dialogs);

    [RelayCommand]
    private Task SaveSampleAsync() => QuestionImportFlow.SaveSampleAsync(_dialogs);
}

/// <summary>Shared steps for importing/exporting question files (used by the questions and settings windows).</summary>
public static class QuestionImportFlow
{
    public static async Task<bool> RunAsync(Journal journal, IDialogs dialogs)
    {
        var path = await dialogs.PickOpenFileAsync("Import questions", FileTypes.QuestionJson);
        if (path is null) return false;

        Core.Questions.QuestionFileParseResult parsed;
        try
        {
            parsed = Core.Questions.QuestionFileParser.ParseFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await dialogs.ShowMessageAsync("Couldn't read the file", ex.Message);
            return false;
        }

        if (!parsed.IsValid)
        {
            var errors = string.Join("\n", parsed.Errors.Take(12).Select(e => "• " + e));
            if (parsed.Errors.Count > 12) errors += $"\n…and {parsed.Errors.Count - 12} more.";
            await dialogs.ShowMessageAsync("This question file has problems", errors + "\n\nNothing was imported.");
            return false;
        }

        var preview = new QuestionImportPreviewViewModel(journal, parsed.Questions);
        var view = new Views.QuestionImportPreviewView { DataContext = preview };
        if (!await dialogs.ShowContentAsync("Import questions", view, "Import", "Cancel")) return false;
        if (!preview.Plan.HasChanges) return false;

        if (journal.IsDisposed) return false;
        try
        {
            journal.QuestionImporter.Apply(preview.Plan);
        }
        catch (Exception ex) when (Errors.IsExpected(ex))
        {
            await dialogs.ShowMessageAsync("The import didn't complete", "Nothing was changed.\n\n" + ex.Message);
            return false;
        }
        return true;
    }

    public static async Task ExportAsync(Journal journal, IDialogs dialogs)
    {
        var path = await dialogs.PickSaveFileAsync("Export questions", "questions.json", FileTypes.QuestionJson);
        if (path is null || journal.IsDisposed) return;
        try
        {
            await File.WriteAllTextAsync(path, journal.QuestionImporter.Export().ToJson());
        }
        catch (Exception ex) when (Errors.IsExpected(ex))
        {
            await dialogs.ShowMessageAsync("Couldn't save the file", ex.Message);
        }
    }

    public static async Task SaveSampleAsync(IDialogs dialogs)
    {
        var path = await dialogs.PickSaveFileAsync("Save sample question file", "sample-questions.json", FileTypes.QuestionJson);
        if (path is null) return;
        try
        {
            await using var source = AssetLoader.Open(new Uri("avares://xJournal/Assets/sample-questions.json"));
            await using var target = File.Create(path);
            await source.CopyToAsync(target);
        }
        catch (Exception ex) when (Errors.IsExpected(ex))
        {
            await dialogs.ShowMessageAsync("Couldn't save the file", ex.Message);
        }
    }
}
