using LauncherTF2.Core;
using LauncherTF2.Models;
using LauncherTF2.Models.Settings;
using System.IO;
using System.Text;

namespace LauncherTF2.Services;

public class AutoexecWriter
{
    public const string BeginMarker = "// === ETERNAL LAUNCHER MANAGED BLOCK — do not edit between these markers ===";
    public const string EndMarker = "// === ETERNAL LAUNCHER MANAGED BLOCK END ===";

    public static void WriteToAutoexec(
        SettingsModel settings,
        string tfPath,
        IReadOnlySet<string>? shadowed = null,
        string? userContentOverride = null)
    {
        var cfgPath = Path.Combine(tfPath, "cfg", "autoexec.cfg");
        var cfgDir = Path.GetDirectoryName(cfgPath);
        if (cfgDir != null && !Directory.Exists(cfgDir))
            Directory.CreateDirectory(cfgDir);

        // userContentOverride is set when a profile is applied that has a stored
        // user section — replaces whatever is currently in the file's user section.
        var userContent = userContentOverride ?? ExtractUserContent(cfgPath);
        var managedBlock = BuildManagedBlock(settings, shadowed);

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(userContent))
        {
            sb.Append(userContent);
            if (!userContent.EndsWith("\n")) sb.AppendLine();
            sb.AppendLine();
        }
        sb.AppendLine(BeginMarker);
        sb.Append(managedBlock);
        sb.AppendLine(EndMarker);

        try
        {
            File.WriteAllText(cfgPath, sb.ToString());
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[AutoexecWriter] Failed to write autoexec.cfg: {ex.Message}");
        }
    }

    // Returns everything outside our managed markers; the whole file when no markers are present.
    internal static string ExtractUserContent(string cfgPath)
    {
        if (!File.Exists(cfgPath)) return string.Empty;

        try
        {
            var lines = File.ReadAllLines(cfgPath);
            var beginIdx = Array.FindIndex(lines, l => l.Trim() == BeginMarker);
            var endIdx = Array.FindIndex(lines, l => l.Trim() == EndMarker);

            if (beginIdx < 0 || endIdx < 0 || endIdx <= beginIdx)
            {
                return string.Join(Environment.NewLine, lines).TrimEnd();
            }

            var before = lines.Take(beginIdx);
            var after = lines.Skip(endIdx + 1);
            return string.Join(Environment.NewLine, before.Concat(after)).TrimEnd();
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[AutoexecWriter] Could not read existing autoexec.cfg: {ex.Message}");
            return string.Empty;
        }
    }

    private static string BuildManagedBlock(SettingsModel settings, IReadOnlySet<string>? shadowed)
    {
        // Only emit settings that differ from their defaults, plus any that are shadowed
        // (found in the user section), so the managed block wins the last-write-wins race.
        var defaults = new SettingsModel();

        var sb = new StringBuilder();

        sb.AppendLine("// Auto-generated. Toggles in the Eternal Launcher write to this block;");
        sb.AppendLine("// anything outside the markers is yours and won't be touched.");
        sb.AppendLine();

        foreach (var cat in SettingsSchema.Build(settings))
        {
            var lines = new List<string>();

            foreach (var item in cat.Items)
            {
                bool differsFromDefault = !Equals(item.GetValue(settings), item.GetValue(defaults));
                bool isShadowed = shadowed != null
                                  && !string.IsNullOrEmpty(item.PropertyName)
                                  && shadowed.Contains(item.PropertyName);
                if (!differsFromDefault && !isShadowed) continue;

                foreach (var line in item.EmitCvarLines())
                    if (!string.IsNullOrWhiteSpace(line)) lines.Add(line);
            }

            if (lines.Count == 0) continue;

            sb.AppendLine($"// --- {cat.AutoexecLabel} ---");
            foreach (var line in lines) sb.AppendLine(line);
            sb.AppendLine();
        }

        if (settings.Binds.Count > 0)
        {
            sb.AppendLine("// --- Binds ---");
            foreach (var bind in settings.Binds)
            {
                if (!string.IsNullOrWhiteSpace(bind.Key) && !string.IsNullOrWhiteSpace(bind.Command))
                    sb.AppendLine($"bind \"{bind.Key}\" \"{bind.Command}\"");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
