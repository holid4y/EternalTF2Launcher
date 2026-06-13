using LauncherTF2.Models;
using LauncherTF2.Core;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;

namespace LauncherTF2.Services;

/// <summary>
/// Manages locally installed mods, scanning the TF2 custom folder.
/// 
/// Enabled/Disabled state is filesystem-based:
///   - Enabled mods live in: {tf/custom}/
///   - Disabled mods live in: {tf/custom}/disabled/
/// 
/// On first run (no mod_state.json), all mods already in custom/ are treated as enabled.
/// </summary>
public class ModManagerService
{
    private readonly string _modsConfigPath;
    private bool _hasExistingState;

    /// <summary>Resolved path to tf/custom.</summary>
    public string CustomFolderPath { get; private set; }

    /// <summary>Resolved path to tf/custom/disabled.</summary>
    public string DisabledFolderPath => Path.Combine(CustomFolderPath, "disabled");

    public ModManagerService(SettingsService settingsService)
    {
        _modsConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mod_state.json");
        _hasExistingState = File.Exists(_modsConfigPath);

        CustomFolderPath = ResolveTf2CustomPath(settingsService);
        InitializeDirectories();
    }

    /// <summary>
    /// Search for a likely thumbnail image file next to a VPK file.
    /// Returns an empty string when none found.
    /// </summary>
    private string FindLocalThumbnailForFile(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath) ?? string.Empty;
            var baseName = Path.GetFileNameWithoutExtension(filePath);

            // Common sibling names: mymod.png, mymod.jpg
            var candidates = new[] { 
                baseName + ".png", baseName + ".jpg", baseName + ".jpeg", baseName + ".webp" };

            foreach (var c in candidates)
            {
                var p = Path.Combine(dir, c);
                if (File.Exists(p)) return p;
            }

            // Fallback: any image file in same folder with typical names
            var prox = Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                        .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(f => new FileInfo(f).Length)
                        .FirstOrDefault();

            return prox ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Heuristic search for thumbnails inside a mod folder.
    /// Prioritize files that match common preview names and larger file sizes.
    /// Returns empty string if none found.
    /// </summary>
    private string FindLocalThumbnailForFolder(string folderPath)
    {
        try
        {
            if (!Directory.Exists(folderPath)) return string.Empty;

            // Look for common filenames at top level first
            var topCandidates = new[] { "thumbnail.png", "thumbnail.jpg", "preview.png", "preview.jpg", "cover.png", "cover.jpg", "icon.png", "icon.jpg", "preview.webp" };
            foreach (var name in topCandidates)
            {
                var p = Path.Combine(folderPath, name);
                if (File.Exists(p)) return p;
            }

            // Look for any image directly under folder (not too deep)
            var imgs = Directory.GetFiles(folderPath, "*.png", SearchOption.TopDirectoryOnly)
                        .Concat(Directory.GetFiles(folderPath, "*.jpg", SearchOption.TopDirectoryOnly))
                        .Concat(Directory.GetFiles(folderPath, "*.jpeg", SearchOption.TopDirectoryOnly))
                        .Concat(Directory.GetFiles(folderPath, "*.webp", SearchOption.TopDirectoryOnly))
                        .ToList();

            if (imgs.Count > 0)
            {
                // Prefer the largest file (likely a preview image)
                return imgs.OrderByDescending(f => new FileInfo(f).Length).First();
            }

            // Search common subfolders (materials, media, images)
            var subfolders = new[] { "materials", "media", "images", "textures" };
            foreach (var sub in subfolders)
            {
                var subPath = Path.Combine(folderPath, sub);
                if (!Directory.Exists(subPath)) continue;
                var found = Directory.GetFiles(subPath, "*.*", SearchOption.AllDirectories)
                            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(f => new FileInfo(f).Length)
                            .FirstOrDefault();
                if (!string.IsNullOrEmpty(found)) return found;
            }

            // Last resort: any image anywhere under the folder (but avoid scanning huge trees extensively)
            var any = Directory.GetFiles(folderPath, "*.png", SearchOption.AllDirectories)
                        .Concat(Directory.GetFiles(folderPath, "*.jpg", SearchOption.AllDirectories))
                        .Concat(Directory.GetFiles(folderPath, "*.jpeg", SearchOption.AllDirectories))
                        .OrderByDescending(f => new FileInfo(f).Length)
                        .FirstOrDefault();

            return any ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Resolves the TF2 custom folder path from the shared SettingsService
    /// instead of re-parsing settings.json directly.
    /// </summary>
    private string ResolveTf2CustomPath(SettingsService settingsService)
    {
        try
        {
            var steamPath = settingsService.GetSettings().SteamPath;
            if (!string.IsNullOrWhiteSpace(steamPath))
            {
                var customPath = ResolveCustomFolder(steamPath);
                Logger.LogInfo($"[Mods] Resolved custom folder: {customPath}");
                return customPath;
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning("[Mods] Failed to read settings for custom path", ex);
        }

        var fallback = Path.Combine(GamePaths.DefaultTf2Path, "custom");
        Logger.LogInfo($"[Mods] Using default custom folder: {fallback}");
        return fallback;
    }

    /// <summary>
    /// Handles SteamPath being either ".../tf" or ".../Team Fortress 2".
    /// </summary>
    private static string ResolveCustomFolder(string steamPath)
    {
        var dirName = Path.GetFileName(steamPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (string.Equals(dirName, "tf", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(steamPath, "custom");

        if (Directory.Exists(Path.Combine(steamPath, "tf")))
            return Path.Combine(steamPath, "tf", "custom");

        return Path.Combine(steamPath, "custom");
    }

    private void InitializeDirectories()
    {
        try
        {
            if (!Directory.Exists(CustomFolderPath))
            {
                Directory.CreateDirectory(CustomFolderPath);
                Logger.LogInfo($"[Mods] Created custom directory: {CustomFolderPath}");
            }

            if (!Directory.Exists(DisabledFolderPath))
            {
                Directory.CreateDirectory(DisabledFolderPath);
                Logger.LogInfo($"[Mods] Created disabled directory: {DisabledFolderPath}");
            }

            // Mark that state now exists so subsequent runs won't override user choices
            if (!_hasExistingState)
            {
                File.WriteAllText(_modsConfigPath, "{}");
                _hasExistingState = true;
            }

            // Clean stale .cache files left by TF2/Steam on every startup
            CleanCacheFiles();
        }
        catch (Exception ex)
        {
            Logger.LogError("[Mods] Failed to initialize directories", ex);
        }
    }

    /// <summary>
    /// Recursively deletes all .cache files inside the TF2 custom folder.
    /// TF2 and Steam leave these behind and they can cause issues / clutter.
    /// </summary>
    private void CleanCacheFiles()
    {
        try
        {
            if (!Directory.Exists(CustomFolderPath)) return;

            var cacheFiles = Directory.GetFiles(CustomFolderPath, "*.cache", SearchOption.AllDirectories);
            int deleted = 0;

            foreach (var cacheFile in cacheFiles)
            {
                try
                {
                    File.Delete(cacheFile);
                    deleted++;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[Mods] Could not delete cache file: {cacheFile}", ex);
                }
            }

            if (deleted > 0)
                Logger.LogInfo($"[Mods] Cleaned {deleted} .cache file(s) from {CustomFolderPath}");
        }
        catch (Exception ex)
        {
            Logger.LogError("[Mods] Failed to clean cache files", ex);
        }
    }

    /// <summary>
    /// Scans both custom/ (enabled) and custom/disabled/ (disabled) folders for mods.
    /// </summary>
    public List<ModModel> GetInstalledMods()
    {
        var mods = new List<ModModel>();

        try
        {
            // --- Scan ENABLED mods (in custom/ directly, excluding the disabled/ subfolder) ---
            foreach (var vpkFile in Directory.GetFiles(CustomFolderPath, "*.vpk", SearchOption.TopDirectoryOnly))
            {
                // Skip numbered chunk files (_000, _001, _002, etc.) — only the _dir.vpk represents the set
                if (IsVpkChunkFile(vpkFile)) continue;

                // Skip Casual Preloader build artifacts — they're managed by the
                // preloader panel, not the mod library
                if (IsPreloaderArtifactVpk(vpkFile)) continue;

                var mod = CreateModFromVpk(vpkFile);
                if (mod != null)
                {
                    mod.IsEnabled = true;
                    mods.Add(mod);
                }
            }

            foreach (var dir in Directory.GetDirectories(CustomFolderPath))
            {
                var dirName = Path.GetFileName(dir);

                // Skip hidden dirs and the disabled subfolder
                if (dirName.StartsWith(".") ||
                    string.Equals(dirName, "disabled", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip Casual Preloader managed folders (backup-mainmenu marker
                // folder and preloader-installed HUDs)
                if (IsPreloaderArtifactFolder(dir)) continue;

                var mod = CreateModFromFolder(dir);
                if (mod != null)
                {
                    mod.IsEnabled = true;
                    mods.Add(mod);
                }
            }

            // --- Scan DISABLED mods (in custom/disabled/) ---
            if (Directory.Exists(DisabledFolderPath))
            {
                foreach (var vpkFile in Directory.GetFiles(DisabledFolderPath, "*.vpk", SearchOption.TopDirectoryOnly))
                {
                    // Skip numbered chunk files
                    if (IsVpkChunkFile(vpkFile)) continue;

                    if (IsPreloaderArtifactVpk(vpkFile)) continue;

                    var mod = CreateModFromVpk(vpkFile);
                    if (mod != null)
                    {
                        mod.IsEnabled = false;
                        mods.Add(mod);
                    }
                }

                foreach (var dir in Directory.GetDirectories(DisabledFolderPath))
                {
                    var dirName = Path.GetFileName(dir);
                    if (dirName.StartsWith(".")) continue;

                    var mod = CreateModFromFolder(dir);
                    if (mod != null)
                    {
                        mod.IsEnabled = false;
                        mods.Add(mod);
                    }
                }
            }

            Logger.LogInfo($"[Mods] Found {mods.Count} mods ({mods.Count(m => m.IsEnabled)} enabled, {mods.Count(m => !m.IsEnabled)} disabled)");
        }
        catch (Exception ex)
        {
            Logger.LogError("[Mods] Failed to scan for mods", ex);
        }

        return mods;
    }

    /// <summary>
    /// Returns true if this VPK is a numbered data chunk that belongs to a multi-file
    /// VPK set. A chunk only "counts" as a chunk when its companion <c>_dir.vpk</c>
    /// exists next to it — a lone <c>mymod_001.vpk</c> with no <c>mymod_dir.vpk</c>
    /// alongside is just a normal single-file VPK and must still be shown.
    /// </summary>
    /// <summary>
    /// True for VPKs generated by the Casual Preloader engine — those belong to
    /// the preloader panel lifecycle (apply/remove), not the mod library.
    /// </summary>
    private static bool IsPreloaderArtifactVpk(string vpkPath)
    {
        var name = Path.GetFileName(vpkPath);
        return name.StartsWith("_casual_preloader", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "_QuickPrecache.vpk", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "QuickPrecache.vpk", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True for folders the Casual Preloader engine manages inside tf/custom:
    /// its backup-mainmenu marker folder, and HUD folders it installed
    /// (mod.json contains "preloader_installed": true).
    /// </summary>
    private static bool IsPreloaderArtifactFolder(string folderPath)
    {
        var name = Path.GetFileName(folderPath);
        if (name.StartsWith("zz_run_preloader_again", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            var modJson = Path.Combine(folderPath, "mod.json");
            if (!File.Exists(modJson)) return false;

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(modJson));
            return doc.RootElement.TryGetProperty("preloader_installed", out var flag)
                && flag.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch
        {
            // unreadable mod.json — treat as a normal mod folder
            return false;
        }
    }

    private static bool IsVpkChunkFile(string vpkPath)
    {
        var name = Path.GetFileNameWithoutExtension(vpkPath);
        var match = Regex.Match(name, @"^(.*)_\d{3}$");
        if (!match.Success) return false;

        var dir = Path.GetDirectoryName(vpkPath);
        if (string.IsNullOrEmpty(dir)) return false;

        var companionDirVpk = Path.Combine(dir, $"{match.Groups[1].Value}_dir.vpk");
        return File.Exists(companionDirVpk);
    }

    private ModModel? CreateModFromVpk(string vpkPath)
    {
        try
        {
            var rawName = Path.GetFileNameWithoutExtension(vpkPath);

            // Strip _dir suffix for display (multi-file VPK sets use modname_dir.vpk as index)
            var isMultiFile = rawName.EndsWith("_dir", StringComparison.OrdinalIgnoreCase);
            var displayName = isMultiFile
                ? rawName[..^4]  // remove "_dir"
                : rawName;

            var fileInfo = new FileInfo(vpkPath);

            // For multi-file VPK sets, sum all chunk files for accurate size
            long totalSize = fileInfo.Length;
            if (isMultiFile)
            {
                var baseName = rawName[..^4]; // base without _dir
                var dir = Path.GetDirectoryName(vpkPath) ?? string.Empty;
                totalSize += Directory.GetFiles(dir, $"{baseName}_*.vpk", SearchOption.TopDirectoryOnly)
                    .Where(f => Regex.IsMatch(Path.GetFileNameWithoutExtension(f), @"_\d{3}$"))
                    .Sum(f => new FileInfo(f).Length);
            }

            // Try to find a local thumbnail sibling (mymod.png/jpg) next to the vpk
            var localThumb = FindLocalThumbnailForFile(vpkPath);

            return new ModModel
            {
                Name = displayName,
                Author = "Unknown",
                Description = isMultiFile ? "Multi-file VPK mod" : "VPK mod file",
                Version = "1.0.0",
                ModPath = vpkPath,
                LastModified = fileInfo.LastWriteTime,
                ModType = ModType.Vpk,
                ThumbnailPath = localThumb,
                SizeBytes = totalSize,
                Categories = new ObservableCollection<string> { "VPK" }
            };
        }
        catch (Exception ex)
        {
            Logger.LogError($"[Mods] Failed to create mod from VPK: {vpkPath}", ex);
            return null;
        }
    }

    private ModModel? CreateModFromFolder(string folderPath)
    {
        try
        {
            var folderName = Path.GetFileName(folderPath);
            var dirInfo = new DirectoryInfo(folderPath);

            // Try to find metadata file
            string author = "Unknown", description = "Folder-based mod", version = "1.0.0";
            var metadataFile = Directory.GetFiles(folderPath, "*.txt", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f =>
                {
                    var name = Path.GetFileNameWithoutExtension(f).ToLower();
                    return name.Contains("modinfo") || name.Contains("info") || name == "readme";
                });

            if (metadataFile != null)
            {
                try
                {
                    foreach (var line in File.ReadAllLines(metadataFile))
                    {
                        if (line.StartsWith("author", StringComparison.OrdinalIgnoreCase) && line.Contains(':'))
                            author = line.Split(':', 2)[1].Trim();
                        else if (line.StartsWith("description", StringComparison.OrdinalIgnoreCase) && line.Contains(':'))
                            description = line.Split(':', 2)[1].Trim();
                        else if (line.StartsWith("version", StringComparison.OrdinalIgnoreCase) && line.Contains(':'))
                            version = line.Split(':', 2)[1].Trim();
                    }
                }
                catch { /* use defaults */ }
            }

            // Thumbnail — search common candidate filenames and subfolders
            var thumbnail = FindLocalThumbnailForFolder(folderPath);

            // Folder size
            long folderSize = 0;
            try { folderSize = dirInfo.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
            catch { }

            return new ModModel
            {
                Name = folderName,
                Author = author,
                Description = description,
                Version = version,
                ModPath = folderPath,
                LastModified = dirInfo.LastWriteTime,
                ModType = ModType.Folder,
                ThumbnailPath = thumbnail,
                SizeBytes = folderSize,
                Categories = new ObservableCollection<string> { "Folder" }
            };
        }
        catch (Exception ex)
        {
            Logger.LogError($"[Mods] Failed to create mod from folder: {folderPath}", ex);
            return null;
        }
    }

    /// <summary>
    /// Toggles a mod between enabled and disabled by physically moving it
    /// between custom/ and custom/disabled/.
    /// For multi-file VPK sets (_dir.vpk + _000.vpk, _001.vpk, ...),
    /// all chunks are moved together.
    /// </summary>
    public void ToggleMod(ModModel mod)
    {
        try
        {
            if (string.IsNullOrEmpty(mod.ModPath))
            {
                Logger.LogWarning("[Mods] Cannot toggle mod with empty path");
                return;
            }

            var itemName = Path.GetFileName(mod.ModPath);
            var sourceDir = Path.GetDirectoryName(mod.ModPath) ?? string.Empty;
            var targetDir = mod.IsEnabled ? DisabledFolderPath : CustomFolderPath;

            // Collect all files to move (the mod file itself + any VPK chunks)
            var filesToMove = new List<string> { mod.ModPath };

            // If this is a multi-file VPK (_dir.vpk), also move the chunk files
            var nameWithoutExt = Path.GetFileNameWithoutExtension(mod.ModPath);
            if (nameWithoutExt.EndsWith("_dir", StringComparison.OrdinalIgnoreCase))
            {
                var baseName = nameWithoutExt[..^4]; // remove "_dir"
                var chunkFiles = Directory.GetFiles(sourceDir, $"{baseName}_*.vpk", SearchOption.TopDirectoryOnly)
                    .Where(f => IsVpkChunkFile(f));
                filesToMove.AddRange(chunkFiles);
            }

            // Move all collected files
            foreach (var filePath in filesToMove)
            {
                var destPath = Path.Combine(targetDir, Path.GetFileName(filePath));
                MoveItem(filePath, destPath);
            }

            // Update the mod's tracked path (always the _dir.vpk)
            mod.ModPath = Path.Combine(targetDir, itemName);
            mod.IsEnabled = !mod.IsEnabled;

            Logger.LogInfo($"[Mods] {(mod.IsEnabled ? "Enabled" : "Disabled")} mod: {mod.Name} ({filesToMove.Count} file(s) moved)");
        }
        catch (Exception ex)
        {
            Logger.LogError($"[Mods] Failed to toggle: {mod.Name}", ex);
        }
    }

    private static void MoveItem(string source, string destination)
    {
        // Guard: never operate if source and destination are the same path
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogWarning($"[Mods] MoveItem: source and destination are the same, skipping. Path: {source}");
            return;
        }

        if (File.Exists(source))
        {
            File.Move(source, destination, true);
        }
        else if (Directory.Exists(source))
        {
            if (Directory.Exists(destination))
                Directory.Delete(destination, true);
            Directory.Move(source, destination);
        }
        else
        {
            Logger.LogWarning($"[Mods] MoveItem: source does not exist: {source}");
        }
    }

    /// <summary>
    /// Installs a mod by copying a file or folder into the TF2 custom directory.
    /// </summary>
    public bool InstallMod(string sourcePath)
    {
        try
        {
            if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
            {
                Logger.LogError($"[Mods] Source path does not exist: {sourcePath}");
                return false;
            }

            var fileName = Path.GetFileName(sourcePath);
            var destPath = Path.Combine(CustomFolderPath, fileName);

            if (File.Exists(sourcePath))
            {
                File.Copy(sourcePath, destPath, true);
                Logger.LogInfo($"[Mods] Installed file: {fileName}");
            }
            else
            {
                if (Directory.Exists(destPath))
                    Directory.Delete(destPath, true);
                CopyDirectory(sourcePath, destPath);
                Logger.LogInfo($"[Mods] Installed folder: {fileName}");
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError($"[Mods] Failed to install from: {sourcePath}", ex);
            return false;
        }
    }

    /// <summary>
    /// Removes a mod from disk permanently. For multi-file VPK sets
    /// (<c>name_dir.vpk</c> + <c>name_000.vpk</c>, <c>name_001.vpk</c>, …),
    /// every chunk file is deleted alongside the index file so no orphans
    /// are left behind.
    /// </summary>
    public bool RemoveMod(ModModel mod)
    {
        try
        {
            if (string.IsNullOrEmpty(mod.ModPath))
                return false;

            if (Directory.Exists(mod.ModPath))
            {
                Directory.Delete(mod.ModPath, true);
                Logger.LogInfo($"[Mods] Removed folder: {mod.Name}");
                return true;
            }

            if (!File.Exists(mod.ModPath))
            {
                Logger.LogWarning($"[Mods] Cannot remove '{mod.Name}' — path does not exist: {mod.ModPath}");
                return false;
            }

            // Collect the index file and any companion chunk files
            var filesToDelete = new List<string> { mod.ModPath };
            var nameWithoutExt = Path.GetFileNameWithoutExtension(mod.ModPath);
            if (nameWithoutExt.EndsWith("_dir", StringComparison.OrdinalIgnoreCase))
            {
                var baseName = nameWithoutExt[..^4];
                var dir = Path.GetDirectoryName(mod.ModPath) ?? string.Empty;
                var chunkFiles = Directory.GetFiles(dir, $"{baseName}_*.vpk", SearchOption.TopDirectoryOnly)
                    .Where(IsVpkChunkFile);
                filesToDelete.AddRange(chunkFiles);
            }

            foreach (var path in filesToDelete)
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[Mods] Failed to delete '{Path.GetFileName(path)}' while removing '{mod.Name}': {ex.Message}");
                }
            }

            Logger.LogInfo($"[Mods] Removed: {mod.Name} ({filesToDelete.Count} file(s) deleted)");
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError($"[Mods] Failed to remove: {mod.Name}", ex);
            return false;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    public void RefreshMods()
    {
        try
        {
            var vpkCount = Directory.Exists(CustomFolderPath)
                ? Directory.GetFiles(CustomFolderPath, "*.vpk", SearchOption.TopDirectoryOnly).Length
                : -1;
            var folderCount = Directory.Exists(CustomFolderPath)
                ? Directory.GetDirectories(CustomFolderPath).Length
                : -1;
            Logger.LogInfo($"[Mods] Refresh scan target: '{CustomFolderPath}' (top-level: {vpkCount} .vpk file(s), {folderCount} folder(s))");
        }
        catch (Exception ex)
        {
            Logger.LogWarning("[Mods] Failed to enumerate custom folder during refresh", ex);
        }
    }
}

