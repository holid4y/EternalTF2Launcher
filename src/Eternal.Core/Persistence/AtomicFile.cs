using System.Text;

namespace Eternal.Core.Persistence;

/// <summary>
/// Writes to a temporary sibling, flushes it and renames it over the target,
/// so a crash leaves either the old file or the new one. Sharing violations
/// (antivirus, indexers, the game) are retried briefly.
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static void WriteAllText(string path, string contents, bool keepBackup = true) =>
        WriteAllBytes(path, Utf8.GetBytes(contents), keepBackup);

    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes, bool keepBackup = true)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            Retry(() =>
            {
                if (keepBackup && File.Exists(fullPath))
                    File.Replace(temp, fullPath, fullPath + ".bak", ignoreMetadataErrors: true);
                else
                    File.Move(temp, fullPath, overwrite: true);
            });
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Reads a file, retrying transient locks. Missing files return null.</summary>
    public static string? ReadAllTextOrNull(string path)
    {
        try
        {
            return Retry(() => File.ReadAllText(path));
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public static void Retry(Action action) => Retry(() => { action(); return true; });

    public static T Retry<T>(Func<T> action, int attempts = 5)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception ex) when (attempt < attempts && IsTransient(ex))
            {
                Thread.Sleep(40 * (1 << attempt));
            }
        }
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException or PathTooLongException => false,
        // Windows reports sharing violations (32), lock violations (33) and
        // scanner-held handles as access denied; none of these exist on Linux.
        UnauthorizedAccessException => OperatingSystem.IsWindows(),
        IOException io => OperatingSystem.IsWindows() && (io.HResult & 0xFFFF) is 32 or 33,
        _ => false,
    };
}
