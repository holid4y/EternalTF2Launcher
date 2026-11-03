using System.Diagnostics;
using Eternal.Core.Logging;
using Eternal.Core.Platform;

namespace Eternal.Core.Steam;

/// <summary>The Steam client itself: whether it runs, and closing or starting it.</summary>
public static class SteamProcess
{
    public static bool IsRunning()
    {
        if (OperatingSystem.IsWindows())
        {
            var processes = Process.GetProcessesByName("steam");
            foreach (var p in processes)
                p.Dispose();
            return processes.Length > 0;
        }
        return Proc.AnyNamed("steam");
    }

    /// <summary>Asks Steam to quit the way its own menu does, and waits until it has.</summary>
    public static async Task<bool> ShutdownAsync(string? steamRoot, TimeSpan timeout, CancellationToken cancellation = default)
    {
        if (!IsRunning())
            return true;
        if (!Run(steamRoot, "-shutdown"))
            return false;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500, cancellation).ConfigureAwait(false);
            if (!IsRunning())
            {
                // Steam writes its files on the way out; give the last write a moment to land.
                await Task.Delay(1500, cancellation).ConfigureAwait(false);
                return true;
            }
        }
        return false;
    }

    public static bool Start(string? steamRoot) => Run(steamRoot);

    private static bool Run(string? steamRoot, params string[] arguments)
    {
        if (Command(steamRoot) is not { } command)
            return false;
        try
        {
            var psi = new ProcessStartInfo(command.FileName) { UseShellExecute = false };
            foreach (var arg in command.Prefix.Concat(arguments))
                psi.ArgumentList.Add(arg);
            Process.Start(psi)?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Log.Warn($"Couldn't run Steam: {ex.Message}");
            return false;
        }
    }

    internal static (string FileName, string[] Prefix)? Command(string? steamRoot)
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
