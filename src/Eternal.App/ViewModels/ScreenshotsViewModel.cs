using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Eternal.App.Services;
using Eternal.Core.Logging;
using Eternal.Core.Platform;
using Eternal.Core.Steam;

namespace Eternal.App.ViewModels;

public sealed partial class ShotItem(SteamScreenshot shot) : ObservableObject
{
    private static readonly SemaphoreSlim Decoding = new(3, 3);
    private DateTime _loadedStamp;
    private bool _loading;
    private bool _wanted;

    public SteamScreenshot Shot { get; private set; } = shot;

    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isBackground;

    public string Taken => Shot.Taken.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);
    public string Size => Shot.Width > 0 ? $"{Shot.Width}×{Shot.Height}" : "";
    public bool Uploaded => Shot.PublishedFileId is not null;
    public string State => Uploaded ? "On your Steam profile" : "Only on this PC";

    internal void Update(SteamScreenshot shot)
    {
        Shot = shot;
        OnPropertyChanged(string.Empty);
        if (Thumbnail is not null && Stamp() != _loadedStamp)
            _ = LoadThumbnailAsync(force: true);
    }

    private DateTime Stamp()
    {
        try
        {
            return File.GetLastWriteTimeUtc(Shot.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>Frees the picture once its card has scrolled away, so a big library doesn't sit in memory.</summary>
    public void ReleaseThumbnail()
    {
        _wanted = false;
        if (IsSelected || Thumbnail is not { } old)
            return;
        Thumbnail = null;
        old.Dispose();
    }

    public async Task LoadThumbnailAsync(bool force = false)
    {
        _wanted = true;
        if (_loading || (Thumbnail is not null && !force))
            return;
        _loading = true;
        try
        {
            await Decoding.WaitAsync();
            var path = Shot.FullPath;
            var stamp = Stamp();
            var bitmap = await Task.Run(() =>
            {
                try
                {
                    using var stream = File.OpenRead(path);
                    return Bitmap.DecodeToWidth(stream, 520, BitmapInterpolationMode.MediumQuality);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                {
                    return null;
                }
            });
            _loadedStamp = stamp;
            if (!_wanted && !IsSelected)
            {
                bitmap?.Dispose();
                return;
            }
            var old = Thumbnail;
            Thumbnail = bitmap;
            old?.Dispose();
        }
        finally
        {
            Decoding.Release();
            _loading = false;
        }
    }
}

public sealed partial class ScreenshotsViewModel : PageViewModel
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;
    private readonly ScreenshotOriginals _originals;
    private readonly DispatcherTimer _reload;
    private FileSystemWatcher? _folderWatch;
    private FileSystemWatcher? _listWatch;
    private string? _userDir;

    public ScreenshotsViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
        _originals = new ScreenshotOriginals(services.Paths.ScreenshotOriginalsDir);
        View = new ViewOptions(services.Launcher, s => s.ScreenshotView, textHeight: 0);
        _reload = new DispatcherTimer(TimeSpan.FromMilliseconds(800), DispatcherPriority.Background, (_, _) =>
        {
            _reload!.Stop();
            _ = LoadAsync();
        });
    }

    public ViewOptions View { get; }
    public SteamFilesViewModel SteamFiles => _main.SteamFiles;
    public BulkCollection<ShotItem> Items { get; } = [];

    [ObservableProperty] private ShotItem? _selected;
    [ObservableProperty] private bool _loading;
    [ObservableProperty] private bool _noAccount;
    [ObservableProperty] private ScreenshotEditorViewModel? _editor;
    [ObservableProperty] private bool _busy;

    public bool HasSelection => Selected is not null;
    public bool IsEmpty => !Loading && !NoAccount && Items.Count == 0;
    public bool Editing => Editor is not null;
    public bool Browsing => Editor is null;
    public bool CanRevert => Selected is { } s && _originals.HasOriginal(s.Shot);
    public string Summary => Items.Count switch
    {
        0 => "",
        1 => "1 screenshot",
        var n => $"{n} screenshots, {Items.Count(i => i.Uploaded)} on your profile",
    };

    partial void OnSelectedChanged(ShotItem? oldValue, ShotItem? newValue)
    {
        if (oldValue is not null)
            oldValue.IsSelected = false;
        if (newValue is not null)
        {
            newValue.IsSelected = true;
            _ = newValue.LoadThumbnailAsync();
        }
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanRevert));
    }

    partial void OnLoadingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    partial void OnEditorChanged(ScreenshotEditorViewModel? value)
    {
        OnPropertyChanged(nameof(Editing));
        OnPropertyChanged(nameof(Browsing));
    }

    protected override void OnFirstActivation() => _ = LoadAsync();

    public string EmptyTitle => $"No {_services.GameInfo.ShortName} screenshots yet";
    public string UploadHint => $"Opens Steam's uploader for {_services.GameInfo.ShortName}";

    public override void Close()
    {
        _reload.Stop();
        _folderWatch?.Dispose();
        _listWatch?.Dispose();
    }

    protected override void OnReturn() => _ = LoadAsync();

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private void SetBackground()
    {
        if (Selected is not { } item)
            return;
        _services.Launcher.Update(s =>
        {
            s.Background = Eternal.Core.BackgroundMode.Screenshot;
            s.BackgroundScreenshot = item.Shot.FullPath;
        });
        _main.Theme.Apply(_services.Launcher.Settings);
        RefreshBackgroundBadges();
        _main.Notify("This screenshot is now the launcher background. Change the mode in Launcher → Look.");
    }

    private void RefreshBackgroundBadges()
    {
        var current = _services.Launcher.Settings.Background == Eternal.Core.BackgroundMode.Screenshot
            ? _services.Launcher.Settings.BackgroundScreenshot
            : null;
        foreach (var item in Items)
            item.IsBackground = current is not null && item.Shot.FullPath == current;
    }

    private async Task LoadAsync()
    {
        var userDir = _services.SteamUserDir;
        NoAccount = userDir is null;
        if (userDir is null)
        {
            Items.ReplaceAll([]);
            Selected = null;
            OnPropertyChanged(nameof(IsEmpty));
            return;
        }
        Watch(userDir);
        Loading = Items.Count == 0;
        try
        {
            var shots = await Task.Run(() => SteamScreenshots.Load(userDir, _services.GameInfo.AppId));
            var known = Items.ToDictionary(i => i.Shot.FullPath);
            var next = shots.Select(shot =>
            {
                if (known.TryGetValue(shot.FullPath, out var item))
                {
                    item.Update(shot);
                    return item;
                }
                return new ShotItem(shot);
            }).ToList();
            Items.ReplaceAll(next);
            RefreshBackgroundBadges();
            if (Selected is not null && !next.Contains(Selected))
                Selected = null;
        }
        finally
        {
            Loading = false;
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(CanRevert));
        }
    }

    private void Watch(string userDir)
    {
        if (_userDir == userDir)
            return;
        _userDir = userDir;
        _folderWatch?.Dispose();
        _listWatch?.Dispose();
        _folderWatch = _listWatch = null;
        try
        {
            var folder = SteamScreenshots.Folder(userDir, _services.GameInfo.AppId);
            if (Directory.Exists(folder))
                _folderWatch = StartWatch(folder, "*.*");
            var list = Path.Combine(userDir, "760");
            if (Directory.Exists(list))
                _listWatch = StartWatch(list, "screenshots.vdf");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        {
            Log.Debug($"Not watching screenshots: {ex.Message}");
        }
    }

    private FileSystemWatcher StartWatch(string folder, string filter)
    {
        var watcher = new FileSystemWatcher(folder, filter) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
        FileSystemEventHandler changed = (_, _) => Dispatcher.UIThread.Post(() => { _reload.Stop(); _reload.Start(); });
        watcher.Created += changed;
        watcher.Deleted += changed;
        watcher.Changed += changed;
        watcher.Renamed += (s, e) => changed(s, e);
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    [RelayCommand]
    private void Select(ShotItem? item) => Selected = item;

    [RelayCommand]
    private async Task EditAsync()
    {
        if (Selected is not { } item || Busy)
            return;
        Busy = true;
        try
        {
            var editor = await ScreenshotEditorViewModel.OpenAsync(item.Shot, _main, SaveAsync, () => Editor = null);
            if (editor is null)
            {
                await _main.AlertAsync("Can't open that screenshot", "The file couldn't be read as a picture.");
                return;
            }
            Editor = editor;
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task<bool> SaveAsync(SteamScreenshot shot, ImageEdit edit)
    {
        try
        {
            var (width, height) = await Task.Run(() =>
            {
                using var source = ThumbnailMaker.Decode(shot.FullPath) ?? throw new InvalidDataException("The screenshot couldn't be read.");
                using var result = ScreenshotRenderer.Render(source, edit);
                var png = shot.FullPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
                _originals.SaveEdited(shot, ScreenshotRenderer.Encode(result, png), ScreenshotRenderer.Thumbnail(result, SteamScreenshots.ThumbnailWidth));
                return (result.Width, result.Height);
            });
            if (shot.Width > 0 && (width != shot.Width || height != shot.Height))
                QueueResize(shot, width, height);
            _main.Notify("Saved. The original is kept, so Revert brings it back. Steam uploads the new version.");
            Editor = null;
            await LoadAsync();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warn($"Saving the edited screenshot failed: {ex.Message}");
            await _main.AlertAsync("Couldn't save", ex.Message);
            return false;
        }
    }

    private void QueueResize(SteamScreenshot shot, int width, int height)
    {
        try
        {
            _services.SteamWrites.Add(new SteamWrite { Kind = SteamWriteKind.ResizeScreenshot, UserDir = shot.UserDir, Target = shot.RecordedName, Width = width, Height = height });
        }
        catch (ArgumentException ex)
        {
            Log.Warn($"Not recording the new size with Steam: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RevertAsync()
    {
        if (Selected is not { } item || !await _main.ConfirmAsync("Revert to the original", "Put back the screenshot as it was taken? The edits are lost.", "Revert"))
            return;
        try
        {
            var size = await Task.Run(() =>
            {
                if (!_originals.Restore(item.Shot))
                    return ((int, int)?)null;
                using var source = ThumbnailMaker.Decode(item.Shot.FullPath);
                if (source is null)
                    return null;
                Eternal.Core.Persistence.AtomicFile.WriteAllBytes(item.Shot.ThumbnailPath, ScreenshotRenderer.Thumbnail(source, SteamScreenshots.ThumbnailWidth), keepBackup: false);
                return (source.Width, source.Height);
            });
            if (size is { } s && item.Shot.Width > 0 && (s.Item1 != item.Shot.Width || s.Item2 != item.Shot.Height))
                QueueResize(item.Shot, s.Item1, s.Item2);
            await LoadAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _main.AlertAsync("Couldn't revert", ex.Message);
        }
    }

    [RelayCommand]
    private async Task CopyImageAsync()
    {
        if (Selected is not { } item)
            return;
        if (await Pickers.CopyImageAsync(item.Shot.FullPath))
            _main.Notify("Copied. Paste it in a chat or an image editor.");
        else
            _main.Notify("The picture couldn't be copied.", isError: true);
    }

    [RelayCommand]
    private void Upload()
    {
        if (!SteamProcess.IsRunning())
        {
            _main.Notify("Start Steam first: its uploader puts the screenshot on your profile.", isError: true);
            return;
        }
        Shell.OpenSteam($"steam://open/screenshots/{_services.GameInfo.AppId}");
        _main.Notify("Steam's screenshot window is open. Pick the screenshot there and press Upload. Its link shows up here once Steam has it.");
    }

    [RelayCommand]
    private async Task CopyLinkAsync()
    {
        if (Selected?.Shot.ShareUrl is not { } url)
            return;
        await Pickers.CopyAsync(url);
        _main.Notify("Link copied.");
    }

    [RelayCommand]
    private void OpenLink() => Shell.OpenUrl(Selected?.Shot.ShareUrl);

    [RelayCommand]
    private void ShowInFolder()
    {
        if (Selected is { } item)
            Shell.OpenFolder(Path.GetDirectoryName(item.Shot.FullPath));
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected is not { } item)
            return;
        var note = item.Uploaded ? " The copy on your Steam profile stays; remove it from Steam if you want it gone there too." : "";
        if (!await _main.ConfirmAsync("Delete screenshot", $"Move this screenshot to the trash?{note}", "Move to trash", danger: true))
            return;
        try
        {
            await Task.Run(() =>
            {
                Trash.Move(item.Shot.FullPath);
                if (File.Exists(item.Shot.ThumbnailPath))
                    Trash.Move(item.Shot.ThumbnailPath);
                _originals.Forget(item.Shot);
            });
            _services.SteamWrites.Add(new SteamWrite { Kind = SteamWriteKind.RemoveScreenshot, UserDir = item.Shot.UserDir, Target = item.Shot.RecordedName });
            Selected = null;
            await LoadAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await _main.AlertAsync("Couldn't delete", ex.Message);
        }
    }
}
