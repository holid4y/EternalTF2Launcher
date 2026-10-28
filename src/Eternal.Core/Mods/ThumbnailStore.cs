using Eternal.Core.Logging;

namespace Eternal.Core.Mods;

/// <summary>
/// Custom card images for mods. Kept in the launcher's data folder, never in
/// the mod library, or they would be packed into the game's VPKs.
/// File names match 1.x so migrated thumbnails keep working.
/// </summary>
public sealed class ThumbnailStore(string directory)
{
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
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Debug($"Couldn't delete {file}: {ex.Message}"); }
        }
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
