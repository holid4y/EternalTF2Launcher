using System.Globalization;
using Eternal.Core.Logging;

namespace Eternal.Core.Mods;

/// <summary>
/// Card images for mods. Kept in the launcher's data folder, never in the mod
/// library, or they would be packed into the game's VPKs. A picture the player
/// picks is stored by mod name (file names match 1.x); pictures fetched from a
/// mod's source page are stored per source under <c>sources/</c>, so mods from
/// the same submission share one.
/// </summary>
public sealed class ThumbnailStore(string directory)
{
    private static readonly TimeSpan RetryMissingAfter = TimeSpan.FromDays(7);

    private string SourcesDir => Path.Combine(directory, "sources");

    public string? Find(string modName)
    {
        if (!Directory.Exists(directory))
            return null;
        return Directory.EnumerateFiles(directory, Prefix(modName) + ".*")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    public string Save(string modName, byte[] png)
    {
        Directory.CreateDirectory(directory);
        Delete(modName);
        var path = Path.Combine(directory, $"{Prefix(modName)}.{DateTime.UtcNow.Ticks}.png");
        File.WriteAllBytes(path, png);
        return path;
    }

    public void Delete(string modName)
    {
        if (!Directory.Exists(directory))
            return;
        foreach (var file in Directory.EnumerateFiles(directory, Prefix(modName) + ".*"))
            TryDelete(file);
    }

    public string? FindForSource(string? source) =>
        SourceKey(source) is { } key && File.Exists(SourcePath(key)) ? SourcePath(key) : null;

    /// <summary>True when the source's picture is stored, or was looked for recently and doesn't exist.</summary>
    public bool KnowsSource(string key)
    {
        if (File.Exists(SourcePath(key)))
            return true;
        var marker = SourcePath(key) + ".none";
        return File.Exists(marker) && DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) < RetryMissingAfter;
    }

    public void SaveForSource(string key, byte[] png)
    {
        Directory.CreateDirectory(SourcesDir);
        Persistence.AtomicFile.WriteAllBytes(SourcePath(key), png, keepBackup: false);
        TryDelete(SourcePath(key) + ".none");
    }

    public void MarkSourceMissing(string key)
    {
        Directory.CreateDirectory(SourcesDir);
        File.WriteAllBytes(SourcePath(key) + ".none", []);
    }

    /// <summary>Deletes stored source pictures no mod points to any more.</summary>
    public void PruneSources(IEnumerable<string?> sources)
    {
        if (!Directory.Exists(SourcesDir))
            return;
        var keep = sources.Select(SourceKey).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(SourcesDir))
        {
            var key = Path.GetFileName(file).Split('.')[0];
            if (!keep.Contains(key))
                TryDelete(file);
        }
    }

    /// <summary>
    /// "gamebanana:123:456" becomes "gamebanana-123": one picture per submission,
    /// whichever of its files was installed. Anything else has no picture to fetch.
    /// </summary>
    public static string? SourceKey(string? source)
    {
        if (source is null)
            return null;
        var parts = source.Split(':');
        if (parts.Length < 2 || parts[0] is not ("gamebanana" or "gamebanana-sound"))
            return null;
        return long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? $"{parts[0]}-{id.ToString(CultureInfo.InvariantCulture)}"
            : null;
    }

    private string SourcePath(string key) => Path.Combine(SourcesDir, key + ".png");

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Debug($"Couldn't delete {file}: {ex.Message}"); }
    }

    internal static string Prefix(string modName)
    {
        var safe = string.Join("_", modName.Split(Path.GetInvalidFileNameChars()));
        if (safe.Length > 60)
            safe = safe[..60];
        uint hash = 2166136261;
        foreach (var c in modName)
            hash = (hash ^ c) * 16777619;
        return $"{safe}-{hash:x8}";
    }
}
