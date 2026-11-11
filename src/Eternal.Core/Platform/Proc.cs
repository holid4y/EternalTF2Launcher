namespace Eternal.Core.Platform;

internal static class Proc
{
    /// <summary>Linux keeps only the first 15 characters of a process name in comm.</summary>
    private const int CommLength = 15;

    public static bool AnyNamed(params string[] names)
    {
        names = [.. names.Select(n => n.Length > CommLength ? n[..CommLength] : n)];
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
