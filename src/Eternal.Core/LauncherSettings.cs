namespace Eternal.Core;

public enum UiDensity { Comfortable, Compact }

public enum BackgroundMode { Screenshots, Picture, None }

public sealed class TabPreference
{
    public string Id { get; set; } = "";
    public bool Visible { get; set; } = true;
}

public sealed class LauncherSettings
{
    public int Version { get; set; } = 2;

    public string? TfPath { get; set; }
    public string? SteamIdOverride { get; set; }

    public bool MinimizeOnLaunch { get; set; }
    public bool CloseToTray { get; set; }
    public bool CheckForUpdates { get; set; } = true;
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

    public string Currency { get; set; } = "USD";

    public string? PythonOverride { get; set; }
    public bool LegacyLibraryChecked { get; set; }

    public static readonly string[] DefaultTabOrder = ["home", "mods", "backpack", "config", "launcher"];

    /// <summary>Tab list in the saved order, with unknown ids dropped and new tabs appended.</summary>
    public List<TabPreference> NormalizedTabs()
    {
        var result = Tabs.Where(t => DefaultTabOrder.Contains(t.Id)).GroupBy(t => t.Id).Select(g => g.First()).ToList();
        foreach (var id in DefaultTabOrder)
        {
            if (result.All(t => t.Id != id))
                result.Add(new TabPreference { Id = id });
        }
        // The launcher tab holds these settings; hiding it would lock the player out.
        result.First(t => t.Id == "launcher").Visible = true;
        return result;
    }
}
