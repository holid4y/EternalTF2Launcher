using System.Text;
using System.Text.RegularExpressions;
using Eternal.Core.Persistence;

namespace Eternal.Core.Settings;

public enum AutoexecStatus { Written, Unchanged, NoGameFolder, Failed }

public sealed record AutoexecResult(AutoexecStatus Status, string? Path = null, string? Error = null)
{
    public bool Applied => Status is AutoexecStatus.Written or AutoexecStatus.Unchanged;
}

/// <summary>
/// autoexec.cfg split into the player's own text and the block the launcher owns.
/// Markers are unchanged from 1.x so existing files keep working.
/// </summary>
public sealed class AutoexecDocument
{
    public const string BeginMarker = "// === ETERNAL LAUNCHER MANAGED BLOCK — do not edit between these markers ===";
    public const string EndMarker = "// === ETERNAL LAUNCHER MANAGED BLOCK END ===";

    private AutoexecDocument(string userContent, IReadOnlyList<string> managedLines, bool hasBlock)
    {
        UserContent = userContent;
        ManagedLines = managedLines;
        HasManagedBlock = hasBlock;
    }

    public string UserContent { get; }
    public IReadOnlyList<string> ManagedLines { get; }
    public bool HasManagedBlock { get; }

    public static AutoexecDocument Parse(string? text)
    {
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
        var begin = Array.FindIndex(lines, l => l.Trim() == BeginMarker);
        var end = begin < 0 ? -1 : Array.FindIndex(lines, begin + 1, l => l.Trim() == EndMarker);

        // A half-deleted block is treated as the player's text so nothing is lost.
        if (begin < 0 || end < 0)
            return new AutoexecDocument(string.Join('\n', lines).TrimEnd(), [], false);

        var user = lines.Take(begin).Concat(lines.Skip(end + 1));
        return new AutoexecDocument(string.Join('\n', user).TrimEnd(), lines[(begin + 1)..end], true);
    }

    public static string Render(string userContent, IReadOnlyList<string> managedLines)
    {
        var nl = Environment.NewLine;
        var sb = new StringBuilder();
        var user = userContent.Replace("\r\n", "\n").TrimEnd();
        if (user.Length > 0)
            sb.Append(user.Replace("\n", nl)).Append(nl).Append(nl);

        sb.Append(BeginMarker).Append(nl);
        sb.Append("// Written by Eternal Launcher. Change these in the launcher; everything").Append(nl);
        sb.Append("// outside the markers belongs to you and is never modified.").Append(nl);
        foreach (var line in managedLines)
            sb.Append(line).Append(nl);
        sb.Append(EndMarker).Append(nl);
        return sb.ToString();
    }
}

public static partial class ManagedBlock
{
    [GeneratedRegex(@"^[A-Za-z0-9_\-\.,'/\\\[\]`=;]+$")]
    private static partial Regex KeyName();

    public static bool IsValidBind(KeyBind bind) =>
        bind.Key.Length is > 0 and <= 24
        && KeyName().IsMatch(bind.Key)
        && !bind.Key.Contains('"')
        && !string.IsNullOrWhiteSpace(bind.Command)
        && bind.Command.Length <= 255
        && bind.Command.IndexOfAny(['"', '\r', '\n']) < 0;

    /// <summary>Lines for every touched autoexec setting, grouped by category, then binds.</summary>
    public static List<string> Build(GameSettings settings)
    {
        var lines = new List<string>();
        foreach (var category in SettingsCatalog.Categories)
        {
            var emitted = category.Settings
                .Where(s => !s.IsLaunchOption && settings.IsTouched(s))
                .SelectMany(s => s.Emit(settings.Get(s)))
                .ToList();
            if (emitted.Count == 0)
                continue;
            lines.Add("");
            lines.Add($"// {category.Title}");
            lines.AddRange(emitted);
        }

        var binds = settings.Binds.Where(IsValidBind).ToList();
        if (binds.Count > 0)
        {
            lines.Add("");
            lines.Add("// Binds");
            lines.AddRange(binds.Select(b => $"bind \"{b.Key.ToLowerInvariant()}\" \"{b.Command}\""));
        }
        return lines;
    }
}

public static class AutoexecFile
{
    /// <summary>
    /// mastercomfig ships its own autoexec.cfg inside a VPK, which shadows
    /// tf/cfg/autoexec.cfg; it runs cfg/overrides/autoexec.cfg instead.
    /// </summary>
    public static string Locate(string tfPath)
    {
        var custom = Path.Combine(tfPath, "custom");
        var hasComfig = Directory.Exists(custom)
            && Directory.EnumerateFileSystemEntries(custom, "mastercomfig*").Any();
        return hasComfig
            ? Path.Combine(tfPath, "cfg", "overrides", "autoexec.cfg")
            : Path.Combine(tfPath, "cfg", "autoexec.cfg");
    }

    /// <summary>
    /// Replaces the managed block. An unreadable existing file is never
    /// overwritten, the previous version is kept as autoexec.cfg.eternal-backup,
    /// and a file that changes while we work is re-read instead of clobbered.
    /// </summary>
    public static AutoexecResult Write(string? tfPath, IReadOnlyList<string> managedLines, string? replaceUserContent = null)
    {
        if (string.IsNullOrWhiteSpace(tfPath) || !File.Exists(Path.Combine(tfPath, "gameinfo.txt")))
            return new AutoexecResult(AutoexecStatus.NoGameFolder, Error: "TF2 folder not set or not found.");

        var path = Locate(tfPath);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            string? original;
            try
            {
                original = AtomicFile.ReadAllTextOrNull(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new AutoexecResult(AutoexecStatus.Failed, path, $"Couldn't read {Path.GetFileName(path)}, so it was left untouched: {ex.Message}");
            }

            var document = AutoexecDocument.Parse(original);
            var text = AutoexecDocument.Render(replaceUserContent ?? document.UserContent, managedLines);
            if (original == text)
                return new AutoexecResult(AutoexecStatus.Unchanged, path);

            try
            {
                if (AtomicFile.ReadAllTextOrNull(path) != original)
                    continue;
                if (original is not null)
                    AtomicFile.WriteAllText(path + ".eternal-backup", original, keepBackup: false);
                AtomicFile.WriteAllText(path, text, keepBackup: false);
                return new AutoexecResult(AutoexecStatus.Written, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new AutoexecResult(AutoexecStatus.Failed, path, ex.Message);
            }
        }

        return new AutoexecResult(AutoexecStatus.Failed, path, "The file kept changing while saving. Close any editor using it and retry.");
    }
}
