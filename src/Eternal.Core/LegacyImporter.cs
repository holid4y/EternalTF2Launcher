using System.Text.Json;
using System.Text.Json.Nodes;
using Eternal.Core.Logging;
using Eternal.Core.Persistence;
using Eternal.Core.Platform;
using Eternal.Core.Profiles;
using Eternal.Core.Settings;

namespace Eternal.Core;

public sealed record LegacyImportResult(bool Imported, IReadOnlyList<string> Notes);

/// <summary>
/// One-time import of 1.x files (Windows only): settings.json,
/// launcher_config.json, profiles/user/*.json and mod thumbnails. The old
/// files are left in place so going back to 1.x still works.
/// </summary>
public static class LegacyImporter
{
    public static LegacyImportResult Run(AppPaths paths, GameConfigStore store, LauncherSettings launcher, ProfileStore profiles)
    {
        if (!store.IsNew)
            return new LegacyImportResult(false, []);

        var legacyDir = new[] { paths.ConfigDir, AppContext.BaseDirectory }
            .FirstOrDefault(dir => File.Exists(Path.Combine(dir, "settings.json")));
        if (legacyDir is null)
            return new LegacyImportResult(false, []);

        var notes = new List<string>();
        try
        {
            ImportSettings(Path.Combine(legacyDir, "settings.json"), store, launcher, notes);
            ImportLauncherConfig(Path.Combine(legacyDir, "launcher_config.json"), launcher);
            ImportProfiles(Path.Combine(legacyDir, "profiles", "user"), profiles, notes);
            CopyThumbnails(Path.Combine(legacyDir, "thumbnails"), paths.ThumbnailsDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn("Importing 1.x settings stopped early", ex);
            notes.Add($"Some 1.x settings could not be imported: {ex.Message}");
        }

        Log.Info($"Imported 1.x settings from {legacyDir}");
        return new LegacyImportResult(true, notes);
    }

    private static void ImportSettings(string file, GameConfigStore store, LauncherSettings launcher, List<string> notes)
    {
        if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject legacy)
            return;

        var tfPath = Read<string>(legacy, "steamPath");
        if (tfPath is not null && File.Exists(Path.Combine(tfPath, "gameinfo.txt")))
            launcher.TfPath = Path.GetFullPath(tfPath);

        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

        // 1.x only wrote changed settings into its managed block, so the
        // existing autoexec is the faithful record of what it applied.
        if (launcher.TfPath is not null && AtomicFile.ReadAllTextOrNull(AutoexecFile.Locate(launcher.TfPath)) is { } autoexec)
        {
            foreach (var (id, value) in ObservedSettings.ImportFromAutoexec(autoexec))
                values[id] = value;
        }

        var (launchValues, extra) = LaunchOptions.FromLegacy(Read<string>(legacy, "launchArgs"));
        foreach (var (id, value) in launchValues)
            values[id] = value;

        store.Settings.Apply(values);
        store.Settings.ExtraLaunchArgs = extra;

        if (legacy["binds"] is JsonArray binds)
        {
            var imported = binds.OfType<JsonObject>()
                .Select(b => new KeyBind { Key = Read<string>(b, "key") ?? "", Command = Read<string>(b, "command") ?? "", Label = Read<string>(b, "name") })
                .Where(ManagedBlock.IsValidBind)
                .ToList();
            store.Settings.SetBinds(imported);
        }

        notes.Add($"Imported {values.Count} settings from Eternal Launcher 1.x.");
    }

    private static void ImportLauncherConfig(string file, LauncherSettings launcher)
    {
        if (!File.Exists(file) || JsonNode.Parse(File.ReadAllText(file)) is not JsonObject legacy)
            return;

        launcher.CloseToTray = Read<bool?>(legacy, "CloseToTray") ?? launcher.CloseToTray;
        launcher.MinimizeOnLaunch = Read<bool?>(legacy, "MinimizeToTrayOnLaunch") ?? launcher.MinimizeOnLaunch;
        launcher.ClearLogsOnStart = Read<bool?>(legacy, "AutoClearLogs") ?? false;
        launcher.LogLevel = Read<bool?>(legacy, "EnableDebugLog") == true ? "Debug" : Read<string>(legacy, "LogLevel") ?? "Info";
        if (Read<string>(legacy, "SelectedThemeId") is { Length: > 0 } theme)
            launcher.ThemeId = theme == "eternal-classic" ? "mann-co" : theme;
    }

    private static void ImportProfiles(string directory, ProfileStore profiles, List<string> notes)
    {
        if (!Directory.Exists(directory))
            return;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                profiles.Import(file);
                count++;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                Log.Warn($"Skipped 1.x profile {Path.GetFileName(file)}: {ex.Message}");
            }
        }
        if (count > 0)
            notes.Add($"Imported {count} profile(s).");
    }

    private static void CopyThumbnails(string from, string to)
    {
        if (!Directory.Exists(from) || Path.GetFullPath(from) == Path.GetFullPath(to))
            return;
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            var target = Path.Combine(to, Path.GetFileName(file));
            if (!File.Exists(target))
                File.Copy(file, target);
        }
    }

    private static T? Read<T>(JsonObject obj, string name)
    {
        try
        {
            return obj[name] is JsonValue value ? value.GetValue<T>() : default;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return default;
        }
    }
}
