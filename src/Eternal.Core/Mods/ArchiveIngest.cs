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
    public static string Extract(string archivePath, string stagingRoot, IProgress<double>? progress = null,
        CancellationToken cancellation = default, long maxBytes = MaxTotalBytes)
    {
        var name = SafeFolderName(Path.GetFileNameWithoutExtension(archivePath));
        var destination = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N")[..8], name);
        Directory.CreateDirectory(destination);
        var budget = new Budget(maxBytes, Path.GetFileName(archivePath));
        try
        {
            Unpack(archivePath, destination, budget, progress, cancellation);
            UnpackNested(destination, budget, depth: 1, cancellation);
            return destination;
        }
        catch
        {
            TryDelete(Path.GetDirectoryName(destination)!);
            throw;
        }
    }

    // Some uploads put each variant in its own archive inside the download.
    private static void UnpackNested(string folder, Budget budget, int depth, CancellationToken cancellation)
    {
        if (depth > 2)
            return;
        foreach (var inner in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(IsArchive).ToList())
        {
            var target = Path.Combine(Path.GetDirectoryName(inner)!, SafeFolderName(Path.GetFileNameWithoutExtension(inner)));
            for (var n = 2; Directory.Exists(target) || File.Exists(target); n++)
                target = Path.Combine(Path.GetDirectoryName(inner)!, $"{SafeFolderName(Path.GetFileNameWithoutExtension(inner))} {n}");
            Directory.CreateDirectory(target);
            Unpack(inner, target, budget, null, cancellation);
            File.Delete(inner);
            UnpackNested(target, budget, depth + 1, cancellation);
        }
    }

    private static void Unpack(string archivePath, string destination, Budget budget, IProgress<double>? progress, CancellationToken cancellation)
    {
        var label = Path.GetFileName(archivePath);
        try
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath, new ReaderOptions());
            var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
            if (entries.Count > MaxEntries)
                throw new ArchiveException($"{label} has too many files ({entries.Count}).");
            if (entries.Any(e => e.IsEncrypted))
                throw new ArchiveException($"{label} is password-protected.");

            long total = 0;
            var targets = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                total += Math.Max(0, entry.Size);
                if (total > budget.Remaining)
                    throw new ArchiveException($"{label} expands to more than {budget.Limit >> 20} MB.");
                if (entry.Key is null)
                    continue;
                targets[entry.Key] = ResolveTarget(destination, entry.Key);
            }

            // Some zip tools store a folder as an empty file; anything with files inside it is a folder.
            var folders = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var target in targets.Values)
            {
                for (var dir = Path.GetDirectoryName(target); dir is not null && dir.Length > destination.Length; dir = Path.GetDirectoryName(dir))
                    folders.Add(dir);
            }
            foreach (var key in targets.Where(t => folders.Contains(t.Value)).Select(t => t.Key).ToList())
                targets.Remove(key);

            // Solid RAR/7z archives decompress in one pass; random access would
            // re-read the whole stream for every file.
            long written = 0;
            void Write(string? key, long size, Action<Stream> copy)
            {
                cancellation.ThrowIfCancellationRequested();
                if (key is null || !targets.TryGetValue(key, out var target))
                    return;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (var output = new MeteredStream(File.Create(target), budget))
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
        }
        catch (Exception ex) when (ex is not ArchiveException and not OperationCanceledException)
        {
            Log.Warn($"Extracting {archivePath} failed", ex);
            throw new ArchiveException($"{label} could not be read: {ex.Message}");
        }
    }

    /// <summary>What may still be written for one download, counted as it is written: an archive's own size fields can lie.</summary>
    internal sealed class Budget(long limit, string label)
    {
        private long _used;

        public long Limit => limit;
        public long Remaining => limit - _used;

        public void Spend(int bytes)
        {
            _used += bytes;
            if (_used > limit)
                throw new ArchiveException($"{label} expands to more than {limit >> 20} MB.");
        }
    }

    internal sealed class MeteredStream(Stream inner, Budget budget) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            budget.Spend(count);
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            budget.Spend(buffer.Length);
            inner.Write(buffer);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
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
