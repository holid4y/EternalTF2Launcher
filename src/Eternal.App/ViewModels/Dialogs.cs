using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Eternal.App.ViewModels;

public interface IDialogs
{
    Task<bool> ConfirmAsync(string title, string message, string confirm = "OK", string cancel = "Cancel", bool danger = false);
    Task AlertAsync(string title, string message);
    Task<string?> PromptAsync(string title, string message, string initial = "", string confirm = "Save");
}

public sealed partial class DialogViewModel : ObservableObject
{
    private readonly TaskCompletionSource<bool> _result = new();

    public required string Title { get; init; }
    public required string Message { get; init; }
    public string ConfirmText { get; init; } = "OK";
    public string? CancelText { get; init; }
    public bool Danger { get; init; }
    public bool HasInput { get; init; }

    [ObservableProperty] private string _input = "";

    public Task<bool> Result => _result.Task;

    [RelayCommand]
    private void Confirm() => _result.TrySetResult(true);

    [RelayCommand]
    private void Cancel() => _result.TrySetResult(false);
}

/// <summary>Native file and folder pickers through the main window.</summary>
public static class Pickers
{
    private static TopLevel? Top => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public static async Task<IReadOnlyList<string>> OpenFilesAsync(string title, bool multiple, string filterName, params string[] patterns)
    {
        if (Top is not { } top)
            return [];
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = multiple,
            FileTypeFilter = patterns.Length == 0 ? null : [new FilePickerFileType(filterName) { Patterns = patterns }],
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
    }

    public static async Task<string?> OpenFolderAsync(string title, string? start = null)
    {
        if (Top is not { } top)
            return null;
        var startFolder = start is not null && Directory.Exists(start) ? await top.StorageProvider.TryGetFolderFromPathAsync(start) : null;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, SuggestedStartLocation = startFolder });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public static async Task<string?> SaveFileAsync(string title, string suggestedName, string extension)
    {
        if (Top is not { } top)
            return null;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(extension.ToUpperInvariant()) { Patterns = [$"*.{extension}"] }],
        });
        return file?.TryGetLocalPath();
    }

    public static async Task CopyAsync(string text)
    {
        if (Top?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }
}
