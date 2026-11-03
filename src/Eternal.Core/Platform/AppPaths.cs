namespace Eternal.Core.Platform;

public sealed class AppPaths
{
    public const string WindowsFolder = "Eternal TF2 Launcher";
    public const string LinuxFolder = "eternal-launcher";
    public const string PortableMarker = "portable.txt";

    public AppPaths(string configDir, string dataDir, string cacheDir, bool portable = false)
    {
        ConfigDir = configDir;
        DataDir = dataDir;
        CacheDir = cacheDir;
        IsPortable = portable;
    }

    public string ConfigDir { get; }
    public string DataDir { get; }
    public string CacheDir { get; }
    public bool IsPortable { get; }

    public string LogsDir => Path.Combine(DataDir, "logs");
    public string EngineDataDir => Path.Combine(DataDir, "engine");
    public string EngineCacheDir => Path.Combine(CacheDir, "engine");
    public string ThumbnailsDir => Path.Combine(DataDir, "thumbnails");
    public string ProfilesDir => Path.Combine(ConfigDir, "profiles");
    public string ImportStagingDir => Path.Combine(CacheDir, "import");
    public string ImageCacheDir => Path.Combine(CacheDir, "images");

    public string GameConfigFile => Path.Combine(ConfigDir, "game.json");
    public string LauncherSettingsFile => Path.Combine(ConfigDir, "launcher.json");
    public string ModStateFile => Path.Combine(DataDir, "mods.json");
    public string PreloadStateFile => Path.Combine(DataDir, "preload-state.json");
    public string SteamWritesFile => Path.Combine(DataDir, "steam-writes.json");
    public string SteamBackupsDir => Path.Combine(DataDir, "steam-backups");
    public string ServersFile => Path.Combine(ConfigDir, "servers.json");
    public string ScreenshotOriginalsDir => Path.Combine(DataDir, "screenshot-originals");

    public static AppPaths Detect(string? baseDirectory = null)
    {
        var exeDir = baseDirectory ?? AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(exeDir, PortableMarker)))
        {
            var root = Path.Combine(exeDir, "userdata");
            return new AppPaths(Path.Combine(root, "config"), Path.Combine(root, "data"), Path.Combine(root, "cache"), portable: true);
        }

        if (OperatingSystem.IsWindows())
        {
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return new AppPaths(
                Path.Combine(roaming, WindowsFolder),
                Path.Combine(local, WindowsFolder),
                Path.Combine(local, WindowsFolder, "cache"));
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string Xdg(string variable, string fallback) =>
            Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value && Path.IsPathRooted(value)
                ? value
                : Path.Combine(home, fallback);

        return new AppPaths(
            Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), LinuxFolder),
            Path.Combine(Xdg("XDG_DATA_HOME", Path.Combine(".local", "share")), LinuxFolder),
            Path.Combine(Xdg("XDG_CACHE_HOME", ".cache"), LinuxFolder));
    }

    public void EnsureCreated()
    {
        foreach (var dir in new[] { ConfigDir, DataDir, CacheDir, LogsDir, EngineDataDir, ThumbnailsDir, ProfilesDir })
            Directory.CreateDirectory(dir);
    }

    public static bool IsInside(string root, string candidate)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var fullCandidate = Path.GetFullPath(candidate);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return fullCandidate.StartsWith(fullRoot, comparison);
    }
}
