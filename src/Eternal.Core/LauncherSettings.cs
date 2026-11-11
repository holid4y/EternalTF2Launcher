using System.Text.Json.Serialization;
using Eternal.Core.Persistence;

namespace Eternal.Core;

public enum UiDensity { Comfortable, Compact }

public enum BackgroundMode { Screenshots, Rotate, Screenshot, Picture, None }

public enum ViewLayout { Grid, List }

public sealed class BrowseView
{
    public const double MinCardWidth = 160, MaxCardWidth = 360;

    public ViewLayout Layout { get; set; }
    public double CardWidth { get; set; } = 220;

    public bool ShowAdult { get; set; }

    internal void Clamp() => CardWidth = double.IsFinite(CardWidth) ? Math.Clamp(CardWidth, MinCardWidth, MaxCardWidth) : 220;
}

public sealed class TabPreference
{
    public string Id { get; set; } = "";
    public bool Visible { get; set; } = true;
}

public sealed class ServerFilters
{
    public string Region { get; set; } = "auto";
    public bool HasPlayers { get; set; } = true;
    public bool NotFull { get; set; }
    public bool NoPassword { get; set; }
    public bool HideFake { get; set; } = true;
    public int MaxPing { get; set; }
    public string Mode { get; set; } = "";

    internal void Clamp()
    {
        if (Region is not ("auto" or "all") && !Enum.TryParse<global::Eternal.Core.Servers.ServerRegion>(Region, out _))
            Region = "auto";
        MaxPing = Math.Clamp(MaxPing, 0, 999);
        if (Mode.Length > 40)
            Mode = "";
    }
}

/// <summary>What the launcher remembers separately for each game.</summary>
public sealed class GamePreferences
{
    public string? Path { get; set; }
    public string ThemeId { get; set; } = "mann-co";
    public string? AccentOverride { get; set; }
    public bool ReviewedGameFolder { get; set; }
    public bool ManageAutoexec { get; set; } = true;
}

public sealed class LauncherSettings : IJsonOnDeserialized
{
    private readonly GamePreferences _tf2 = new();

    public int Version { get; set; } = 2;

    /// <summary>The game the launcher is showing. <see cref="Game"/> holds what is remembered for it.</summary>
    public string SelectedGame { get; set; } = Games.GameCatalog.Tf2.Id;

    /// <summary>Every game but Team Fortress 2, whose values stay in the fields earlier versions read.</summary>
    public Dictionary<string, GamePreferences> OtherGames { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore]
    public GamePreferences Game => PreferencesFor(SelectedGame);

    public GamePreferences PreferencesFor(string gameId)
    {
        if (gameId == Games.GameCatalog.Tf2.Id)
            return _tf2;
        if (!OtherGames.TryGetValue(gameId, out var preferences))
            OtherGames[gameId] = preferences = new GamePreferences { ThemeId = _tf2.ThemeId, AccentOverride = _tf2.AccentOverride };
        return preferences;
    }

    /// <summary>Where a game was last found, without starting to remember anything for a game that was never opened.</summary>
    public string? KnownPath(string gameId) =>
        gameId == Games.GameCatalog.Tf2.Id ? _tf2.Path : OtherGames.GetValueOrDefault(gameId)?.Path;

    public string? TfPath { get => _tf2.Path; set => _tf2.Path = value; }
    public string? SteamIdOverride { get; set; }

    public bool MinimizeOnLaunch { get; set; }
    public bool CloseToTray { get; set; }
    public bool CheckForUpdates { get; set; } = true;

    public bool AutoUpdate { get; set; } = true;
    public string LogLevel { get; set; } = "Info";
    public bool ClearLogsOnStart { get; set; }

    public string ThemeId { get => _tf2.ThemeId; set => _tf2.ThemeId = value; }
    public string? AccentOverride { get => _tf2.AccentOverride; set => _tf2.AccentOverride = value; }
    public BackgroundMode Background { get; set; } = BackgroundMode.Screenshots;
    public string? BackgroundImage { get; set; }

    public string? BackgroundScreenshot { get; set; }

    public int BackgroundRotateMinutes { get; set; } = 5;
    public double BackgroundDim { get; set; } = 0.72;
    public double BackgroundBlur { get; set; } = 12;
    public double UiScale { get; set; } = 1.0;
    public UiDensity Density { get; set; } = UiDensity.Comfortable;
    public List<TabPreference> Tabs { get; set; } = [];
    public string StartTab { get; set; } = "home";
    public bool ShowAdvancedSettings { get; set; }
    public BrowseView Library { get; set; } = new() { Layout = ViewLayout.List };
    public BrowseView GameBanana { get; set; } = new() { Layout = ViewLayout.Grid, CardWidth = 216 };
    public BrowseView ScreenshotView { get; set; } = new() { Layout = ViewLayout.Grid, CardWidth = 260 };
    public ServerFilters Servers { get; set; } = new();

    public string? TeamworkKey { get; set; }

    public string? SteamApiKey { get; set; }

    public string Currency { get; set; } = "USD";

    public string? PythonOverride { get; set; }
    public bool LegacyLibraryChecked { get; set; }

    public bool ReviewedGameFolder { get => _tf2.ReviewedGameFolder; set => _tf2.ReviewedGameFolder = value; }

    public bool ManageAutoexec { get => _tf2.ManageAutoexec; set => _tf2.ManageAutoexec = value; }

    public double PreviewVolume { get; set; } = 0.7;

    void IJsonOnDeserialized.OnDeserialized()
    {
        JsonChecks.NoEmptyEntries(Tabs, "tabs");
        if (Games.GameCatalog.All.All(g => g.Id != SelectedGame))
            SelectedGame = Games.GameCatalog.Tf2.Id;
        OtherGames = OtherGames
            .Where(g => g.Value is not null && g.Key != Games.GameCatalog.Tf2.Id && Games.GameCatalog.All.Any(known => known.Id == g.Key))
            .ToDictionary(g => g.Key, g => g.Value, StringComparer.Ordinal);
        foreach (var view in new[] { Library, GameBanana, ScreenshotView })
            view.Clamp();
        Servers.Clamp();
        if (TeamworkKey is not null && !global::Eternal.Core.Servers.TeamworkSource.IsValidKey(TeamworkKey))
            TeamworkKey = null;
        if (SteamApiKey is not null && !Inventory.DecalLookup.IsValidKey(SteamApiKey))
            SteamApiKey = null;
        if (!Enum.IsDefined(Background))
            Background = BackgroundMode.Screenshots;
        if (!Enum.IsDefined(Density))
            Density = UiDensity.Comfortable;
        BackgroundRotateMinutes = Math.Clamp(BackgroundRotateMinutes, 1, 120);
        BackgroundDim = double.IsFinite(BackgroundDim) ? Math.Clamp(BackgroundDim, 0, 0.95) : 0.72;
        BackgroundBlur = double.IsFinite(BackgroundBlur) ? Math.Clamp(BackgroundBlur, 0, 40) : 12;
        UiScale = double.IsFinite(UiScale) ? Math.Clamp(UiScale, 0.8, 1.4) : 1.0;
        PreviewVolume = double.IsFinite(PreviewVolume) ? Math.Clamp(PreviewVolume, 0, 1) : 0.7;
        if (BackgroundScreenshot is { Length: > 4096 })
            BackgroundScreenshot = null;
    }

    public static readonly string[] DefaultTabOrder = ["home", "servers", "mods", "backpack", "screenshots", "config", "launcher"];

    public List<TabPreference> NormalizedTabs()
    {
        var result = Tabs.Where(t => DefaultTabOrder.Contains(t.Id)).GroupBy(t => t.Id).Select(g => g.First()).ToList();
        for (var i = 0; i < DefaultTabOrder.Length; i++)
        {
            var id = DefaultTabOrder[i];
            if (result.Any(t => t.Id == id))
                continue;
            var after = i == 0 ? -1 : result.FindIndex(t => t.Id == DefaultTabOrder[i - 1]);
            result.Insert(after + 1, new TabPreference { Id = id });
        }
        result.First(t => t.Id == "launcher").Visible = true;
        return result;
    }
}
