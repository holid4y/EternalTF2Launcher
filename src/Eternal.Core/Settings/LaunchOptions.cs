using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Eternal.Core.Settings;

/// <summary>
/// Builds the TF2 launch line from settings instead of patching a string in
/// place, so the result is the same no matter how many times it is rebuilt.
/// </summary>
public static class LaunchOptions
{
    public const string PreloadExec = "w/config.cfg";

    private static readonly HashSet<string> ManagedFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "-novid", "-nojoy", "-high", "-softparticlesdefaultoff", "-no_texture_stream", "-noreplay",
        "-windowed", "-sw", "-noborder", "-fullscreen", "-full",
    };

    private static readonly HashSet<string> ManagedValueFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "-w", "-width", "-h", "-height", "-freq", "-refresh", "-dxlevel",
    };

    /// <summary>Splits like a shell: whitespace separates, double quotes group.</summary>
    public static List<string> Tokenize(string? args)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(args))
            return tokens;

        var current = new StringBuilder();
        var inQuotes = false;
        var quoted = false;
        foreach (var c in args)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                quoted = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0 || quoted)
                    tokens.Add(current.ToString());
                current.Clear();
                quoted = false;
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0 || quoted)
            tokens.Add(current.ToString());
        return tokens;
    }

    public static string Join(IEnumerable<string> tokens) =>
        string.Join(' ', tokens.Select(t => t.Length == 0 || t.Any(char.IsWhiteSpace) ? $"\"{t}\"" : t));

    public static bool HasUnbalancedQuotes(string? args) => (args ?? "").Count(c => c == '"') % 2 == 1;

    private static bool IsValue(string token) => !token.StartsWith('-') && !token.StartsWith('+');

    private static bool IsExecOf(List<string> tokens, int i, string file) =>
        tokens[i].Equals("+exec", StringComparison.OrdinalIgnoreCase)
        && i + 1 < tokens.Count
        && tokens[i + 1].Equals(file, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Removes flags the launcher writes itself, plus +exec autoexec.cfg (TF2
    /// already runs it from valve.rc, so passing it again runs it twice) and
    /// the preloader exec, which is added only while a preload is installed.
    /// </summary>
    public static List<string> StripManaged(IEnumerable<string> input, List<string>? removed = null)
    {
        var tokens = input.ToList();
        var kept = new List<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (ManagedFlags.Contains(token))
            {
                removed?.Add(token);
            }
            else if (ManagedValueFlags.Contains(token))
            {
                removed?.Add(token);
                if (i + 1 < tokens.Count && IsValue(tokens[i + 1]))
                    i++;
            }
            else if (IsExecOf(tokens, i, "autoexec.cfg") || IsExecOf(tokens, i, "autoexec") || IsExecOf(tokens, i, PreloadExec))
            {
                removed?.Add($"{tokens[i]} {tokens[i + 1]}");
                i++;
            }
            else
            {
                kept.Add(token);
            }
        }
        return kept;
    }

    public static List<string> Build(GameSettings settings, bool preloadInstalled)
    {
        var tokens = new List<string>();
        foreach (var setting in SettingsCatalog.All)
        {
            if (!setting.IsLaunchOption || !settings.IsTouched(setting))
                continue;
            if (setting.Tags.HasFlag(SettingTags.WindowsOnly) && !OperatingSystem.IsWindows())
                continue;
            tokens.AddRange(setting.Emit(settings.Get(setting)));
        }

        tokens.AddRange(StripManaged(Tokenize(settings.ExtraLaunchArgs)));
        if (preloadInstalled)
            tokens.AddRange(["+exec", PreloadExec]);
        return tokens;
    }

    /// <summary>
    /// Converts a 1.x launch string into setting values plus the leftover
    /// arguments that the player typed themselves.
    /// </summary>
    public static (Dictionary<string, JsonNode> Values, string Extra) FromLegacy(string? legacy)
    {
        var values = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var tokens = Tokenize(legacy);
        var extra = new List<string>();
        var windowed = false;
        var noBorder = false;
        string? Next(ref int i) => i + 1 < tokens.Count && IsValue(tokens[i + 1]) ? tokens[++i] : null;

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i].ToLowerInvariant();
            switch (token)
            {
                case "-novid": values["SkipIntro"] = true; break;
                case "-nojoy": values["DisableJoystick"] = true; break;
                case "-high": values["HighPriority"] = true; break;
                case "-softparticlesdefaultoff": values["SoftParticlesOff"] = true; break;
                case "-no_texture_stream": values["NoTextureStream"] = true; break;
                case "-noreplay": values["DisableReplay"] = true; break;
                case "-windowed" or "-sw": windowed = true; break;
                case "-noborder": noBorder = true; break;
                case "-fullscreen" or "-full": values["DisplayMode"] = "fullscreen"; break;
                case "-w" or "-width":
                    if (Next(ref i) is { } w && int.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width))
                        values["Width"] = width;
                    break;
                case "-h" or "-height":
                    if (Next(ref i) is { } h && int.TryParse(h, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height))
                        values["Height"] = height;
                    break;
                case "-freq" or "-refresh":
                    if (Next(ref i) is { } f && int.TryParse(f, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hz))
                        values["RefreshRate"] = hz;
                    break;
                case "-dxlevel":
                    // 1.x always forced -dxlevel 90 unless the player picked something else.
                    if (Next(ref i) is { } dx && dx != "90")
                        values["DxLevel"] = dx;
                    break;
                case "-threads":
                    Next(ref i);
                    break;
                case "-nosound" or "-nohltv" or "-no_steam_controller":
                    break;
                case "+exec" when i + 1 < tokens.Count && tokens[i + 1].ToLowerInvariant() is "autoexec.cfg" or "autoexec" or PreloadExec:
                    i++;
                    break;
                default:
                    extra.Add(tokens[i]);
                    break;
            }
        }

        if (windowed)
            values["DisplayMode"] = noBorder ? "borderless" : "windowed";
        return (values, Join(extra));
    }

    /// <summary>Managed flags the player typed into their own options, for a UI hint.</summary>
    public static List<string> ManagedFlagsIn(string? extra)
    {
        var removed = new List<string>();
        StripManaged(Tokenize(extra), removed);
        return removed;
    }
}
