namespace Eternal.Core.Games;

/// <summary>What the launcher offers for a game. A part only shows once it has been checked against that game's own files.</summary>
[Flags]
public enum GameFeatures
{
    None = 0,
    Launch = 1 << 0,
    Mods = 1 << 1,
    GameBanana = 1 << 2,
    Settings = 1 << 3,
    Servers = 1 << 4,
    News = 1 << 5,
    Screenshots = 1 << 6,
    Backpack = 1 << 7,
    HitKillSounds = 1 << 8,
    SeeThroughWeapons = 1 << 9,
    All = (1 << 10) - 1,
}

/// <summary>
/// One game the launcher can manage: Team Fortress 2 or a mod built on it that Steam
/// installs as its own app. Everything that differs between them lives here.
/// </summary>
public sealed record GameDefinition(
    string Id,
    string Name,
    int AppId,
    string InstallDir,
    string ModDir,
    int GameBananaId,
    string EngineName,
    GameFeatures Features)
{
    /// <summary>What the game is called in a sentence.</summary>
    public required string ShortName { get; init; }

    /// <summary>The picture on the game's Steam page, shown for news that bring none of their own.</summary>
    public required string HeaderArt { get; init; }

    /// <summary>The icon Steam shows for the game, used until the game is installed and its own icon can be read.</summary>
    public required string SteamIcon { get; init; }

    /// <summary>The icon the game ships in its own folder.</summary>
    public string IconFile(string gameFolder) => Path.Combine(gameFolder, "resource", "game.ico");

    public IReadOnlyList<string> WindowsProcesses { get; init; } = [];

    public IReadOnlyList<string> LinuxProcesses { get; init; } = [];

    /// <summary>Settings whose cvars the game's own binaries don't have. They are hidden and never written.</summary>
    public IReadOnlySet<string> MissingSettings { get; init; } = new HashSet<string>();

    public bool Supports(Settings.SettingDefinition setting) =>
        !MissingSettings.Contains(setting.Id)
        && (setting.Id != Settings.SettingsCatalog.TransparentViewmodelsId || Has(GameFeatures.SeeThroughWeapons));

    public bool Has(GameFeatures feature) => (Features & feature) == feature;

    public string StorePage => $"steam://store/{AppId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}

public static class GameCatalog
{
    public static GameDefinition Tf2 { get; } = new("tf2", "Team Fortress 2", 440, "Team Fortress 2", "tf", 297, "TF2", GameFeatures.All)
    {
        ShortName = "TF2",
        HeaderArt = "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/440/header.jpg",
        SteamIcon = "https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/440/f568912870a4684f9ec76277a1a404dda6bab213.jpg",
        WindowsProcesses = ["tf_win64", "tf"],
        LinuxProcesses = ["tf_linux64", "tf_win64.exe", "tf.exe"],
    };

    public static GameDefinition Classified { get; } = new("tf2classified", "Team Fortress 2 Classified", 3545060,
        "Team Fortress 2 Classified", "tf2classified", 5418, "TF2C",
        GameFeatures.Launch | GameFeatures.News | GameFeatures.Settings | GameFeatures.Servers | GameFeatures.Screenshots | GameFeatures.Mods | GameFeatures.GameBanana)
    {
        WindowsProcesses = ["tf2classified_win64"],
        LinuxProcesses = ["tf2classified_linux64", "tf2classified_win64.exe"],
        ShortName = "TF2 Classified",
        MissingSettings = new HashSet<string> { "HideFloatingHealth", "ScoreboardPingAsText", "ColorblindAssist" },
        HeaderArt = "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/3545060/cede55b7156e2fc893d6eef6ecbe9c6cdfe52e9b/header.jpg",
        SteamIcon = "https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/3545060/e8ad16bf68e1338627ffe9219496cade521e66be.jpg",
    };

    public static GameDefinition GoldRush { get; } = new("tf2goldrush", "Team Fortress 2: Gold Rush", 3826520,
        "Team Fortress 2 Gold Rush", "tf_goldrush", 22251, "TF2GR",
        GameFeatures.Launch | GameFeatures.News | GameFeatures.Settings | GameFeatures.Servers | GameFeatures.Screenshots | GameFeatures.Mods | GameFeatures.GameBanana)
    {
        ShortName = "Gold Rush",
        MissingSettings = new HashSet<string> { "HudPlayerClassModel", "HideFloatingHealth", "ScoreboardPingAsText", "RomeVisionSkipPrompt" },
        HeaderArt = "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/3826520/d612f93c895760e955dca73d45e569ad6621c974/header.jpg",
        SteamIcon = "https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/3826520/8e09492f0503b54ce9f718c0d950dd92f56dfac2.jpg",
        WindowsProcesses = ["tf_goldrush_win64"],
        LinuxProcesses = ["hl2_linux64", "tf_goldrush_win64.exe"],
    };

    public static IReadOnlyList<GameDefinition> All { get; } = [Tf2, Classified, GoldRush];

    public static GameDefinition Find(string? id) => All.FirstOrDefault(g => g.Id == id) ?? Tf2;
}
