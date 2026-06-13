using System.Collections.ObjectModel;

namespace LauncherTF2.Models.Settings;

/// <summary>
/// A logical group of related settings. The settings page renders one panel
/// per category with the category title as the section header, plus one
/// row per <see cref="Items"/> entry.
///
/// Categories are the unit the sidebar nav scrolls between — <see cref="Id"/>
/// is the stable anchor used by both the sidebar binding and the autoexec
/// comment header for that block.
/// </summary>
public sealed class SettingCategory
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>Short cvar-style block name written to autoexec ("Gameplay", "Network", ...).</summary>
    public string AutoexecLabel { get; init; } = "";

    public ObservableCollection<SettingItem> Items { get; } = new();
}
