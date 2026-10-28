using System.Diagnostics;

namespace Eternal.Core.Steam;

/// <summary>
/// Detects a running TF2: native Windows, native Linux, or the Windows build
/// under Proton. hl2 binaries are left out; since the 64-bit update TF2 no
/// longer uses them, and every other Source game does.
/// </summary>
public static class GameProcess
{
    private static readonly string[] WindowsNames = ["tf_win64", "tf"];
    private static readonly HashSet<string> LinuxNames = new(StringComparer.Ordinal)
    {
        "tf_linux64", "tf_win64.exe", "tf.exe",
    };

    public static bool IsRunning()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var name in WindowsNames)
            {
                var processes = Process.GetProcessesByName(name);
                var found = processes.Length > 0;
                foreach (var p in processes)
                    p.Dispose();
                if (found)
                    return true;
            }
            return false;
        }

        // /proc/<pid>/comm is cheap to read and keeps names like "tf_win64.exe" intact under Proton.
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                var name = Path.GetFileName(dir);
                if (name.Length == 0 || !char.IsAsciiDigit(name[0]))
                    continue;
                try
                {
                    if (LinuxNames.Contains(File.ReadAllText(Path.Combine(dir, "comm")).TrimEnd('\n')))
                        return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // process exited between listing and reading
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        return false;
    }
}
