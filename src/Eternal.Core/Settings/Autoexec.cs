using System.Text;
using System.Text.RegularExpressions;
using Eternal.Core.Persistence;

namespace Eternal.Core.Settings;

public enum AutoexecStatus { Written, Unchanged, NoGameFolder, Failed, LeftAlone }

public sealed record AutoexecResult(AutoexecStatus Status, string? Path = null, string? Error = null)
{
    public bool Applied => Status is AutoexecStatus.Written or AutoexecStatus.Unchanged or AutoexecStatus.LeftAlone;
}

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

        var seeThrough = SettingsCatalog.Get<ToggleSetting>(SettingsCatalog.TransparentViewmodelsId);
        if (settings.Supports(seeThrough) && settings.Get(seeThrough))
        {
            lines.Add("");
            lines.Add("// See-through weapons");
            lines.AddRange(TransparentViewmodels.Cvars);
        }
        return lines;
    }
}

public static class AutoexecFile
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Older editors saved config files in the Windows code page. Read as UTF-8, their accented
    /// letters become replacement characters, and a rewrite would save them that way.
    /// </summary>
    public static string Decode(byte[] bytes)
    {
        try
        {
            using var reader = new StreamReader(new MemoryStream(bytes), StrictUtf8);
            return reader.ReadToEnd();
        }
        catch (DecoderFallbackException)
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(1252)!.GetString(bytes);
        }
    }

    public static string Locate(string tfPath)
    {
        var custom = Path.Combine(tfPath, "custom");
        var hasComfig = Directory.Exists(custom)
            && Directory.EnumerateFileSystemEntries(custom, "mastercomfig*").Any();
        return hasComfig
            ? Path.Combine(tfPath, "cfg", "overrides", "autoexec.cfg")
            : Path.Combine(tfPath, "cfg", "autoexec.cfg");
    }

    public static AutoexecResult Write(string? tfPath, IReadOnlyList<string> managedLines, string? replaceUserContent = null)
    {
        if (string.IsNullOrWhiteSpace(tfPath) || !File.Exists(Path.Combine(tfPath, "gameinfo.txt")))
            return new AutoexecResult(AutoexecStatus.NoGameFolder, Error: "The game folder isn't set or wasn't found.");

        var path = Locate(tfPath);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            byte[]? original;
            try
            {
                original = AtomicFile.ReadAllBytesOrNull(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new AutoexecResult(AutoexecStatus.Failed, path, $"Couldn't read {Path.GetFileName(path)}, so it was left untouched: {ex.Message}");
            }

            var originalText = original is null ? null : Decode(original);
            var document = AutoexecDocument.Parse(originalText);
            if (managedLines.Count == 0 && !document.HasManagedBlock && replaceUserContent is null)
                return new AutoexecResult(AutoexecStatus.Unchanged, path);
            var text = AutoexecDocument.Render(replaceUserContent ?? document.UserContent, managedLines);
            if (originalText == text)
                return new AutoexecResult(AutoexecStatus.Unchanged, path);

            try
            {
                if (!(AtomicFile.ReadAllBytesOrNull(path) ?? []).AsSpan().SequenceEqual(original ?? []))
                    continue;
                if (original is not null)
                    AtomicFile.WriteAllBytes(path + ".eternal-backup", original, keepBackup: false);
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
