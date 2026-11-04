using System.Text.Json.Serialization;
using Eternal.Core.Persistence;

namespace Eternal.Core;

public enum UiDensity { Comfortable, Compact }

public enum BackgroundMode { Screenshots, Picture, None }

public enum ViewLayout { Grid, List }

public sealed class BrowseView
{
    public const double MinCardWidth = 160, MaxCardWidth = 360;

    public ViewLayout Layout { get; set; }
    public double CardWidth { get; set; } = 220;

    /// <summary>Also list submissions GameBanana marks as adult (18+). Off by default.</summary>
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

public sealed class LauncherSettings : IJsonOnDeserialized
{
    public int Version { get; set; } = 2;

    public string? TfPath { get; set; }
    public string? SteamIdOverride { get; set; }

    public bool MinimizeOnLaunch { get; set; }
    public bool CloseToTray { get; set; }
    public bool CheckForUpdates { get; set; } = true;

    public bool AutoUpdate { get; set; } = true;
    public string LogLevel { get; set; } = "Info";
    public bool ClearLogsOnStart { get; set; }

    public string ThemeId { get; set; } = "mann-co";
    public string? AccentOverride { get; set; }
    public BackgroundMode Background { get; set; } = BackgroundMode.Screenshots;
    public string? BackgroundImage { get; set; }
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

    void IJsonOnDeserialized.OnDeserialized()
    {
        JsonChecks.NoEmptyEntries(Tabs, "tabs");
        foreach (var view in new[] { Library, GameBanana, ScreenshotView })
            view.Clamp();
        Servers.Clamp();
        if (TeamworkKey is not null && !global::Eternal.Core.Servers.TeamworkSource.IsValidKey(TeamworkKey))
            TeamworkKey = null;
        if (SteamApiKey is not null && !Inventory.DecalLookup.IsValidKey(SteamApiKey))
            SteamApiKey = null;
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
