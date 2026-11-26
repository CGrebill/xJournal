using CommunityToolkit.Mvvm.ComponentModel;
using XJournal.Core;
using XJournal.Core.Models;
using XJournal.Core.Questions;

namespace XJournal.App.ViewModels;

public sealed record PreviewLine(string Title, string? Detail = null)
{
    public bool HasDetail => Detail is not null;
}

/// <summary>Shows exactly what an import will add, reword and archive before anything is saved.</summary>
public sealed partial class QuestionImportPreviewViewModel : ViewModelBase
{
    private readonly Journal _journal;
    private readonly IReadOnlyList<ValidQuestion> _questions;

    public QuestionImportPreviewViewModel(Journal journal, IReadOnlyList<ValidQuestion> questions)
    {
        _journal = journal;
        _questions = questions;
        ArchiveMissing = journal.Questions.GetAll().Count > 0;
        Plan = journal.QuestionImporter.Plan(questions, ArchiveMissing);
        Rebuild();
    }

    [ObservableProperty] public partial QuestionImportPlan Plan { get; set; }

    /// <summary>Replace the set (archive questions not in the file) vs. only add/update.</summary>
    [ObservableProperty] public partial bool ArchiveMissing { get; set; }

    public bool HasExisting => _journal.Questions.GetAll(includeArchived: true).Count > 0;

    public IReadOnlyList<PreviewLine> Added { get; private set; } = [];
    public IReadOnlyList<PreviewLine> Changed { get; private set; } = [];
    public IReadOnlyList<PreviewLine> Archived { get; private set; } = [];
    public bool HasAdded => Added.Count > 0;
    public bool HasChanged => Changed.Count > 0;
    public bool HasArchived => Archived.Count > 0;
    public string Headline { get; private set; } = "";
    public string UnchangedText { get; private set; } = "";

    partial void OnArchiveMissingChanged(bool value)
    {
        Plan = _journal.QuestionImporter.Plan(_questions, value);
        Rebuild();
    }

    private void Rebuild()
    {
        Added = Plan.Questions.Where(q => q.Change == QuestionChange.Added)
            .Select(q => new PreviewLine(q.Question.Text, QuestionTypes.Describe(q.Question.Type, q.Question.Config))).ToList();
        Changed = Plan.Questions.Where(q => q.Change == QuestionChange.Changed)
            .Select(q => new PreviewLine(q.Question.Text, ChangeDetail(q))).ToList();
        Archived = Plan.ToArchive.Select(q => new PreviewLine(q.Current.Text)).ToList();

        var restored = Plan.RestoredCount;
        UnchangedText = Plan.UnchangedCount == 0 && restored == 0
            ? ""
            : $"{Plan.UnchangedCount} unchanged" + (restored > 0 ? $", {restored} brought back from the archive" : "") + ".";
        Headline = Plan.HasChanges
            ? "Review the changes. Past answers always keep the exact wording they were given for."
            : "These questions are already up to date. Nothing will change.";

        OnPropertyChanged(nameof(HasAdded));
        OnPropertyChanged(nameof(HasChanged));
        OnPropertyChanged(nameof(HasArchived));
        OnPropertyChanged(nameof(Added));
        OnPropertyChanged(nameof(Changed));
        OnPropertyChanged(nameof(Archived));
        OnPropertyChanged(nameof(UnchangedText));
        OnPropertyChanged(nameof(Headline));
    }

    private static string ChangeDetail(PlannedQuestion q)
    {
        var previous = q.Previous!;
        var parts = new List<string>();
        if (previous.Text != q.Question.Text) parts.Add($"was: “{previous.Text}”");
        var before = QuestionTypes.Describe(previous.Type, previous.Config);
        var after = QuestionTypes.Describe(q.Question.Type, q.Question.Config);
        if (before != after) parts.Add($"now: {after}");
        return string.Join("\n", parts);
    }
}
