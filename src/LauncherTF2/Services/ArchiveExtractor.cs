using System.IO;
using System.IO.Compression;
using LauncherTF2.Core;
using SharpCompress.Archives;

namespace LauncherTF2.Services;

/// <summary>
/// The Casual Preloader engine imports <c>.zip</c>, <c>.vpk</c>, and folders.
/// It does not read RAR/7z. This helper repacks those formats into a temporary
/// <c>.zip</c> so the engine's own (smart) ZIP detection handles the contents —
/// no mod-structure logic is duplicated on the launcher side.
/// </summary>
public static class ArchiveExtractor
{
    /// <summary>True for archive formats the engine can't read and we must repack.</summary>
    public static bool NeedsRepack(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".rar" or ".7z" or ".7zip";
    }

    /// <summary>
    /// Extracts a RAR/7z archive and repacks it into a temporary <c>.zip</c> the
    /// engine can import. Returns the path to the temp zip, or null on failure.
    /// The returned zip lives inside <paramref name="tempRoot"/>; the caller must
    /// delete <paramref name="tempRoot"/> once the import has finished.
    /// </summary>
    public static string? RepackToZip(string archivePath, out string tempRoot)
    {
        tempRoot = Path.Combine(Path.GetTempPath(), $"preloader_import_{Guid.NewGuid():N}");
        var extractDir = Path.Combine(tempRoot, "extracted");

        try
        {
            Directory.CreateDirectory(extractDir);

            using (var archive = ArchiveFactory.OpenArchive(archivePath))
            {
                foreach (var entry in archive.Entries)
                {
                    if (entry.IsDirectory || string.IsNullOrEmpty(entry.Key)) continue;

                    var destPath = GetSafeExtractPath(extractDir, entry.Key);
                    if (destPath == null)
                    {
                        Logger.LogWarning($"[ArchiveExtractor] Blocked suspicious archive path: {entry.Key}");
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    using var entryStream = entry.OpenEntryStream();
                    using var fileStream = File.Create(destPath);
                    entryStream.CopyTo(fileStream);
                }
            }

            if (!Directory.EnumerateFileSystemEntries(extractDir).Any())
            {
                Logger.LogWarning($"[ArchiveExtractor] Archive extracted empty: {Path.GetFileName(archivePath)}");
                return null;
            }

            // Name the zip after the archive so the engine derives a sensible
            // addon name when the contents aren't wrapped in their own folder.
            var safeName = Path.GetFileNameWithoutExtension(archivePath);
            if (string.IsNullOrWhiteSpace(safeName)) safeName = "mod";
            var zipPath = Path.Combine(tempRoot, safeName + ".zip");

            ZipFile.CreateFromDirectory(extractDir, zipPath, CompressionLevel.NoCompression, includeBaseDirectory: false);
            return zipPath;
        }
        catch (Exception ex)
        {
            Logger.LogError($"[ArchiveExtractor] Failed to repack archive: {Path.GetFileName(archivePath)}", ex);
            return null;
        }
    }

    /// <summary>Best-effort recursive cleanup of a temp root created by <see cref="RepackToZip"/>.</summary>
    public static void Cleanup(string? tempRoot)
    {
        if (string.IsNullOrEmpty(tempRoot)) return;
        try
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
        catch
        {
            // temp cleanup is best-effort
        }
    }

    // Validates that an archive entry path doesn't escape the extraction root (zip-slip).
    private static string? GetSafeExtractPath(string rootDirectory, string relativeEntryPath)
    {
        if (string.IsNullOrWhiteSpace(relativeEntryPath)) return null;

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
