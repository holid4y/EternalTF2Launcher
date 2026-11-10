using System.Diagnostics;
using Eternal.Core.Logging;

namespace Eternal.Core.Platform;

public static class Shell
{
    /// <summary>Opens a web page. Links from feeds and mod pages come through here, so nothing but http and https gets past.</summary>
    public static bool OpenUrl(string? url, string? logAs = null) => Open(url, logAs, Uri.UriSchemeHttp, Uri.UriSchemeHttps);

    /// <summary>Hands a steam:// link the launcher built itself to the Steam client.</summary>
    public static bool OpenSteam(string url, string? logAs = null) => Open(url, logAs, "steam");

    private static bool Open(string? url, string? logAs, params string[] schemes)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !schemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            Log.Warn($"Refused to open link: {logAs ?? url}");
            return false;
        }
        return Start(uri.AbsoluteUri, logAs);
    }

    public static bool OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return false;
        return Start(Path.GetFullPath(path));
    }

    private static bool Start(string target, string? logAs = null)
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
            Log.Warn($"Couldn't open {logAs ?? target}", ex);
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
                }
            }
        }
        return null;
    }
}
