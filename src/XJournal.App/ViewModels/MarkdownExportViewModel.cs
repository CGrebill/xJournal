using CommunityToolkit.Mvvm.ComponentModel;
using XJournal.Core.Export;

namespace XJournal.App.ViewModels;

/// <summary>Options for "Export entries as Markdown".</summary>
public sealed partial class MarkdownExportViewModel : ViewModelBase
{
    [ObservableProperty] public partial DateTime? FromDate { get; set; }
    [ObservableProperty] public partial DateTime? ToDate { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OneFilePerEntry))]
    public partial bool SingleDocument { get; set; } = true;

    public bool OneFilePerEntry
    {
        get => !SingleDocument;
        set => SingleDocument = !value;
    }

    [ObservableProperty] public partial bool IncludeCheckIns { get; set; } = true;
    [ObservableProperty] public partial string? Error { get; set; }

    public bool Validate()
    {
        Error = FromDate > ToDate ? "The start date is after the end date." : null;
        return Error is null;
    }

    public MarkdownOptions ToOptions() => new(
        FromDate is { } from ? DateOnly.FromDateTime(from) : null,
        ToDate is { } to ? DateOnly.FromDateTime(to) : null,
        OneFilePerEntry,
        IncludeCheckIns);
}
