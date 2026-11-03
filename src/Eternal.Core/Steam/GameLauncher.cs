using System.Diagnostics;
using System.Globalization;
using Eternal.Core.Logging;
using Eternal.Core.Platform;
using Eternal.Core.Settings;

namespace Eternal.Core.Steam;

public static class GameLauncher
{
    public static void Launch(string? steamRoot, IReadOnlyList<string> arguments)
    {
        var applaunch = new List<string> { "-applaunch", SteamLocator.Tf2AppId.ToString(CultureInfo.InvariantCulture) };
        applaunch.AddRange(arguments);

        var command = SteamProcess.Command(steamRoot);
        if (command is not null)
        {
            var psi = new ProcessStartInfo(command.Value.FileName) { UseShellExecute = false };
            foreach (var arg in command.Value.Prefix.Concat(applaunch))
                psi.ArgumentList.Add(arg);
            Log.Info($"Launching TF2: {psi.FileName} {string.Join(' ', Redacted(psi.ArgumentList))}");
            Process.Start(psi)?.Dispose();
            return;
        }

        var url = $"steam://rungameid/{SteamLocator.Tf2AppId}";
        if (arguments.Count > 0)
            url += "//" + Uri.EscapeDataString(LaunchOptions.Join(arguments));
        Log.Info($"Launching TF2 via steam://rungameid/{SteamLocator.Tf2AppId} {string.Join(' ', Redacted(arguments))}");
        if (!Shell.OpenUrl(url))
            throw new InvalidOperationException("Steam could not be started. Make sure Steam is installed.");
    }

    internal static IEnumerable<string> Redacted(IEnumerable<string> arguments)
    {
        var hide = false;
        foreach (var arg in arguments)
        {
            yield return hide ? "***" : arg;
            hide = arg.Equals("+password", StringComparison.OrdinalIgnoreCase);
        }
    }
}
