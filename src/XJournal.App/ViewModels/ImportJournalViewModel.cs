using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XJournal.Core;
using XJournal.Core.Accounts;
using XJournal.Core.Backup;

namespace XJournal.App.ViewModels;

/// <summary>The "Import journal" dialog: unlock/inspect a backup or JSON export, then choose where it goes.</summary>
public sealed partial class ImportJournalViewModel : ViewModelBase
{
    private readonly AccountStore _accounts;

    public ImportJournalViewModel(AccountStore accounts, string path, string? suggestedUsername)
    {
        _accounts = accounts;
        FilePath = path;
        IsJson = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        TargetUsername = string.IsNullOrWhiteSpace(suggestedUsername) ? "" : suggestedUsername.Trim();
        UseNewPassword = IsJson;
        if (IsJson) Inspect();
    }

    public string FilePath { get; }
    public string FileName => Path.GetFileName(FilePath);
    public bool IsJson { get; }
    public bool IsBackup => !IsJson;

    [ObservableProperty] public partial string BackupPassword { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnlocked), nameof(SummaryText))]
    public partial BackupSummary? Summary { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetExists))]
    public partial string TargetUsername { get; set; }

    [ObservableProperty] public partial bool ReplaceExisting { get; set; }
    [ObservableProperty] public partial bool UseNewPassword { get; set; }
    [ObservableProperty] public partial string NewPassword { get; set; } = "";
    [ObservableProperty] public partial string ConfirmNewPassword { get; set; } = "";
    [ObservableProperty] public partial string? Error { get; set; }

    public bool IsUnlocked => Summary is not null;
    public bool TargetExists => _accounts.FindAccount(TargetUsername ?? "") is not null;

    public string SummaryText => Summary is not { } s
        ? ""
        : s.EntryCount == 0
            ? "No entries yet."
            : $"{s.EntryCount} entr{(s.EntryCount == 1 ? "y" : "ies")} from {s.FirstEntry:d} to {s.LastEntry:d} · " +
              $"{s.QuestionCount} question{(s.QuestionCount == 1 ? "" : "s")} · {s.AnsweredDays} day{(s.AnsweredDays == 1 ? "" : "s")} of answers";

    [RelayCommand]
    private void Unlock()
    {
        if (IsJson) return;
        Error = null;
        if (BackupPassword.Length == 0) { Error = "Enter the backup's password."; return; }
        try
        {
            Summary = JournalTransfer.InspectBackup(FilePath, BackupPassword);
        }
        catch (InvalidPasswordException)
        {
            Error = "That password doesn't open this backup.";
        }
        catch (JournalException ex)
        {
            Error = ex.Message;
        }
    }

    private void Inspect()
    {
        try
        {
            Summary = JournalTransfer.InspectJson(FilePath);
        }
        catch (JournalException ex)
        {
            Error = ex.Message;
        }
    }

    /// <summary>Checks the form; returns false (and shows why) if the import can't run yet.</summary>
    public bool Validate()
    {
        Error = null;
        if (!IsUnlocked)
        {
            if (IsBackup) Unlock();
            if (!IsUnlocked) { Error ??= "Unlock the backup first."; return false; }
        }
        if (AccountStore.ValidateUsername(TargetUsername) is { } usernameError) { Error = usernameError; return false; }
        if (TargetExists && !ReplaceExisting)
        {
            Error = "That account already exists. Choose another name, or tick \"Replace it\".";
            return false;
        }
        if (UseNewPassword && AccountStore.ValidateNewPassword(NewPassword, ConfirmNewPassword) is { } passwordError)
        {
            Error = passwordError;
            return false;
        }
        return true;
    }

    /// <summary>Performs the import (call off the UI thread) and returns the signed-in journal.</summary>
    public Journal Run()
    {
        var transfer = new JournalTransfer(_accounts);
        return IsJson
            ? transfer.ImportJson(FilePath, TargetUsername, NewPassword, ReplaceExisting)
            : transfer.ImportBackup(FilePath, BackupPassword, TargetUsername, UseNewPassword ? NewPassword : null, ReplaceExisting);
    }
}
