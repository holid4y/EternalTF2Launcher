using System.IO;
using LauncherTF2.Core;
using LauncherTF2.Models;

namespace LauncherTF2.Services;

/// <summary>
/// Stores user-chosen mod thumbnails in the launcher's own AppData folder
/// (%APPDATA%\Eternal TF2 Launcher\thumbnails). Never writes into the
/// preloader engine's mod folders — anything placed there would be scanned as
/// mod content and baked into the game VPKs on the next sync.
///
/// Saved files are named "{sanitized-mod-name}.{ticks}{ext}" so each save gets
/// a fresh path; WPF caches BitmapImages by URI, so overwriting the same file
/// would keep showing the stale image.
/// </summary>
public static class ThumbnailStore
{
    private static string ThumbsDir => Path.Combine(GamePaths.AppDataPath, "thumbnails");

    /// <summary>Latest stored thumbnail for the mod, or null when none exists.</summary>
    public static string? Find(string modName)
    {
        try
        {
            var dir = ThumbsDir;
            if (!Directory.Exists(dir)) return null;

            return Directory.EnumerateFiles(dir, Prefix(modName) + ".*")
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[Thumbs] Lookup failed for '{modName}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Moves <paramref name="sourceFile"/> (a temp image produced by the crop
    /// dialog) into the store, replacing any previous thumbnail for the mod.
    /// Returns the stored path, or null on failure.
    /// </summary>
    public static string? Save(string modName, string sourceFile)
    {
        try
        {
            Directory.CreateDirectory(ThumbsDir);
            Delete(modName);

            var ext = Path.GetExtension(sourceFile);
            if (string.IsNullOrEmpty(ext)) ext = ".jpg";
            var dest = Path.Combine(ThumbsDir, $"{Prefix(modName)}.{DateTime.UtcNow.Ticks}{ext}");

            File.Move(sourceFile, dest, overwrite: true);
            return dest;
        }
        catch (Exception ex)
        {
            Logger.LogError($"[Thumbs] Failed to save thumbnail for '{modName}'", ex);
            return null;
        }
    }

    /// <summary>Removes all stored thumbnails for the mod.</summary>
    public static void Delete(string modName)
    {
        try
        {
            var dir = ThumbsDir;
            if (!Directory.Exists(dir)) return;

            foreach (var file in Directory.EnumerateFiles(dir, Prefix(modName) + ".*"))
            {
                try { File.Delete(file); }
                catch (IOException) { /* in use by an Image control — orphan is harmless */ }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[Thumbs] Cleanup failed for '{modName}': {ex.Message}");
        }
    }

    /// <summary>
    /// Filename-safe, collision-resistant key for a mod name. The hash suffix
    /// keeps names distinct even when sanitizing strips the differing characters.
    /// </summary>
    private static string Prefix(string modName)
    {
        var safe = string.Join("_", modName.Split(Path.GetInvalidFileNameChars()));
        if (safe.Length > 60) safe = safe[..60];

        uint hash = 2166136261;
        foreach (var c in modName)
            hash = (hash ^ c) * 16777619;

        return $"{safe}-{hash:x8}";
    }
}
