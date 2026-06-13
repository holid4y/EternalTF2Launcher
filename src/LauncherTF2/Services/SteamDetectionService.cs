using Microsoft.Win32;
using System;
using LauncherTF2.Core;

namespace LauncherTF2.Services;

public class SteamDetectionService
{
    private const long SteamId64Base = 76561197960265728;

    public string? GetActiveSteamId()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            if (key != null)
            {
                var activeUserValue = key.GetValue("ActiveUser");
                if (TryParseAccountId(activeUserValue, out var accountId) && accountId > 0)
                {
                    long steamId64 = SteamId64Base + accountId;
                    Logger.LogInfo($"[Steam] Detected active SteamID: {steamId64}");
                    return steamId64.ToString();
                }
                else
                {
                    Logger.LogDebug("[Steam] ActiveUser value is null or 0");
                }
            }
            else
            {
                Logger.LogDebug("[Steam] Registry key Software\\Valve\\Steam\\ActiveProcess not found");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("[Steam] Error detecting active SteamID", ex);
        }

        Logger.LogDebug("[Steam] Could not detect active SteamID");
        return null;
    }

    private static bool TryParseAccountId(object? activeUserValue, out long accountId)
    {
        accountId = 0;

        if (activeUserValue == null)
            return false;

        return activeUserValue switch
        {
            int value => (accountId = value) >= 0,
            long value => (accountId = value) >= 0,
            uint value => (accountId = value) >= 0,
            string text when long.TryParse(text, out var parsed) => (accountId = parsed) >= 0,
            _ => false
        };
    }

    public string? GetSteamInstallPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key != null)
            {
                var steamPath = key.GetValue("SteamPath") as string;
                if (!string.IsNullOrWhiteSpace(steamPath))
                {
                    Logger.LogInfo($"[Steam] Detected installation path: {steamPath}");
                    return steamPath;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("[Steam] Error detecting installation path", ex);
        }

        Logger.LogDebug("[Steam] Could not detect installation path");
        return null;
    }

    /// <summary>
    /// The logged-in user's Steam display name, read from Steam's local
    /// <c>config\loginusers.vdf</c> (no network / API key needed). Prefers the
    /// active account, then the entry marked MostRecent, then the first user.
    /// Returns null when Steam isn't installed or the file can't be parsed.
    /// </summary>
    public string? GetPersonaName()
    {
        try
        {
            var steamPath = GetSteamInstallPath();
            if (string.IsNullOrWhiteSpace(steamPath)) return null;

            var vdfPath = System.IO.Path.Combine(steamPath, "config", "loginusers.vdf");
            if (!System.IO.File.Exists(vdfPath)) return null;

            var text = System.IO.File.ReadAllText(vdfPath);

            // "76561198..." { ... "PersonaName" "name" ... "MostRecent" "1" ... }
            var users = System.Text.RegularExpressions.Regex.Matches(
                text,
                "\"(?<id>7656\\d{13})\"\\s*\\{(?<body>[^}]*)\\}",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            if (users.Count == 0) return null;

            var activeId = GetActiveSteamId();
            string? best = null;

            foreach (System.Text.RegularExpressions.Match user in users)
            {
                var body = user.Groups["body"].Value;
                var nameMatch = System.Text.RegularExpressions.Regex.Match(body, "\"PersonaName\"\\s*\"([^\"]*)\"");
                if (!nameMatch.Success) continue;
                var name = nameMatch.Groups[1].Value;

                if (activeId != null && user.Groups["id"].Value == activeId)
                    return name; // exact match for the logged-in account

                if (best == null || body.Contains("\"MostRecent\"\t\t\"1\"") || body.Contains("\"MostRecent\" \"1\""))
                    best = name;
            }

            return string.IsNullOrWhiteSpace(best) ? null : best;
        }
        catch (Exception ex)
        {
            Logger.LogDebug($"[Steam] Could not read persona name: {ex.Message}");
            return null;
        }
    }

    public bool IsSteamRunning()
    {
        try
        {
            var processes = System.Diagnostics.Process.GetProcessesByName("Steam");
            var isRunning = processes.Length > 0;

            foreach (var proc in processes)
            {
                proc.Dispose();
            }

            Logger.LogDebug($"[Steam] Running check: {isRunning}");
            return isRunning;
        }
        catch (Exception ex)
        {
            Logger.LogError("[Steam] Error checking if Steam is running", ex);
            return false;
        }
    }
}
