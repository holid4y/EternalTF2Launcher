using LauncherTF2.Core;
using LauncherTF2.Models;
using LauncherTF2.Models.Settings;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace LauncherTF2.Services;

public class AutoexecParser
{
    private static readonly IReadOnlyDictionary<string, SettingItem> CvarIndex = BuildCvarIndex();

    private static IReadOnlyDictionary<string, SettingItem> BuildCvarIndex()
        => SettingsSchema
            .Build(new SettingsModel())
            .SelectMany(cat => cat.Items)
            .Where(item => !string.IsNullOrEmpty(item.Cvar))
            .GroupBy(item => item.Cvar.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First());

    public IReadOnlySet<string> LoadFromAutoexec(SettingsModel settings, string tfPath, bool fullFileImport = false)
    {
        var shadowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cfgPath  = Path.Combine(tfPath, "cfg", "autoexec.cfg");
        if (!File.Exists(cfgPath)) return shadowed;

        try
        {
            var lines = File.ReadAllLines(cfgPath);

            if (fullFileImport)
            {
                bool insideManagedBlock = false;

                foreach (var line in lines)
                {
                    var clean = line.Trim();

                    if (clean == AutoexecWriter.BeginMarker) { insideManagedBlock = true;  continue; }
                    if (clean == AutoexecWriter.EndMarker)   { insideManagedBlock = false; continue; }

                    if (string.IsNullOrEmpty(clean) || clean.StartsWith("//")) continue;

                    var (cvar, value) = ParseCvarLine(clean);
                    if (cvar == null) continue;

                    if (!CvarIndex.TryGetValue(cvar, out var item)) continue;

                    ApplyItem(item, settings, value!);

                    // Only user-section cvars (outside our managed block) enter the shadow set.
                    if (!insideManagedBlock)
                        shadowed.Add(item.PropertyName);
                }
            }
            else
            {
                foreach (var line in ExtractManagedBlockLines(lines))
                {
                    var clean = line.Trim();
                    if (string.IsNullOrEmpty(clean) || clean.StartsWith("//")) continue;

                    var (cvar, value) = ParseCvarLine(clean);
                    if (cvar == null) continue;

                    if (!CvarIndex.TryGetValue(cvar, out var item)) continue;
                    ApplyItem(item, settings, value!);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[AutoexecParser] Failed to read autoexec.cfg: {ex.Message}");
        }

        return shadowed;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static (string? cvar, string? value) ParseCvarLine(string trimmedLine)
    {
        var match = Regex.Match(trimmedLine, @"^([a-zA-Z0-9_]+)\s+""?(-?[^""\s]+)""?");
        if (!match.Success) return (null, null);
        return (match.Groups[1].Value.ToLowerInvariant(), match.Groups[2].Value);
    }

    private static void ApplyItem(SettingItem item, SettingsModel settings, string rawValue)
    {
        var parser = item.CvarParser ?? GetDefaultParser(item);
        parser?.Invoke(settings, rawValue);
    }

    private static IEnumerable<string> ExtractManagedBlockLines(string[] lines)
    {
        var beginIdx = Array.FindIndex(lines, l => l.Trim() == AutoexecWriter.BeginMarker);
        var endIdx   = Array.FindIndex(lines, l => l.Trim() == AutoexecWriter.EndMarker);

        if (beginIdx < 0 || endIdx <= beginIdx) return [];

        return lines[(beginIdx + 1)..endIdx];
    }

    // Derives a type-appropriate parser; explicit CvarParser on the item takes precedence.
    private static Action<SettingsModel, string>? GetDefaultParser(SettingItem item)
    {
        return item switch
        {
            ToggleSetting  => (model, v) => item.SetValue(model, v == "1"),
            SliderSetting s when s.IsInteger
                           => (model, v) => item.SetValue(model,
                                  int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                                      ? (object)i : 0),
            SliderSetting  => (model, v) => item.SetValue(model,
                                  double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
                                      ? (object)d : 0.0),
            ChoiceSetting cs => (model, v) =>
            {
                var opt = cs.Options.FirstOrDefault(o =>
                    string.Equals(
                        Convert.ToString(o.Value, CultureInfo.InvariantCulture),
                        v,
                        StringComparison.OrdinalIgnoreCase));
                if (opt != null) item.SetValue(model, opt.Value);
            },
            _ => null,
        };
    }
}
