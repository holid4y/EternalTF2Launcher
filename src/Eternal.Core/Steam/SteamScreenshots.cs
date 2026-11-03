using System.Globalization;
using System.Text.RegularExpressions;
using Eternal.Core.Logging;
using Eternal.Core.Persistence;
using Eternal.Core.Platform;

namespace Eternal.Core.Steam;

public sealed record SteamScreenshot(
    string UserDir,
    string RecordedName,
    string FullPath,
    string ThumbnailPath,
    int Width,
    int Height,
    DateTimeOffset Taken,
    string? Caption,
    ulong? PublishedFileId)
{
    public string FileName => Path.GetFileName(FullPath);

    public string? ShareUrl => PublishedFileId is { } id
        ? $"https://steamcommunity.com/sharedfiles/filedetails/?id={id.ToString(CultureInfo.InvariantCulture)}"
        : null;
}

public static partial class SteamScreenshots
{
    public const string AppKey = "440";
    public const int ThumbnailWidth = 200;

    [GeneratedRegex(@"^440/screenshots/[A-Za-z0-9_\-]{1,80}\.(?:jpg|jpeg|png)$", RegexOptions.IgnoreCase)]
    private static partial Regex RecordedPattern();

    public static bool IsRecordedName(string? name) => name is not null && RecordedPattern().IsMatch(name);

    public static string Folder(string userDir) => Path.Combine(userDir, "760", "remote", AppKey, "screenshots");

    public static IReadOnlyList<SteamScreenshot> Load(string userDir)
    {
        var remote = Path.Combine(userDir, "760", "remote");
        var folder = Folder(userDir);
        var result = new Dictionary<string, SteamScreenshot>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var text = AtomicFile.ReadAllTextOrNull(Path.Combine(userDir, "760", "screenshots.vdf"));
            var entries = text is null ? null : Vdf.Parse(text).Path("screenshots", AppKey);
            foreach (var (_, value) in entries?.Children ?? [])
            {
                if (value is not VdfNode entry || entry.Value("filename") is not { } name || !IsRecordedName(name))
                    continue;
                var full = Path.GetFullPath(Path.Combine(remote, name));
                if (!AppPaths.IsInside(folder, full) || !File.Exists(full))
                    continue;
                result[full] = new SteamScreenshot(
                    userDir, name, full, ThumbnailFor(full),
                    Int(entry.Value("width")), Int(entry.Value("height")),
                    long.TryParse(entry.Value("creation"), NumberStyles.None, CultureInfo.InvariantCulture, out var created) && created > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(Math.Min(created, 253402300799)) : new DateTimeOffset(File.GetLastWriteTimeUtc(full)),
                    entry.Value("caption") is { Length: > 0 } caption ? caption : null,
                    ulong.TryParse(entry.Value("publishedfileid"), NumberStyles.None, CultureInfo.InvariantCulture, out var published) && published > 0 ? published : null);
            }

            if (Directory.Exists(folder))
            {
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    var name = $"440/screenshots/{Path.GetFileName(file)}";
                    var full = Path.GetFullPath(file);
                    if (!IsRecordedName(name) || result.ContainsKey(full))
                        continue;
                    result[full] = new SteamScreenshot(userDir, name, full, ThumbnailFor(full), 0, 0, new DateTimeOffset(File.GetLastWriteTimeUtc(full)), null, null);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Couldn't read the Steam screenshot library: {ex.Message}");
        }
        return result.Values.OrderByDescending(s => s.Taken).ToList();
    }

    public static string ThumbnailFor(string fullPath) =>
        Path.Combine(Path.GetDirectoryName(fullPath)!, "thumbnails", Path.GetFileName(fullPath));

    private static int Int(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is > 0 and <= 32768 ? n : 0;
}

public sealed class ScreenshotOriginals(string directory)
{
    private string PathFor(SteamScreenshot shot) =>
        Path.Combine(directory, Path.GetFileName(Path.TrimEndingDirectorySeparator(shot.UserDir)), shot.FileName);

    public bool HasOriginal(SteamScreenshot shot) => File.Exists(PathFor(shot));

    public void SaveEdited(SteamScreenshot shot, ReadOnlySpan<byte> image, ReadOnlySpan<byte> thumbnail)
    {
        var original = PathFor(shot);
        if (!File.Exists(original))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(original)!);
            File.Copy(shot.FullPath, original);
        }
        AtomicFile.WriteAllBytes(shot.FullPath, image, keepBackup: false);
        AtomicFile.WriteAllBytes(shot.ThumbnailPath, thumbnail, keepBackup: false);
    }

    public bool Restore(SteamScreenshot shot)
    {
        var original = PathFor(shot);
        if (!File.Exists(original))
            return false;
        AtomicFile.WriteAllBytes(shot.FullPath, File.ReadAllBytes(original), keepBackup: false);
        File.Delete(original);
        return true;
    }

    public void Forget(SteamScreenshot shot)
    {
        var original = PathFor(shot);
        if (File.Exists(original))
            File.Delete(original);
    }
}
