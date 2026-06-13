namespace LauncherTF2.Models;

/// <summary>
/// Launcher-specific configuration (log level, tray behavior, theming, etc.)
/// stored separately from TF2 game settings. Existing users with a
/// launcher_config.json on disk keep whatever they set previously — JSON properties
/// that exist in the file override these C# defaults during deserialisation.
/// </summary>
public class LauncherConfig
{
    public bool EnableDebugLog { get; set; }
    public string? LogLevel { get; set; }
    public bool AutoClearLogs { get; set; }
    public bool MinimizeToTrayOnLaunch { get; set; } = false;
    public bool CloseToTray { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;

    // — Personalization —
    /// <summary>Theme ID picked in the Personalization section. Empty / unknown ID falls back to the default theme.</summary>
    public string? SelectedThemeId { get; set; }

    // — Casual Preloader integration —
    /// <summary>
    /// Optional override for locating the preloader engine: either a path to
    /// preloader-cli.exe or to an engine directory containing cli\main.py.
    /// Empty/null = automatic discovery (packaged folder, then dev checkout).
    /// </summary>
    public string? PreloaderCliPath { get; set; }
}
