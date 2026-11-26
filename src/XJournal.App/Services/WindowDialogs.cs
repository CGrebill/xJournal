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
    Task<bool> ConfirmAsync(string title, string message, string primaryText, string closeText = "Cancel");

    /// <summary>
    /// Shows arbitrary content in a dialog. <paramref name="canClose"/> runs when the primary button is
    /// pressed; returning false keeps the dialog open (e.g. to show a validation message).
    /// </summary>
    Task<bool> ShowContentAsync(string title, object content, string primaryText, string? closeText = "Cancel",
        Func<Task<bool>>? canClose = null);
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
    // A window can show only one content dialog at a time; queue any others.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, SemaphoreSlim> Gates = new();

    private SemaphoreSlim Gate => Gates.GetValue(window, _ => new SemaphoreSlim(1, 1));

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
            DefaultExtension = types.Length > 0 && types[0].Patterns is [var pattern, ..] ? pattern.TrimStart('*') : null,
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

    public Task<bool> ConfirmAsync(string title, string message, string primaryText, string closeText = "Cancel") =>
        ShowContentAsync(title, Text(message), primaryText, closeText);

    public async Task<bool> ShowContentAsync(string title, object content, string primaryText, string? closeText = "Cancel",
        Func<Task<bool>>? canClose = null)
    {
        await Gate.WaitAsync();
        try
        {
            return await ShowCoreAsync(title, content, primaryText, closeText, canClose);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<bool> ShowCoreAsync(string title, object content, string primaryText, string? closeText,
        Func<Task<bool>>? canClose)
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
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                var deferral = args.GetDeferral();
                try
                {
                    args.Cancel = !await canClose();
                }
                finally
                {
                    deferral.Complete();
                }
            };
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
