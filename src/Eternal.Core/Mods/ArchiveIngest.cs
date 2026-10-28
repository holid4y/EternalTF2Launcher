using Eternal.Core.Logging;
using Eternal.Core.Platform;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Eternal.Core.Mods;

public sealed class ArchiveException(string message) : Exception(message);

/// <summary>
/// Unpacks ZIP, RAR and 7z downloads into a staging folder named after the
/// archive, which the engine then imports as one unit. Every entry is checked
/// before anything is written: sizes, counts and that it stays inside the folder.
/// </summary>
public static class ArchiveIngest
{
    public const long MaxTotalBytes = 8L * 1024 * 1024 * 1024;
    public const int MaxEntries = 200_000;
    public static readonly string[] Extensions = [".zip", ".rar", ".7z"];

    public static bool IsArchive(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <returns>The folder holding the archive's contents (named after the archive).</returns>
    public static string Extract(string archivePath, string stagingRoot, IProgress<double>? progress = null, CancellationToken cancellation = default)
    {
        var name = SafeFolderName(Path.GetFileNameWithoutExtension(archivePath));
        var destination = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N")[..8], name);
        Directory.CreateDirectory(destination);

        try
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath, new ReaderOptions());
            var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
            if (entries.Count > MaxEntries)
                throw new ArchiveException($"{Path.GetFileName(archivePath)} has too many files ({entries.Count}).");
            if (entries.Any(e => e.IsEncrypted))
                throw new ArchiveException($"{Path.GetFileName(archivePath)} is password-protected.");

            long total = 0;
            var targets = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                total += Math.Max(0, entry.Size);
                if (total > MaxTotalBytes)
                    throw new ArchiveException($"{Path.GetFileName(archivePath)} expands to more than {MaxTotalBytes >> 30} GiB.");
                if (entry.Key is null)
                    continue;
                targets[entry.Key] = ResolveTarget(destination, entry.Key);
            }

            // Solid RAR/7z archives decompress in one pass; random access would
            // re-read the whole stream for every file.
            long written = 0;
            void Write(string? key, long size, Action<Stream> copy)
            {
                cancellation.ThrowIfCancellationRequested();
                if (key is null || !targets.TryGetValue(key, out var target))
                    return;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (var output = File.Create(target))
                    copy(output);
                written += Math.Max(0, size);
                if (total > 0)
                    progress?.Report((double)written / total);
            }

            if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
            {
                using var reader = archive.ExtractAllEntries();
                while (reader.MoveToNextEntry())
                {
                    if (!reader.Entry.IsDirectory)
                        Write(reader.Entry.Key, reader.Entry.Size, reader.WriteEntryTo);
                }
            }
            else
            {
                foreach (var entry in entries)
                {
                    Write(entry.Key, entry.Size, output =>
                    {
                        using var input = entry.OpenEntryStream();
                        input.CopyTo(output);
                    });
                }
            }
            return destination;
        }
        catch (Exception ex) when (ex is not ArchiveException and not OperationCanceledException)
        {
            Log.Warn($"Extracting {archivePath} failed", ex);
            TryDelete(Path.GetDirectoryName(destination)!);
            throw new ArchiveException($"{Path.GetFileName(archivePath)} could not be read: {ex.Message}");
        }
        catch
        {
            TryDelete(Path.GetDirectoryName(destination)!);
            throw;
        }
    }

    internal static string ResolveTarget(string destination, string key)
    {
        var relative = key.Replace('\\', '/').TrimStart('/');
        var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "." or "..") || relative.Contains(':'))
            throw new ArchiveException($"Unsafe path inside archive: {key}");

        var target = Path.GetFullPath(Path.Combine([destination, .. parts]));
        if (!AppPaths.IsInside(destination, target))
            throw new ArchiveException($"Unsafe path inside archive: {key}");
        return target;
    }

    internal static string SafeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var cleaned = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim(' ', '.');
        return cleaned.Length == 0 ? "mod" : cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }

    public static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug($"Couldn't remove staging folder {directory}: {ex.Message}");
        }
    }
}
