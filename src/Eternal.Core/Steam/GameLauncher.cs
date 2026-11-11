using System.Diagnostics;
using System.Globalization;
using Eternal.Core.Logging;
using Eternal.Core.Platform;
using Eternal.Core.Settings;

namespace Eternal.Core.Steam;

public static class GameLauncher
{
    public static void Launch(string? steamRoot, Games.GameDefinition game, IReadOnlyList<string> arguments)
    {
        var appId = game.AppId.ToString(CultureInfo.InvariantCulture);
        var applaunch = new List<string> { "-applaunch", appId };
        applaunch.AddRange(arguments);

        var command = SteamProcess.Command(steamRoot);
        if (command is not null)
        {
            var psi = new ProcessStartInfo(command.Value.FileName) { UseShellExecute = false };
            foreach (var arg in command.Value.Prefix.Concat(applaunch))
                psi.ArgumentList.Add(arg);
            Log.Info($"Launching {game.ShortName}: {psi.FileName} {string.Join(' ', Redacted(psi.ArgumentList))}");
            Process.Start(psi)?.Dispose();
            return;
        }

        var url = $"steam://rungameid/{appId}";
        if (arguments.Count > 0)
            url += "//" + Uri.EscapeDataString(LaunchOptions.Join(arguments));
        Log.Info($"Launching {game.ShortName} via steam://rungameid/{appId} {string.Join(' ', Redacted(arguments))}");
        if (!Shell.OpenSteam(url))
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
