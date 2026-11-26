using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XJournal.App.Services;
using XJournal.Core;
using XJournal.Core.Models;
using XJournal.Core.Questions;

namespace XJournal.App.ViewModels;

/// <summary>One question in today's check-in. Subclasses hold the answer in the way that suits its type.</summary>
public abstract partial class QuestionItemViewModel : ViewModelBase
{
    private readonly Action<bool> _changed;
    private bool _loading = true;

    protected QuestionItemViewModel(QuestionVersion version, Action<bool> changed)
    {
        Version = version;
        _changed = changed;
    }

    public QuestionVersion Version { get; }
    public string Text => Version.Text;

    /// <summary>The current answer, or null if unanswered (or not valid yet, e.g. a half-typed number).</summary>
    public abstract AnswerValue? Answer { get; }

    public bool IsAnswered => Answer is not null;

    /// <summary>Sets the saved answer without triggering a save.</summary>
    public void Load(AnswerValue? saved)
    {
        _loading = true;
        Apply(saved);
        _loading = false;
        OnPropertyChanged(nameof(IsAnswered));
    }

    protected abstract void Apply(AnswerValue? answer);

    /// <summary>Call when the answer changed. <paramref name="typing"/> answers are saved after a short pause.</summary>
    protected void Changed(bool typing = false)
    {
        OnPropertyChanged(nameof(IsAnswered));
        if (!_loading) _changed(typing);
    }

    [RelayCommand]
    private void Clear()
    {
        Apply(null);
        Changed();
    }

    public static QuestionItemViewModel Create(QuestionVersion version, Action<bool> changed) => version.Type switch
    {
        QuestionType.Rating => new PickItemViewModel(version, changed,
            version.Labels.Select((label, i) => new PickOption((i + 1).ToString(CultureInfo.CurrentCulture), label, i + 1, null))),
        QuestionType.Scale => new ScaleItemViewModel(version, changed),
        QuestionType.YesNo => new PickItemViewModel(version, changed, [new PickOption("Yes", "", 1, null), new PickOption("No", "", 0, null)]),
        QuestionType.Choice => new ChoiceItemViewModel(version, changed),
        QuestionType.Number => new NumberItemViewModel(version, changed),
        _ => new TextItemViewModel(version, changed),
    };
}

/// <summary>A button for one possible answer. <see cref="Tag"/> is the big text (number), <see cref="Label"/> the small text.</summary>
public sealed record PickOption(string Tag, string Label, double? Number, string? Choice);

/// <summary>A picked option, bound to a radio button or toggle.</summary>
public sealed partial class OptionViewModel(PickItemViewModel owner, PickOption option, int index) : ViewModelBase
{
    public PickOption Option { get; } = option;
    public int Index { get; } = index;
    public string Tag => Option.Tag;
    public string Label => Option.Label;
    public bool HasLabel => Option.Label.Length > 0;

    public bool IsSelected
    {
        get => owner.IsPicked(Index);
        set => owner.SetPicked(Index, value);
    }

    internal void Refresh() => OnPropertyChanged(nameof(IsSelected));
}

/// <summary>Answered by picking one button: ratings and yes/no (and the base for scales and choices).</summary>
public partial class PickItemViewModel : QuestionItemViewModel
{
    private readonly HashSet<int> _picked = [];

    public PickItemViewModel(QuestionVersion version, Action<bool> changed, IEnumerable<PickOption> options)
        : base(version, changed)
    {
        Options = options.Select((o, i) => new OptionViewModel(this, o, i)).ToList();
    }

    public IReadOnlyList<OptionViewModel> Options { get; }
    protected virtual bool AllowsMultiple => false;

    internal bool IsPicked(int index) => _picked.Contains(index);

    internal void SetPicked(int index, bool picked)
    {
        if (picked == _picked.Contains(index)) return;
        if (picked)
        {
            if (!AllowsMultiple) _picked.Clear();
            _picked.Add(index);
        }
        else
        {
            _picked.Remove(index);
        }
        Refresh();
        Changed();
    }

    private void Refresh()
    {
        foreach (var option in Options) option.Refresh();
    }

    public override AnswerValue? Answer
    {
        get
        {
            if (_picked.Count == 0) return null;
            var picked = Options.Where(o => _picked.Contains(o.Index)).Select(o => o.Option).ToList();
            return picked[0].Choice is null
                ? AnswerValue.Of(picked[0].Number!.Value)
                : AnswerValue.OfChoices(picked.Select(p => p.Choice!));
        }
    }

    protected override void Apply(AnswerValue? answer)
    {
        _picked.Clear();
        if (answer is not null)
        {
            foreach (var option in Options)
            {
                var o = option.Option;
                if (o.Choice is null ? o.Number == answer.Number : answer.Choices.Contains(o.Choice))
                    _picked.Add(option.Index);
            }
        }
        Refresh();
    }
}

/// <summary>A numbered scale (1–10 by default), with optional words under the ends.</summary>
public sealed class ScaleItemViewModel(QuestionVersion version, Action<bool> changed)
    : PickItemViewModel(version, changed,
        Enumerable.Range(version.Config.Min ?? 1, (version.Config.Max ?? 10) - (version.Config.Min ?? 1) + 1)
            .Select(n => new PickOption(n.ToString(CultureInfo.CurrentCulture), "", n, null)))
{
    public string Low => Version.Config.Low ?? "";
    public string High => Version.Config.High ?? "";
    public bool HasEndLabels => !string.IsNullOrEmpty(Low) || !string.IsNullOrEmpty(High);
}

/// <summary>The question's own options; several can be picked when the question allows it.</summary>
public sealed class ChoiceItemViewModel(QuestionVersion version, Action<bool> changed)
    : PickItemViewModel(version, changed, version.Options.Select(o => new PickOption(o, "", null, o)))
{
    protected override bool AllowsMultiple => Version.Config.AllowsMultiple;
    public string Hint => AllowsMultiple ? "Pick any that apply." : "";
}

/// <summary>A number, with an optional unit.</summary>
public sealed partial class NumberItemViewModel(QuestionVersion version, Action<bool> changed) : QuestionItemViewModel(version, changed)
{
    public string Unit => Version.Config.Unit ?? "";

    [ObservableProperty] public partial string Input { get; set; } = "";
    [ObservableProperty] public partial string Error { get; set; } = "";

    partial void OnInputChanged(string value)
    {
        Error = Input.Trim().Length > 0 && Parse() is null ? "Enter a number, like 7 or 7.5." : "";
        Changed(typing: true);
    }

    private double? Parse() =>
        double.TryParse(Input.Trim(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out var n)
        && AnswerRules.Validate(Version, AnswerValue.Of(n)) is null
            ? n
            : null;

    public override AnswerValue? Answer => Parse() is { } n ? AnswerValue.Of(n) : null;

    protected override void Apply(AnswerValue? answer) =>
        Input = answer?.Number is { } n ? n.ToString("0.##", CultureInfo.CurrentCulture) : "";
}

/// <summary>A short sentence.</summary>
public sealed partial class TextItemViewModel(QuestionVersion version, Action<bool> changed) : QuestionItemViewModel(version, changed)
{
    public int MaxLength { get; } = AnswerRules.MaxTextAnswerLength;

    [ObservableProperty] public partial string Input { get; set; } = "";

    partial void OnInputChanged(string value) => Changed(typing: true);

    public override AnswerValue? Answer =>
        string.IsNullOrWhiteSpace(Input) ? null : AnswerValue.OfText(Input.Trim().ReplaceLineEndings(" "));

    protected override void Apply(AnswerValue? answer) => Input = answer?.Text ?? "";
}

/// <summary>Today's check-in. Picked answers save at once; typed ones shortly after typing stops, and on close.</summary>
public sealed partial class QuestionsViewModel : ViewModelBase
{
    private readonly Journal _journal;
    private readonly IDialogs _dialogs;
    private readonly Func<long?> _currentEntryId;
    private readonly DateOnly _day;
    private readonly DispatcherTimer _typingPause = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private IReadOnlyDictionary<long, AnswerValue> _otherAnswers = new Dictionary<long, AnswerValue>();
    private bool _pending;

    public QuestionsViewModel(Journal journal, IDialogs dialogs, Func<long?> currentEntryId)
    {
        _journal = journal;
        _dialogs = dialogs;
        _currentEntryId = currentEntryId;
        _day = journal.Clock.Today();
        DateText = _day.ToString("dddd, MMMM d, yyyy", CultureInfo.CurrentCulture);
        _typingPause.Tick += (_, _) => SaveNow();
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
        var existing = _journal.Answers.GetForDay(_day);
        Items.Clear();
        foreach (var q in _journal.Questions.GetAll())
        {
            var item = QuestionItemViewModel.Create(q.Current, OnAnswered);
            item.Load(existing?.Values.GetValueOrDefault(q.Current.Id));
            Items.Add(item);
        }
        var shown = Items.Select(i => i.Version.Id).ToHashSet();
        _otherAnswers = existing?.Values.Where(kv => !shown.Contains(kv.Key)).ToDictionary() ?? [];
        Status = existing is null ? "Answers save as you go." : $"Saved {existing.AnsweredAt:t}";
        UpdateProgress();
        OnPropertyChanged(nameof(HasQuestions));
    }

    private void UpdateProgress()
    {
        var answered = Items.Count(i => i.IsAnswered);
        Progress = Items.Count == 0 ? "" : $"{answered} of {Items.Count} answered";
    }

    private void OnAnswered(bool typing)
    {
        UpdateProgress();
        _pending = true;
        _typingPause.Stop();
        if (typing) _typingPause.Start();
        else SaveNow();
    }

    /// <summary>Saves any unsaved answers (called after a typing pause and when the window closes).</summary>
    public void SaveNow()
    {
        _typingPause.Stop();
        if (!_pending || _journal.IsDisposed) return;
        _pending = false;

        // Keep answers to questions not shown (e.g. archived since) — never drop data.
        var values = new Dictionary<long, AnswerValue>(_otherAnswers);
        foreach (var item in Items)
            if (item.Answer is { } answer) values[item.Version.Id] = answer;

        try
        {
            var now = _journal.Clock.LocalNow();
            _journal.Answers.SaveForDay(_day, _currentEntryId(), values, now);
            Status = $"Saved {now:t}";
        }
        catch (Exception ex) when (Errors.IsExpected(ex) || ex is ArgumentException)
        {
            Status = "Couldn't save: " + ex.Message;
        }
        AnswersChanged?.Invoke();
    }

    [RelayCommand]
    private async Task ImportQuestionsAsync()
    {
        SaveNow();
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

        QuestionFileParseResult parsed;
        try
        {
            parsed = QuestionFileParser.ParseFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await dialogs.ShowMessageAsync("Couldn't read the file", ex.Message);
            return false;
        }

        if (!parsed.IsValid)
        {
            await ShowProblemsAsync(dialogs, parsed.Errors);
            return false;
        }

        var preview = new QuestionImportPreviewViewModel(journal, parsed.Questions);
        if (preview.Plan.Errors.Count > 0)
        {
            await ShowProblemsAsync(dialogs, preview.Plan.Errors);
            return false;
        }

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

    private static Task ShowProblemsAsync(IDialogs dialogs, IReadOnlyList<string> problems)
    {
        var errors = string.Join("\n", problems.Take(12).Select(e => "• " + e));
        if (problems.Count > 12) errors += $"\n…and {problems.Count - 12} more.";
        return dialogs.ShowMessageAsync("This question file has problems", errors + "\n\nNothing was imported.");
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
