using System.Diagnostics;
using Eternal.Core.Logging;

namespace Eternal.Core.Platform;

public static class Shell
{
    private static readonly string[] AllowedSchemes = ["http", "https", "steam"];

    /// <summary>Only web and steam:// links: URLs from feeds or APIs must never open local files.</summary>
    public static bool OpenUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !AllowedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            Log.Warn($"Refused to open link: {url}");
            return false;
        }
        return Start(uri.AbsoluteUri);
    }

    public static bool OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return false;
        return Start(Path.GetFullPath(path));
    }

    private static bool Start(string target)
    {
        try
        {
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo(target) { UseShellExecute = true }
                : new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false, ArgumentList = { target } };
            Process.Start(psi)?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Log.Warn($"Couldn't open {target}", ex);
            return false;
        }
    }

    public static string? FindOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", "" } : [""];
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), executable + ext);
                    if (File.Exists(candidate)
                        && !candidate.Contains(@"\Microsoft\WindowsApps\", StringComparison.OrdinalIgnoreCase))
                        return candidate;
                }
                catch (ArgumentException)
                {
                    // malformed PATH entry
                }
            }
        }
        return null;
    }
}
