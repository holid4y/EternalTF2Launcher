using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LauncherTF2.Core;

namespace LauncherTF2.Services;

/// <summary>
/// Launches TF2 the normal, risk-free way: through the Steam protocol with the
/// configured launch arguments. There is no VAC bypass, no sv_pure patcher, and
/// no self-elevation — the launcher runs entirely unelevated.
///
/// Mod support comes from the Casual Preloader: mods are baked into tf/custom
/// VPKs ahead of time via the Mods tab ("Apply Preload"), so they load
/// legitimately when the game starts. Launching never modifies the game or the
/// Steam client.
/// </summary>
public class GameService
{
    private readonly SettingsService _settingsService;
    private readonly IPreloaderService? _preloader;
    private int _launchInProgress;

    private static readonly TimeSpan Tf2ProcessTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan PollInterval       = TimeSpan.FromMilliseconds(500);
    // A preload bake can run QuickPrecache on large mod sets; give it room to
    // finish before we launch rather than starting TF2 on a half-built tf/custom.
    private static readonly TimeSpan PreloadWaitCap     = TimeSpan.FromMinutes(10);

    public GameService(SettingsService settingsService, IPreloaderService? preloader = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _preloader = preloader;
    }

    /// <summary>
    /// Raised during launch (percent, message). May fire from a background
    /// thread; WPF scalar bindings handle that fine.
    /// </summary>
    public event Action<int, string>? LaunchProgress;

    public bool LaunchTF2()
    {
        try
        {
            Logger.LogInfo("[Game] Launch requested");

            if (Interlocked.CompareExchange(ref _launchInProgress, 1, 0) != 0)
            {
                Logger.LogWarning("[Game] Launch ignored — a launch is already in progress");
                return true;
            }

            var settings = _settingsService.GetSettings();
            if (settings == null)
            {
                Logger.LogError("[Game] Cannot launch — settings are null");
                Interlocked.Exchange(ref _launchInProgress, 0);
                return false;
            }

            if (!IsSteamPathValid(settings.SteamPath))
                Logger.LogWarning($"[Game] TF2 path looks invalid: {settings.SteamPath}");

            var finalArgs = (settings.LaunchArgs ?? string.Empty).Trim();

            // Fire-and-forget; the async body releases the in-progress gate.
            _ = RunLaunchAsync(finalArgs);

            MinimizeToTray();
            return true;
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _launchInProgress, 0);
            Logger.LogError("[Game] Unexpected error during launch", ex);
            return false;
        }
    }

    private async Task RunLaunchAsync(string finalArgs)
    {
        try
        {
            if (IsProcessRunning("tf_win64"))
            {
                Logger.LogInfo("[Game] TF2 is already running — nothing to do");
                ReportProgress(100, "TF2 is already running");
                return;
            }

            // If the Mods tab is mid-bake, tf/custom is being rebuilt — wait for
            // it to finish so the game doesn't load a half-written preload.
            await WaitForPreloadIdleAsync();

            ReportProgress(10, "Launching TF2 through Steam...");
            TryLaunchThroughSteam(finalArgs);

            Logger.LogInfo($"[Game] Waiting for tf_win64 process (timeout {Tf2ProcessTimeout.TotalSeconds}s)...");
            await WaitForProcessAsync("tf_win64", Tf2ProcessTimeout);

            if (IsProcessRunning("tf_win64"))
            {
                Logger.LogInfo("[Game] TF2 process detected — launch complete");
                ReportProgress(100, "TF2 is running");
                _ = MonitorForEarlyExitAsync();
            }
            else
            {
                Logger.LogWarning($"[Game] TF2 not detected within {Tf2ProcessTimeout.TotalSeconds}s after Steam launch");
                ReportProgress(100, "Launch sent to Steam — TF2 not detected yet");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("[Game] Launch failed", ex);
            ReportProgress(0, "Launch failed");
            ShowLaunchError(ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _launchInProgress, 0);
        }
    }

    public bool ValidateGameInstallation()
    {
        try
        {
            var settings = _settingsService.GetSettings();
            if (settings == null || string.IsNullOrWhiteSpace(settings.SteamPath))
            {
                Logger.LogWarning("[Game] Cannot validate — TF2 path not configured");
                return false;
            }

            if (!Directory.Exists(settings.SteamPath))
            {
                Logger.LogWarning($"[Game] TF2 directory missing: {settings.SteamPath}");
                return false;
            }

            var tf2Exe = Path.Combine(settings.SteamPath, "tf_win64.exe");
            if (!File.Exists(tf2Exe))
            {
                Logger.LogWarning($"[Game] tf_win64.exe not found in: {settings.SteamPath}");
                return false;
            }

            Logger.LogInfo("[Game] Installation validated successfully");
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError("[Game] Validation failed", ex);
            return false;
        }
    }

    public bool IsGameRunning()
    {
        try { return IsProcessRunning("tf_win64"); }
        catch (Exception ex)
        {
            Logger.LogError("[Game] Error checking game process", ex);
            return false;
        }
    }

    private static bool TryLaunchThroughSteam(string args)
    {
        var encoded  = string.IsNullOrWhiteSpace(args) ? string.Empty : Uri.EscapeDataString(args);
        var steamUrl = string.IsNullOrWhiteSpace(encoded)
            ? "steam://rungameid/440"
            : $"steam://rungameid/440//{encoded}";

        Logger.LogInfo($"[Game] Launching TF2 — URL: {steamUrl}");
        Process.Start(new ProcessStartInfo { FileName = steamUrl, UseShellExecute = true });
        Logger.LogInfo("[Game] Launch command sent to Steam");
        return true;
    }

    private void ReportProgress(int percent, string message)
    {
        try { LaunchProgress?.Invoke(percent, message); }
        catch (Exception ex) { Logger.LogWarning($"[Game] LaunchProgress handler threw: {ex.Message}"); }
    }

    private static void ShowLaunchError(string message)
    {
        Application.Current?.Dispatcher.Invoke(() =>
            LauncherTF2.Views.MessageDialog.ShowError(
                "Launch Failed",
                $"TF2 could not be launched.\n\n{message}"));
    }

    private void MinimizeToTray()
    {
        if (!_settingsService.GetLauncherConfig().MinimizeToTrayOnLaunch)
        {
            Logger.LogDebug("[Game] MinimizeToTrayOnLaunch disabled — keeping launcher visible");
            return;
        }

        var mainWindow = Application.Current?.MainWindow;
        if (mainWindow == null)
        {
            Logger.LogWarning("[Game] Cannot minimize — MainWindow not found");
            return;
        }

        mainWindow.Hide();
        Logger.LogInfo("[Game] Launcher minimized to system tray");
    }

    private static bool IsProcessRunning(string processName)
    {
        var procs = Process.GetProcessesByName(processName);
        bool running = procs.Length > 0;
        foreach (var p in procs) p.Dispose();
        return running;
    }

    /// <summary>
    /// Blocks the launch until the preloader engine is idle (no bake in flight).
    /// Reports "Preparing your mods…" while waiting. Bounded by <see cref="PreloadWaitCap"/>
    /// so a pathologically long bake can't hang Play forever.
    /// </summary>
    private async Task WaitForPreloadIdleAsync()
    {
        if (_preloader is not { IsRunning: true }) return;

        Logger.LogInfo("[Game] A preload bake is in progress — waiting before launch");
        ReportProgress(5, "Preparing your mods...");

        var sw = Stopwatch.StartNew();
        while (_preloader.IsRunning && sw.Elapsed < PreloadWaitCap)
            await Task.Delay(PollInterval);

        if (_preloader.IsRunning)
            Logger.LogWarning($"[Game] Preload still running after {PreloadWaitCap.TotalMinutes:0} min — launching anyway");
        else
            Logger.LogInfo("[Game] Preload finished — proceeding to launch");
    }

    private static async Task WaitForProcessAsync(string processName, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (IsProcessRunning(processName)) return;
            await Task.Delay(PollInterval);
        }
    }

    // A genuine preload/precache startup crash kills TF2 during initial load —
    // within a few seconds, well under this window. Kept short so a player who
    // simply launches and quits early isn't told their game "crashed".
    private static readonly TimeSpan EarlyExitWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// After TF2 is detected, watches for it dying inside <see cref="EarlyExitWindow"/>.
    /// If it does, it's very likely a preload/precache startup crash, so we point
    /// the user at the responsible bake (timestamp + mods) and the engine log.
    /// </summary>
    private async Task MonitorForEarlyExitAsync()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < EarlyExitWindow)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                if (IsProcessRunning("tf_win64")) continue;

                Logger.LogWarning($"[Game] TF2 exited {sw.Elapsed.TotalSeconds:0}s after launch — possible startup crash");

                var bake = DescribeLastBake();
                ShowLaunchError(
                    "TF2 closed right after launching.\n\n" +
                    "If it crashed on startup, a baked mod is the likely cause — try the " +
                    "Mods tab: disable the most recently added mod (or click \"Remove Preload\") " +
                    "and launch again.\n\n" +
                    $"{bake}\n" +
                    "Details: logs/preloader.log");
                ReportProgress(0, "TF2 closed shortly after launch");
                return;
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[Game] Error monitoring TF2 early exit: {ex.Message}");
        }
    }

    /// <summary>Reads the engine's last-install manifest into a one-line summary
    /// (when the bake happened + how many mods), or a neutral fallback.</summary>
    private string DescribeLastBake()
    {
        try
        {
            var manifestPath = _preloader?.ManifestPath;
            if (manifestPath == null || !File.Exists(manifestPath))
                return "No recent preload bake on record.";

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = doc.RootElement;

            var when = "unknown time";
            if (root.TryGetProperty("timestamp", out var ts) &&
                DateTimeOffset.TryParse(ts.GetString(), out var dto))
                when = dto.ToLocalTime().ToString("g");

            int addonCount = 0;
            if (root.TryGetProperty("addons", out var addons) && addons.ValueKind == System.Text.Json.JsonValueKind.Array)
                addonCount = addons.GetArrayLength();

            return $"Last preload bake: {when} ({addonCount} mod(s)).";
        }
        catch (Exception ex)
        {
            Logger.LogDebug($"[Game] Could not read install manifest: {ex.Message}");
            return "Could not read the last preload bake record.";
        }
    }

    private static bool IsSteamPathValid(string? steamPath)
        => !string.IsNullOrWhiteSpace(steamPath) && Directory.Exists(steamPath);
}
