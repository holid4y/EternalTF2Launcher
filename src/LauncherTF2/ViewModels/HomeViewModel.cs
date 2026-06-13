using LauncherTF2.Core;
using LauncherTF2.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;

namespace LauncherTF2.ViewModels;

/// <summary>
/// Backs the Home tab. Surfaces three pieces of state:
///   1. A friendly greeting (time-of-day + Windows username)
///   2. Quick "is the launcher healthy?" indicators (TF2 install / Steam API / GameBanana API / mod count)
///   3. Live news + GameBanana feeds (existing).
/// Quick-action commands open the relevant folders or trigger short utilities.
/// </summary>
public class HomeViewModel : ViewModelBase
{
    private bool _isLoading;
    private string _greeting = string.Empty;
    private string _userName = string.Empty;
    private string _tagline = "Optimized and ready when you are.";
    private bool _tf2Detected;
    private bool _steamApiDetected;
    private bool _gamebananaApiDetected;
    private bool _autoexecDetected;
    private int _modsCount;
    private bool _updateAvailable;
    private string _updateVersion = string.Empty;
    private string _updateUrl = string.Empty;

    public ObservableCollection<NewsItem> NewsItems { get; } = new();
    public ObservableCollection<NewModItem> NewMods { get; } = new();

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string Greeting
    {
        get => _greeting;
        private set => SetProperty(ref _greeting, value);
    }

    public string UserName
    {
        get => _userName;
        private set => SetProperty(ref _userName, value);
    }

    public string Tagline
    {
        get => _tagline;
        private set => SetProperty(ref _tagline, value);
    }

    public bool Tf2Detected
    {
        get => _tf2Detected;
        private set => SetProperty(ref _tf2Detected, value);
    }

    public bool SteamApiDetected
    {
        get => _steamApiDetected;
        private set => SetProperty(ref _steamApiDetected, value);
    }

    public bool GamebananaApiDetected
    {
        get => _gamebananaApiDetected;
        private set => SetProperty(ref _gamebananaApiDetected, value);
    }

    public bool AutoexecDetected
    {
        get => _autoexecDetected;
        private set => SetProperty(ref _autoexecDetected, value);
    }

    public int ModsCount
    {
        get => _modsCount;
        private set => SetProperty(ref _modsCount, value);
    }

    public bool UpdateAvailable
    {
        get => _updateAvailable;
        private set => SetProperty(ref _updateAvailable, value);
    }

    public string UpdateVersion
    {
        get => _updateVersion;
        private set => SetProperty(ref _updateVersion, value);
    }

    public string UpdateUrl
    {
        get => _updateUrl;
        private set => SetProperty(ref _updateUrl, value);
    }

    public ICommand PlayCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand OpenUrlCommand { get; }
    public ICommand OpenTf2FolderCommand { get; }
    public ICommand OpenCustomFolderCommand { get; }
    public ICommand OpenAutoexecCommand { get; }
    public ICommand BackupConfigCommand { get; }
    public ICommand OpenUpdateCommand { get; }
    public ICommand DismissUpdateCommand { get; }

    public HomeViewModel()
    {
        PlayCommand = new RelayCommand(_ => ServiceLocator.Game.LaunchTF2());
        RefreshCommand = new AsyncRelayCommand(_ => LoadFeedAsync(forceRefresh: true));
        OpenUrlCommand = new RelayCommand(OpenUrl);
        OpenTf2FolderCommand = new RelayCommand(_ => OpenFolder(GetTfPath()));
        OpenCustomFolderCommand = new RelayCommand(_ => OpenFolder(Path.Combine(GetTfPath(), "custom")));
        OpenAutoexecCommand = new RelayCommand(_ => OpenAutoexec());
        BackupConfigCommand = new RelayCommand(_ => BackupConfig());
        OpenUpdateCommand = new RelayCommand(_ => OpenUrl(UpdateUrl));
        DismissUpdateCommand = new RelayCommand(_ => UpdateAvailable = false);

        // Preloader pipeline progress (validate → build → install → launch)
        // surfaces through the tagline under the greeting. Scalar INPC updates
        // are safe from background threads in WPF.
        ServiceLocator.Game.LaunchProgress += OnLaunchProgress;

        BuildGreeting();
        RefreshSystemStatus();

        _ = LoadFeedAsync(forceRefresh: false);
        _ = CheckForUpdateAsync();
    }

    private void OnLaunchProgress(int percent, string message)
    {
        Tagline = percent is > 0 and < 100
            ? $"{message} ({percent}%)"
            : message;
    }

    /// <summary>
    /// Re-evaluates the three status indicators. Called once during construction;
    /// could also be wired to a refresh button if we ever expose one.
    /// </summary>
    public void RefreshSystemStatus()
    {
        var tfPath = GetTfPath();
        Tf2Detected = !string.IsNullOrWhiteSpace(tfPath) && Directory.Exists(tfPath);
        AutoexecDetected = Tf2Detected && File.Exists(Path.Combine(tfPath, "cfg", "autoexec.cfg"));
        ModsCount = CountMods(tfPath);
        Tagline = Tf2Detected
            ? "Optimized and ready when you are."
            : "TF2 path not configured — set it up in Settings.";
    }

    private static int CountMods(string tfPath)
    {
        try
        {
            var customDir = Path.Combine(tfPath, "custom");
            if (!Directory.Exists(customDir)) return 0;
            return Directory.EnumerateDirectories(customDir)
                .Count(d => !Path.GetFileName(d).Equals("disabled", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return 0;
        }
    }

    private void BuildGreeting()
    {
        var hour = DateTime.Now.Hour;
        Greeting = hour switch
        {
            < 5 => "Still up",
            < 12 => "Good morning",
            < 18 => "Good afternoon",
            _ => "Good evening"
        };

        // Prefer the Steam display name (what the player actually goes by);
        // the Windows account name is only a fallback.
        var persona = ServiceLocator.SteamDetection.GetPersonaName();
        UserName = !string.IsNullOrWhiteSpace(persona)
            ? persona
            : Capitalize(Environment.UserName);
    }

    private static string Capitalize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        return name.Length == 1
            ? name.ToUpperInvariant()
            : char.ToUpperInvariant(name[0]) + name[1..];
    }

    private static string GetTfPath() => ServiceLocator.Settings.GetSettings().SteamPath ?? string.Empty;

    private async Task CheckForUpdateAsync()
    {
        var info = await ServiceLocator.UpdateChecker.CheckAsync();
        if (info == null) return;

        UpdateVersion = info.LatestVersion;
        UpdateUrl = info.HtmlUrl;
        UpdateAvailable = true;
    }

    private async Task LoadFeedAsync(bool forceRefresh)
    {
        if (IsLoading) return;
        IsLoading = true;

        try
        {
            if (forceRefresh) ServiceLocator.HomeFeed.InvalidateCache();

            var newsTask = ServiceLocator.HomeFeed.GetSteamNewsAsync(5);
            var modsTask = ServiceLocator.HomeFeed.GetNewModsAsync(8);
            await Task.WhenAll(newsTask, modsTask);

            ReplaceItems(NewsItems, await newsTask);
            ReplaceItems(NewMods, await modsTask);

            Logger.LogInfo($"[Home] Loaded {NewsItems.Count} news, {NewMods.Count} mods");
        }
        catch (Exception ex)
        {
            Logger.LogWarning("[Home] Feed load failed", ex);
        }
        finally
        {
            SteamApiDetected = ServiceLocator.HomeFeed.LastSteamNewsLoadSucceeded;
            GamebananaApiDetected = ServiceLocator.HomeFeed.LastGameBananaLoadSucceeded;
            IsLoading = false;
        }
    }

    private static void ReplaceItems<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    private static void OpenUrl(object? parameter)
    {
        if (parameter is not string url || string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[Home] Could not open URL '{url}'", ex);
        }
    }

    private static void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            // The stored TF2 path comes from Steam's registry with forward
            // slashes; explorer.exe rejects those and opens Documents instead.
            path = Path.GetFullPath(path);

            if (!Directory.Exists(path))
            {
                Logger.LogWarning($"[Home] Folder doesn't exist: {path}");
                return;
            }
            // Use explorer.exe explicitly — Process.Start(dirPath, UseShellExecute)
            // can misfire on some Windows configurations (e.g. launching Steam when
            // the path resolves to a game directory with special handlers).
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"")
            {
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[Home] Could not open folder '{path}'", ex);
        }
    }

    private static void OpenAutoexec()
    {
        try
        {
            var tfPath = GetTfPath();
            if (string.IsNullOrWhiteSpace(tfPath)) return;

            var cfgDir = Path.Combine(tfPath, "cfg");
            if (!Directory.Exists(cfgDir)) Directory.CreateDirectory(cfgDir);

            var autoexec = Path.Combine(cfgDir, "autoexec.cfg");
            if (!File.Exists(autoexec)) File.WriteAllText(autoexec, "// autoexec.cfg\n");

            Process.Start(new ProcessStartInfo(autoexec) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.LogWarning("[Home] Could not open autoexec.cfg", ex);
        }
    }

    private static void BackupConfig()
    {
        try
        {
            var tfPath = GetTfPath();
            if (string.IsNullOrWhiteSpace(tfPath))
            {
                Views.MessageDialog.ShowError("Backup Failed", "TF2 path is not configured. Set it up in Settings first.");
                return;
            }

            var src = Path.Combine(tfPath, "cfg", "autoexec.cfg");
            if (!File.Exists(src))
            {
                Views.MessageDialog.ShowError("Backup Failed", $"No autoexec.cfg found at:\n{src}");
                return;
            }

            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var dst   = Path.Combine(tfPath, "cfg", $"autoexec_backup_{stamp}.cfg");
            File.Copy(src, dst);

            Logger.LogInfo($"[Home] autoexec.cfg backed up to: {dst}");

            // Open the backup in the user's preferred text editor — no confirmation
            // dialog needed. The user can read, verify, and close it naturally.
            Process.Start(new ProcessStartInfo(dst) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.LogWarning("[Home] Backup failed", ex);
            Views.MessageDialog.ShowError("Backup Failed", $"Could not create backup:\n{ex.Message}");
        }
    }
}
