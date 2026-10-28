using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Eternal.Core.Logging;
using Eternal.Core.Persistence;

namespace Eternal.Core.Settings;

/// <summary>
/// Values TF2 wrote to config.cfg and video.txt, for settings the player
/// never changed in the launcher.
/// </summary>
public static partial class ObservedSettings
{
    [GeneratedRegex("\"setting\\.([A-Za-z0-9_]+)\"\\s+\"([^\"]*)\"")]
    private static partial Regex VideoSetting();

    public static Dictionary<string, JsonNode> Read(string? tfPath)
    {
        var result = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(tfPath))
            return result;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (AtomicFile.ReadAllTextOrNull(Path.Combine(tfPath, "cfg", "video.txt")) is { } video)
            {
                foreach (Match m in VideoSetting().Matches(video))
                    values[m.Groups[1].Value] = m.Groups[2].Value;
            }

            if (AtomicFile.ReadAllTextOrNull(Path.Combine(tfPath, "cfg", "config.cfg")) is { } config)
            {
                foreach (var (cvar, value) in CfgText.Assignments(config))
                    values[cvar] = value;

                var binds = CfgText.Binds(config);
                if (binds.TryGetValue("w", out var w))
                    result["NullMovement"] = JsonValue.Create(w.Equals("+mfwd", StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Couldn't read TF2's own config files: {ex.Message}");
            return result;
        }

        foreach (var (cvar, setting) in SettingsCatalog.ByCvar)
        {
            if (values.TryGetValue(cvar, out var raw) && setting.Parse(raw) is { } parsed)
                result[setting.Id] = parsed;
        }
        return result;
    }

    /// <summary>
    /// First run: known cvars from a hand-written autoexec become touched
    /// values, so the managed block keeps the player's numbers.
    /// </summary>
    public static Dictionary<string, JsonNode> ImportFromAutoexec(string text)
    {
        var result = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var document = AutoexecDocument.Parse(text);
        var source = document.UserContent + "\n" + string.Join('\n', document.ManagedLines);

        foreach (var (cvar, raw) in CfgText.Assignments(source))
        {
            if (SettingsCatalog.ByCvar.TryGetValue(cvar, out var setting) && setting.Parse(raw) is { } parsed)
                result[setting.Id] = parsed;
        }
        if (NullMovement.Detect(source) is { } nullMovement)
            result["NullMovement"] = JsonValue.Create(nullMovement);
        return result;
    }
}
