namespace Eternal.Core.Platform;

internal static class Proc
{
    /// <summary>Linux only: /proc/&lt;pid&gt;/comm is cheap to read and keeps names like "tf_win64.exe" intact under Proton.</summary>
    public static bool AnyNamed(params string[] names)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                var pid = Path.GetFileName(dir);
                if (pid.Length == 0 || !char.IsAsciiDigit(pid[0]))
                    continue;
                try
                {
                    if (Array.IndexOf(names, File.ReadAllText(Path.Combine(dir, "comm")).TrimEnd('\n')) >= 0)
                        return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The process exited between listing and reading.
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
