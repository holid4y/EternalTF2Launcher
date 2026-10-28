using System.Diagnostics;
using System.Globalization;
using Eternal.Core.Logging;
using Eternal.Core.Platform;
using Eternal.Core.Settings;

namespace Eternal.Core.Steam;

/// <summary>
/// Starts TF2 through Steam. The Steam executable with -applaunch passes
/// arguments untouched and never shows Steam's "launch with these
/// arguments?" prompt; the steam:// URL is the fallback.
/// </summary>
public static class GameLauncher
{
    public static void Launch(string? steamRoot, IReadOnlyList<string> arguments)
    {
        var applaunch = new List<string> { "-applaunch", SteamLocator.Tf2AppId.ToString(CultureInfo.InvariantCulture) };
        applaunch.AddRange(arguments);

        var command = SteamCommand(steamRoot);
        if (command is not null)
        {
            var psi = new ProcessStartInfo(command.Value.FileName) { UseShellExecute = false };
            foreach (var arg in command.Value.Prefix.Concat(applaunch))
                psi.ArgumentList.Add(arg);
            Log.Info($"Launching TF2: {psi.FileName} {string.Join(' ', psi.ArgumentList)}");
            Process.Start(psi)?.Dispose();
            return;
        }

        var url = $"steam://rungameid/{SteamLocator.Tf2AppId}";
        if (arguments.Count > 0)
            url += "//" + Uri.EscapeDataString(LaunchOptions.Join(arguments));
        Log.Info($"Launching TF2 via {url}");
        if (!Shell.OpenUrl(url))
            throw new InvalidOperationException("Steam could not be started. Make sure Steam is installed.");
    }

    private static (string FileName, string[] Prefix)? SteamCommand(string? steamRoot)
    {
        if (OperatingSystem.IsWindows())
        {
            var exe = steamRoot is null ? null : Path.Combine(steamRoot, "steam.exe");
            return exe is not null && File.Exists(exe) ? (exe, []) : null;
        }

        var flatpakRoot = Path.Combine(".var", "app", "com.valvesoftware.Steam");
        if (steamRoot is not null && steamRoot.Contains(flatpakRoot, StringComparison.Ordinal) && Shell.FindOnPath("flatpak") is { } flatpak)
            return (flatpak, ["run", "com.valvesoftware.Steam"]);

        return Shell.FindOnPath("steam") is { } steam ? (steam, []) : null;
    }
}
