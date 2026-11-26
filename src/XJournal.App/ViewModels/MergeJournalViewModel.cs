using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XJournal.Core;
using XJournal.Core.Backup;

namespace XJournal.App.ViewModels;

/// <summary>The "Merge in a journal" dialog: unlock the file, preview what will change, then merge.</summary>
public sealed partial class MergeJournalViewModel : ViewModelBase
{
    private readonly Journal _journal;

    public MergeJournalViewModel(Journal journal, string path)
    {
        _journal = journal;
        FilePath = path;
        IsJson = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        if (IsJson) Load(() => JournalTransfer.ReadJson(path));
    }

    public string FilePath { get; }
    public string FileName => Path.GetFileName(FilePath);
    public bool IsJson { get; }
    public bool IsBackup => !IsJson;

    [ObservableProperty] public partial string BackupPassword { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnlocked), nameof(PreviewLines), nameof(HasChanges), nameof(NothingToMerge))]
    public partial MergePlan? Plan { get; set; }

    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }

    public bool IsUnlocked => Plan is not null;
    public bool HasChanges => Plan?.Summary.HasChanges == true;
    public bool NothingToMerge => Plan is not null && !HasChanges;
    public IReadOnlyList<string> PreviewLines => Plan is null ? [] : Describe(Plan.Summary);

    [RelayCommand]
    private async Task UnlockAsync()
    {
        if (IsBusy) return;
        Error = null;
        if (BackupPassword.Length == 0) { Error = "Enter the backup's password."; return; }
        IsBusy = true;
        try
        {
            // Deriving the key is deliberately slow; keep the dialog responsive.
            var (path, password) = (FilePath, BackupPassword);
            var incoming = await Task.Run(() => JournalTransfer.ReadBackup(path, password));
            Load(() => incoming);
        }
        catch (InvalidPasswordException)
        {
            Error = "That password doesn't open this backup.";
        }
        catch (Exception ex) when (Services.Errors.IsExpected(ex))
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Load(Func<JournalExport> read)
    {
        try
        {
            Plan = JournalMerger.Plan(_journal, read());
        }
        catch (InvalidPasswordException)
        {
            Error = "That password doesn't open this backup.";
        }
        catch (Exception ex) when (Services.Errors.IsExpected(ex))
        {
            Error = "Couldn't read the file: " + ex.Message;
        }
    }

    /// <summary>The dialog's Merge button: unlocks first if needed; only closes when there's something to merge.</summary>
    public async Task<bool> ValidateAsync()
    {
        if (!IsUnlocked && IsBackup) await UnlockAsync();
        if (!IsUnlocked) { Error ??= "Unlock the backup first."; return false; }
        if (!HasChanges) { Error = "Everything in this file is already in your journal."; return false; }
        return true;
    }

    public MergeSummary Run() => JournalMerger.Apply(_journal, Plan!, JournalMerger.DefaultSafetyCopyPath(_journal));

    public static IReadOnlyList<string> Describe(MergeSummary s)
    {
        var lines = new List<string>();
        if (s.NewEntries > 0) lines.Add(Count(s.NewEntries, "new entry", "new entries"));
        if (s.UpdatedEntries > 0)
            lines.Add(Count(s.UpdatedEntries, "entry", "entries") + " updated with a newer version (your text is kept as a previous version)");
        if (s.KeptEntries > 0)
            lines.Add(Count(s.KeptEntries, "entry", "entries") + " newer here, kept (the other text is saved as a previous version)");
        if (s.NewRevisions > 0) lines.Add(Count(s.NewRevisions, "previous version", "previous versions") + " of entries");
        if (s.NewQuestions > 0) lines.Add(Count(s.NewQuestions, "new question", "new questions"));
        if (s.NewWordings > 0) lines.Add(Count(s.NewWordings, "new question wording", "new question wordings"));
        if (s.NewAnswerDays > 0) lines.Add(Count(s.NewAnswerDays, "new day", "new days") + " of check-in answers");
        if (s.MergedAnswerDays > 0) lines.Add(Count(s.MergedAnswerDays, "day", "days") + " of answers combined");
        return lines;

        static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
    }
}
