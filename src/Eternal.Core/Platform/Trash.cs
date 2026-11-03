using System.Globalization;
using System.Text;

namespace Eternal.Core.Platform;

/// <summary>
/// Moves files to the desktop's trash so a delete can be undone: the Recycle Bin
/// on Windows, the freedesktop.org trash on Linux (what file managers use).
/// </summary>
public static class Trash
{
    public static void Move(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException("There's no such file.", full);
        if (OperatingSystem.IsWindows())
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(full,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            return;
        }
        MoveToFreedesktopTrash(full, HomeTrash());
    }

    private static string HomeTrash()
    {
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(data) || !Path.IsPathFullyQualified(data))
            data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(data, "Trash");
    }

    internal static string MoveToFreedesktopTrash(string full, string trash)
    {
        var files = Path.Combine(trash, "files");
        var info = Path.Combine(trash, "info");
        Directory.CreateDirectory(files);
        Directory.CreateDirectory(info);

        var stem = Path.GetFileNameWithoutExtension(full);
        var extension = Path.GetExtension(full);
        for (var n = 1; n < 1000; n++)
        {
            var name = n == 1 ? stem + extension : $"{stem}.{n.ToString(CultureInfo.InvariantCulture)}{extension}";
            var infoPath = Path.Combine(info, name + ".trashinfo");
            FileStream stream;
            try
            {
                // Creating the info file first claims the name, as the spec asks.
                stream = new FileStream(infoPath, FileMode.CreateNew, FileAccess.Write);
            }
            catch (IOException) when (File.Exists(infoPath))
            {
                continue;
            }
            using (stream)
            {
                var text = $"[Trash Info]\nPath={Escape(full)}\nDeletionDate={DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}\n";
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
            var target = Path.Combine(files, name);
            try
            {
                if (File.Exists(target))
                    throw new IOException("Trash name taken.");
                File.Move(full, target);
                return target;
            }
            catch
            {
                File.Delete(infoPath);
                throw;
            }
        }
        throw new IOException("The trash has too many files with that name.");
    }

    private static string Escape(string path)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(path))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.' or '~')
                sb.Append(c);
            else
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
