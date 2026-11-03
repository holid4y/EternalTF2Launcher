namespace Eternal.Core.Platform;

internal static class Proc
{
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
