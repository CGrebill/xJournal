using CommunityToolkit.Mvvm.ComponentModel;
using XJournal.Core.Models;
using XJournal.Core.Search;

namespace XJournal.App.ViewModels;

public sealed record RevisionItem(EntryRevision Revision)
{
    public string Edited => $"Edited {Revision.UpdatedAt:ddd, MMM d, yyyy} at {Revision.UpdatedAt:t}";

    public string Why => Revision.Reason == EntryRevision.RestoreReason
        ? $"Replaced by a restore on {Revision.SavedAt:MMM d, yyyy}"
        : $"Kept during a merge on {Revision.SavedAt:MMM d, yyyy}";

    public string Summary => Revision.Summary.Trim();
    public bool HasSummary => Summary.Length > 0;
    public string Preview => FtsQuery.Preview(Revision.Summary, Revision.Body, 400);
}

/// <summary>Lists an entry's previous versions so one can be restored.</summary>
public sealed partial class RevisionsViewModel(IReadOnlyList<EntryRevision> revisions) : ViewModelBase
{
    public IReadOnlyList<RevisionItem> Items { get; } = revisions.Select(r => new RevisionItem(r)).ToList();

    [ObservableProperty] public partial RevisionItem? Selected { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }

    partial void OnSelectedChanged(RevisionItem? value) => Error = null;

    public bool Validate()
    {
        Error = Selected is null ? "Choose the version to restore." : null;
        return Error is null;
    }
}
