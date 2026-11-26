using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;

namespace XJournal.App.Services;

/// <summary>File pickers and dialogs, so view models don't touch windows directly.</summary>
public interface IDialogs
{
    Task<string?> PickOpenFileAsync(string title, params FilePickerFileType[] types);
    Task<string?> PickSaveFileAsync(string title, string suggestedName, params FilePickerFileType[] types);
    Task<string?> PickFolderAsync(string title);
    Task ShowMessageAsync(string title, string message);

    /// <returns>True if the primary button was chosen.</returns>
    Task<bool> ConfirmAsync(string title, string message, string primaryText, string closeText = "Cancel", bool destructive = false);

    /// <summary>Shows arbitrary content in a dialog.</summary>
    Task<bool> ShowContentAsync(string title, object content, string primaryText, string? closeText = "Cancel", Func<bool>? canClose = null);
}

public static class FileTypes
{
    public static readonly FilePickerFileType Backup = new("xJournal backup") { Patterns = ["*.xjbackup"] };
    public static readonly FilePickerFileType JournalJson = new("xJournal export (JSON)") { Patterns = ["*.json"] };
    public static readonly FilePickerFileType AnyJournal = new("xJournal backup or export") { Patterns = ["*.xjbackup", "*.json"] };
    public static readonly FilePickerFileType QuestionJson = new("Question file (JSON)") { Patterns = ["*.json"] };
}

public sealed class WindowDialogs(Window window) : IDialogs
{
    public async Task<string?> PickOpenFileAsync(string title, params FilePickerFileType[] types)
    {
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [.. types, FilePickerFileTypes.All],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName, params FilePickerFileType[] types)
    {
        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = types,
            DefaultExtension = types.FirstOrDefault()?.Patterns?.FirstOrDefault()?.TrimStart('*'),
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public Task ShowMessageAsync(string title, string message) =>
        ShowContentAsync(title, Text(message), "OK", closeText: null);

    public Task<bool> ConfirmAsync(string title, string message, string primaryText, string closeText = "Cancel", bool destructive = false) =>
        ShowContentAsync(title, Text(message), primaryText, closeText);

    public async Task<bool> ShowContentAsync(string title, object content, string primaryText, string? closeText = "Cancel", Func<bool>? canClose = null)
    {
        var dialog = new FAContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText ?? "",
            DefaultButton = FAContentDialogButton.Primary,
        };
        if (canClose is not null)
        {
            dialog.PrimaryButtonClick += (_, args) => args.Cancel = !canClose();
        }
        var result = await dialog.ShowAsync(window);
        return result == FAContentDialogResult.Primary;
    }

    private static TextBlock Text(string message) => new()
    {
        Text = message,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 420,
        HorizontalAlignment = HorizontalAlignment.Left,
    };
}
