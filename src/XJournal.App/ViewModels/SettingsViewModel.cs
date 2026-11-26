using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XJournal.App.Services;
using XJournal.Core;
using XJournal.Core.Accounts;
using XJournal.Core.Backup;

namespace XJournal.App.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly Journal _journal;
    private readonly IDialogs _dialogs;
    private bool _loading = true;

    public SettingsViewModel(Journal journal, IDialogs dialogs)
    {
        _journal = journal;
        _dialogs = dialogs;
        var settings = journal.Settings;
        ThemeIndex = settings.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        ResumeMinutes = (decimal)settings.ResumeWindow.TotalMinutes;
        AutoLockMinutes = settings.AutoLockMinutes;
        AutoBackupFolder = settings.AutoBackupFolder;
        AutoBackupKeep = settings.AutoBackupKeep;
        _loading = false;
    }

    public event Action? QuestionsChanged;
    public event Action? SignOutRequested;

    /// <summary>Raised before a merge writes to the journal (the editor should save first).</summary>
    public event Action? MergeStarting;

    /// <summary>Raised after a merge (the editor should reload what it shows).</summary>
    public event Action? Merged;

    public string Username => _journal.Username;
    public string JournalLocation => _journal.Database.FilePath;
    public static string AboutText => $"xJournal {JournalTransfer.AppVersion} · Copyright 2026 CGrebill · PolyForm Noncommercial License 1.0.0";
    public IReadOnlyList<string> Themes { get; } = ["Use system setting", "Light", "Dark"];

    // ------------------------------------------------------------------ appearance & entries

    [ObservableProperty] public partial int ThemeIndex { get; set; }
    [ObservableProperty] public partial decimal? ResumeMinutes { get; set; }

    [ObservableProperty] public partial decimal? AutoLockMinutes { get; set; }

    partial void OnAutoLockMinutesChanged(decimal? value)
    {
        if (_loading || value is null) return;
        _journal.Settings.AutoLockMinutes = (int)Math.Clamp(value.Value, 0, 24 * 60);
    }

    partial void OnThemeIndexChanged(int value)
    {
        if (_loading) return;
        var theme = value switch { 1 => "light", 2 => "dark", _ => "system" };
        _journal.Settings.Theme = theme;
        AppShell.ApplyTheme(theme);
    }

    partial void OnResumeMinutesChanged(decimal? value)
    {
        if (_loading || value is null) return;
        _journal.Settings.ResumeWindow = TimeSpan.FromMinutes((double)Math.Clamp(value.Value, 0, 24 * 60));
    }

    // ------------------------------------------------------------------ questions

    [RelayCommand]
    private async Task ImportQuestionsAsync()
    {
        if (await QuestionImportFlow.RunAsync(_journal, _dialogs)) QuestionsChanged?.Invoke();
    }

    [RelayCommand]
    private Task ExportQuestionsAsync() => QuestionImportFlow.ExportAsync(_journal, _dialogs);

    [RelayCommand]
    private Task SaveSampleQuestionsAsync() => QuestionImportFlow.SaveSampleAsync(_dialogs);

    // ------------------------------------------------------------------ backup & export

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAutoBackupFolder), nameof(AutoBackupDescription))]
    public partial string? AutoBackupFolder { get; set; }

    [ObservableProperty] public partial decimal? AutoBackupKeep { get; set; }
    [ObservableProperty] public partial string? BackupStatus { get; set; }

    public bool HasAutoBackupFolder => AutoBackupFolder is not null;

    public string AutoBackupDescription => AutoBackupFolder is null
        ? "Off. Pick a folder, e.g. one synced by OneDrive, Dropbox or Google Drive, for an encrypted backup every time you sign out."
        : $"An encrypted backup is saved to this folder every time you sign out or close xJournal.";

    partial void OnAutoBackupKeepChanged(decimal? value)
    {
        if (_loading || value is null) return;
        _journal.Settings.AutoBackupKeep = (int)value.Value;
    }

    [RelayCommand]
    private async Task ChooseBackupFolderAsync()
    {
        var folder = await _dialogs.PickFolderAsync("Folder for automatic backups");
        if (folder is null) return;
        _journal.Settings.AutoBackupFolder = folder;
        AutoBackupFolder = folder;
        BackupStatus = null;
    }

    [RelayCommand]
    private void TurnOffAutoBackup()
    {
        _journal.Settings.AutoBackupFolder = null;
        AutoBackupFolder = null;
        BackupStatus = null;
    }

    [RelayCommand]
    private void BackUpNow()
    {
        try
        {
            var path = JournalTransfer.RunAutoBackup(_journal);
            BackupStatus = path is null ? null : $"Backed up to {Path.GetFileName(path)}";
        }
        catch (JournalException ex)
        {
            BackupStatus = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ExportBackupAsync()
    {
        var options = new ExportBackupViewModel();
        var view = new Views.ExportBackupView { DataContext = options };
        if (!await _dialogs.ShowContentAsync("Export encrypted backup", view, "Choose location…", "Cancel", () => Task.FromResult(options.Validate())))
            return;

        var path = await _dialogs.PickSaveFileAsync("Save encrypted backup",
            $"xJournal-{_journal.Username}-{_journal.Clock.LocalNow():yyyy-MM-dd}{AccountStore.BackupExtension}", FileTypes.Backup);
        if (path is null) return;
        try
        {
            JournalTransfer.ExportEncrypted(_journal, path, options.UseSeparatePassword ? options.Password : null);
            await _dialogs.ShowMessageAsync("Backup saved",
                $"Your journal was saved to:\n{path}\n\nTo use it on another computer, choose \"Import a journal from a backup\" on the sign-in screen.");
        }
        catch (Exception ex) when (Errors.IsExpected(ex))
        {
            await _dialogs.ShowMessageAsync("Couldn't save the backup", ex.Message);
        }
    }

    [RelayCommand]
    private async Task MergeJournalAsync()
    {
        var path = await _dialogs.PickOpenFileAsync("Merge in a journal", FileTypes.AnyJournal, FileTypes.Backup, FileTypes.JournalJson);
        if (path is null) return;

        var merge = new MergeJournalViewModel(_journal, path);
        var view = new Views.MergeJournalView { DataContext = merge };
        if (!await _dialogs.ShowContentAsync("Merge in a journal", view, "Merge", "Cancel", merge.ValidateAsync)) return;

        try
        {
            MergeStarting?.Invoke();
            var result = merge.Run();
            Merged?.Invoke();
            QuestionsChanged?.Invoke();
            var lines = string.Join("\n", MergeJournalViewModel.Describe(result).Select(l => "• " + l));
            await _dialogs.ShowMessageAsync("Journals merged", lines.Length > 0 ? lines : "Nothing needed to change.");
        }
        catch (Exception ex) when (Errors.IsExpected(ex))
        {
            await _dialogs.ShowMessageAsync("The merge didn't complete",
                "Your journal wasn't changed.\n\n" + ex.Message);
        }
    }

    [RelayCommand]
    private async Task ExportMarkdownAsync()
    {
        var options = new MarkdownExportViewModel();
        var view = new Views.MarkdownExportView { DataContext = options };
        if (!await _dialogs.ShowContentAsync("Export entries as Markdown", view, "Choose location…", "Cancel",
                () => Task.FromResult(options.Validate())))
            return;

        var stamp = _journal.Clock.LocalNow().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var destination = options.OneFilePerEntry
            ? await _dialogs.PickFolderAsync("Folder to export the entries into")
            : await _dialogs.PickSaveFileAsync("Save Markdown export", $"xJournal-{_journal.Username}-{stamp}.md", FileTypes.Markdown);
        if (destination is null || _journal.IsDisposed) return;
        if (options.OneFilePerEntry) destination = Path.Combine(destination, $"xJournal-{_journal.Username}-{stamp}");

        try
        {
            var count = Core.Export.MarkdownExporter.Export(_journal, destination, options.ToOptions());
            await _dialogs.ShowMessageAsync("Export complete",
                $"Exported {count} entr{(count == 1 ? "y" : "ies")} to:\n{destination}");
        }
        catch (Exception ex) when (Errors.IsExpected(ex))
        {
            await _dialogs.ShowMessageAsync("Couldn't export", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ExportJsonAsync()
    {
        var proceed = await _dialogs.ConfirmAsync("Export readable copy?",
            "This saves your entire journal as plain JSON that anyone can read. It is NOT encrypted. " +
            "Use it as a long-term archive and keep it somewhere safe.", "Continue");
        if (!proceed) return;
        var path = await _dialogs.PickSaveFileAsync("Save readable export",
            $"xJournal-{_journal.Username}-{_journal.Clock.LocalNow():yyyy-MM-dd}.json", FileTypes.JournalJson);
        if (path is null) return;
        try
        {
            JournalTransfer.ExportJson(_journal, path);
        }
        catch (Exception ex) when (Errors.IsExpected(ex))
        {
            await _dialogs.ShowMessageAsync("Couldn't save the export", ex.Message);
        }
    }

    // ------------------------------------------------------------------ password

    [ObservableProperty] public partial string CurrentPassword { get; set; } = "";
    [ObservableProperty] public partial string NewPassword { get; set; } = "";
    [ObservableProperty] public partial string ConfirmPassword { get; set; } = "";
    [ObservableProperty] public partial string? PasswordStatus { get; set; }
    [ObservableProperty] public partial bool PasswordStatusIsError { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChangePasswordCommand))]
    public partial bool IsChangingPassword { get; set; }

    [RelayCommand(CanExecute = nameof(CanChangePassword))]
    private async Task ChangePasswordAsync()
    {
        PasswordStatus = null;
        if (AccountStore.ValidateNewPassword(NewPassword, ConfirmPassword) is { } error)
        {
            SetPasswordStatus(error, isError: true);
            return;
        }

        IsChangingPassword = true;
        try
        {
            var current = CurrentPassword;
            var correct = await Task.Run(() => _journal.Database.CheckPassword(current));
            if (_journal.IsDisposed) return; // signed out meanwhile
            if (!correct)
            {
                SetPasswordStatus("Your current password is incorrect.", isError: true);
                return;
            }
            _journal.Database.ChangePassword(NewPassword);
            CurrentPassword = NewPassword = ConfirmPassword = "";
            SetPasswordStatus("Password changed. Use the new one next time you sign in.", isError: false);
        }
        catch (Exception ex)
        {
            SetPasswordStatus("Couldn't change the password: " + ex.Message, isError: true);
        }
        finally
        {
            IsChangingPassword = false;
        }
    }

    private bool CanChangePassword() => !IsChangingPassword;

    private void SetPasswordStatus(string message, bool isError)
    {
        PasswordStatusIsError = isError;
        PasswordStatus = message;
    }

    [RelayCommand]
    private void SignOut() => SignOutRequested?.Invoke();
}

public sealed partial class ExportBackupViewModel : ViewModelBase
{
    [ObservableProperty] public partial bool UseSeparatePassword { get; set; }
    [ObservableProperty] public partial string Password { get; set; } = "";
    [ObservableProperty] public partial string Confirm { get; set; } = "";
    [ObservableProperty] public partial string? Error { get; set; }

    public bool Validate()
    {
        Error = UseSeparatePassword ? AccountStore.ValidateNewPassword(Password, Confirm) : null;
        return Error is null;
    }
}
