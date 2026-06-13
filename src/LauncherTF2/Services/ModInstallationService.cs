using LauncherTF2.Core;
using SharpCompress.Archives;
using System.IO;
using System.IO.Compression;

namespace LauncherTF2.Services;

/// <summary>
/// Handles mod installation from files, folders, and archives.
/// Supports: VPK, ZIP, RAR, 7z formats and raw folder drops.
/// </summary>
public class ModInstallationService
{
    private readonly ModManagerService _modManager;

    // TF2 mods always contain at least one of these subdirectories
    private static readonly string[] Tf2KnownSubdirs =
    [
        "materials", "models", "sound", "scripts", "cfg",
        "particles", "resource", "maps", "media", "expressions"
    ];

    public ModInstallationService(ModManagerService modManager)
    {
        _modManager = modManager;
    }

    /// <summary>
    /// Installs mods from the given file/folder paths (typically from a drag-and-drop).
    /// Each path is processed independently — one failure won't block the others.
    /// </summary>
    public async Task<(int Success, int Failed)> InstallFromPathsAsync(string[] paths)
    {
        int success = 0, fail = 0;
        Logger.LogInfo($"[ModInstaller] Processing {paths.Length} dropped item(s)");

        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    // Direct folder drop — install as-is
                    Logger.LogInfo($"[ModInstaller] Installing folder: {Path.GetFileName(path)}");
                    if (_modManager.InstallMod(path)) success++; else fail++;
                }
                else if (File.Exists(path))
                {
                    var ext = Path.GetExtension(path).ToLowerInvariant();
                    switch (ext)
                    {
                        case ".vpk":
                            Logger.LogInfo($"[ModInstaller] Installing VPK: {Path.GetFileName(path)}");
                            if (_modManager.InstallMod(path)) success++; else fail++;
                            break;

                        case ".zip":
                            Logger.LogInfo($"[ModInstaller] Extracting ZIP: {Path.GetFileName(path)}");
                            var zipResult = await Task.Run(() => ExtractZip(path));
                            success += zipResult.Success;
                            fail += zipResult.Failed;
                            break;

                        case ".rar" or ".7z" or ".7zip":
                            Logger.LogInfo($"[ModInstaller] Extracting archive: {Path.GetFileName(path)}");
                            var archiveResult = await ExtractArchiveAsync(path);
                            success += archiveResult.Success;
                            fail += archiveResult.Failed;
                            break;

                        default:
                            Logger.LogWarning($"[ModInstaller] Skipped unsupported file type '{ext}': {Path.GetFileName(path)}");
                            fail++;
                            break;
                    }
                }
                else
                {
                    Logger.LogWarning($"[ModInstaller] Path does not exist: {path}");
                    fail++;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[ModInstaller] Failed to install: {Path.GetFileName(path)}", ex);
                fail++;
            }
        }

        Logger.LogInfo($"[ModInstaller] Finished — {success} installed, {fail} failed");
        return (success, fail);
    }

    // Extracts a .zip into a temp folder and installs the contents
    private (int Success, int Failed) ExtractZip(string zipPath)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tf2mod_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            ZipFile.ExtractToDirectory(zipPath, tempDir, true);
            return InstallExtractedContents(tempDir, Path.GetFileNameWithoutExtension(zipPath));
        }
        catch (Exception ex)
        {
            Logger.LogError($"[ModInstaller] ZIP extraction failed: {Path.GetFileName(zipPath)}", ex);
            return (0, 1);
        }
        finally
        {
            TryCleanup(tempDir);
        }
    }

    // Extracts RAR/7z archives using SharpCompress
    private async Task<(int Success, int Failed)> ExtractArchiveAsync(string archivePath)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tf2mod_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            await Task.Run(() =>
            {
                using var archive = ArchiveFactory.OpenArchive(archivePath);
                foreach (var entry in archive.Entries)
                {
                    if (!entry.IsDirectory && !string.IsNullOrEmpty(entry.Key))
                    {
                        // Prevent zip-slip attacks by validating the output path
                        var destPath = GetSafeExtractPath(tempDir, entry.Key);
                        if (destPath == null)
                        {
                            Logger.LogWarning($"[ModInstaller] Blocked suspicious archive path: {entry.Key}");
                            continue;
                        }

                        var destDir = Path.GetDirectoryName(destPath);
                        if (destDir == null)
                        {
                            Logger.LogWarning($"[ModInstaller] Could not resolve directory for: {entry.Key}");
                            continue;
                        }
                        Directory.CreateDirectory(destDir);

                        using var entryStream = entry.OpenEntryStream();
                        using var fileStream = File.Create(destPath);
                        entryStream.CopyTo(fileStream);
                    }
                }
            });

            return InstallExtractedContents(tempDir, Path.GetFileNameWithoutExtension(archivePath));
        }
        catch (Exception ex)
        {
            Logger.LogError($"[ModInstaller] Archive extraction failed: {Path.GetFileName(archivePath)}", ex);
            return (0, 1);
        }
        finally
        {
            TryCleanup(tempDir);
        }
    }

    /// <summary>
    /// Figures out how to install extracted contents using a priority system:
    ///   1. VPK files anywhere → copy each to custom/
    ///   2. Folders with TF2 structure (materials/, models/, etc.) → install as mod
    ///   3. Fallback → single top-level folder or wrap loose files
    /// </summary>
    private (int Success, int Failed) InstallExtractedContents(string tempDir, string archiveName)
    {
        int success = 0, fail = 0;

        // Check for VPK files first — they're self-contained mods
        var vpkFiles = Directory.GetFiles(tempDir, "*.vpk", SearchOption.AllDirectories);
        if (vpkFiles.Length > 0)
        {
            foreach (var vpk in vpkFiles)
            {
                if (_modManager.InstallMod(vpk)) success++; else fail++;
                Logger.LogInfo($"[ModInstaller] Installed VPK from archive: {Path.GetFileName(vpk)}");
            }
            return EnsureResult(success, fail);
        }

        // Look for folders that look like TF2 mods (contain materials/, models/, etc.)
        var tf2ModFolders = FindTf2ModFolders(tempDir);
        if (tf2ModFolders.Count > 0)
        {
            foreach (var folder in tf2ModFolders)
            {
                if (_modManager.InstallMod(folder)) success++; else fail++;
                Logger.LogInfo($"[ModInstaller] Installed mod folder: {Path.GetFileName(folder)}");
            }
            return EnsureResult(success, fail);
        }

        // Fallback: assume the archive contains a single mod
        var topDirs = Directory.GetDirectories(tempDir);
        var topFiles = Directory.GetFiles(tempDir);

        if (topDirs.Length == 1 && topFiles.Length == 0)
        {
            // Single folder inside archive — install it directly
            if (_modManager.InstallMod(topDirs[0])) success++; else fail++;
        }
        else if (topDirs.Length > 0 || topFiles.Length > 0)
        {
            // Multiple loose items — wrap them in a folder named after the archive
            var wrapperDir = Path.Combine(tempDir, archiveName);
            if (!Directory.Exists(wrapperDir))
            {
                Directory.CreateDirectory(wrapperDir);
                foreach (var file in topFiles)
                    File.Move(file, Path.Combine(wrapperDir, Path.GetFileName(file)));
                foreach (var dir in topDirs)
                    Directory.Move(dir, Path.Combine(wrapperDir, Path.GetFileName(dir)));
            }
            Logger.LogInfo($"[ModInstaller] Wrapped loose files into folder: {archiveName}");
            if (_modManager.InstallMod(wrapperDir)) success++; else fail++;
        }
        else
        {
            Logger.LogWarning("[ModInstaller] Archive extracted but contained no installable content");
            fail++;
        }

        return EnsureResult(success, fail);
    }

    // Ensures we always report at least one result
    private static (int, int) EnsureResult(int success, int fail) =>
        success == 0 && fail == 0 ? (0, 1) : (success, fail);

    // Recursively searches for folders that look like TF2 mods (up to 2 levels deep)
    private static List<string> FindTf2ModFolders(string rootDir)
    {
        var result = new List<string>();

        if (IsTf2ModFolder(rootDir))
        {
            result.Add(rootDir);
            return result;
        }

        foreach (var dir in Directory.GetDirectories(rootDir))
        {
            if (IsTf2ModFolder(dir))
            {
                result.Add(dir);
            }
            else
            {
                // Check one level deeper (archive → outer-folder → mod-folder)
                foreach (var subDir in Directory.GetDirectories(dir))
                {
                    if (IsTf2ModFolder(subDir))
                        result.Add(subDir);
                }
            }
        }

        return result;
    }

    // A folder is a TF2 mod if it contains known subdirectories like materials/, models/, etc.
    private static bool IsTf2ModFolder(string path)
    {
        if (!Directory.Exists(path)) return false;
        var childNames = Directory.GetDirectories(path)
            .Select(d => Path.GetFileName(d).ToLowerInvariant())
            .ToHashSet();
        return Tf2KnownSubdirs.Any(known => childNames.Contains(known));
    }

    // Silently cleans up temp directories — failures are expected and harmless
    private static void TryCleanup(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
            // Temp folder cleanup is best-effort
        }
    }

    /// <summary>
    /// Validates that an archive entry path doesn't escape the extraction root.
    /// Prevents "zip-slip" directory traversal attacks.
    /// </summary>
    private static string? GetSafeExtractPath(string rootDirectory, string relativeEntryPath)
    {
        if (string.IsNullOrWhiteSpace(relativeEntryPath))
            return null;

        var sanitizedRelative = relativeEntryPath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

        var fullRoot = Path.GetFullPath(rootDirectory);
        var fullDestination = Path.GetFullPath(Path.Combine(fullRoot, sanitizedRelative));
        var rootedPrefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        return fullDestination.StartsWith(rootedPrefix, StringComparison.OrdinalIgnoreCase)
            ? fullDestination
            : null;
    }
}
