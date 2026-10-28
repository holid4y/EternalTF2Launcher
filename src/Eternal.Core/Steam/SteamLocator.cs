using System.Globalization;
using Eternal.Core.Logging;
using Microsoft.Win32;

namespace Eternal.Core.Steam;

public sealed record SteamAccount(string SteamId64, string? PersonaName);

public static class SteamLocator
{
    public const int Tf2AppId = 440;
    private const long SteamId64Base = 76561197960265728;

    public static IEnumerable<string> CandidateRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var path in WindowsRegistryRoots())
                yield return path;
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
            yield break;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, ".local", "share", "Steam");
        yield return Path.Combine(home, ".steam", "steam");
        yield return Path.Combine(home, ".steam", "root");
        yield return Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
        yield return Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam");
    }

    public static IReadOnlyList<string> FindRoots()
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var roots = new List<string>();
        foreach (var candidate in CandidateRoots())
        {
            try
            {
                if (!Directory.Exists(Path.Combine(candidate, "steamapps")))
                    continue;
                var resolved = ResolveLinks(candidate);
                if (seen.Add(resolved))
                    roots.Add(resolved);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Debug($"Skipping Steam candidate {candidate}: {ex.Message}");
            }
        }
        return roots;
    }

    public static string? FindRoot() => FindRoots().FirstOrDefault();

    public static IReadOnlyList<string> Libraries(string root)
    {
        var libraries = new List<string> { root };
        var file = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(file))
            return libraries;
        try
        {
            var folders = Vdf.Parse(File.ReadAllText(file)).Section("libraryfolders");
            foreach (var (_, value) in folders?.Children ?? [])
            {
                var path = (value as VdfNode)?.Value("path") ?? value as string;
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)
                    && !libraries.Contains(path, StringComparer.OrdinalIgnoreCase))
                    libraries.Add(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Couldn't read {file}: {ex.Message}");
        }
        return libraries;
    }

    /// <summary>The tf/ folder of the TF2 install, or null when it can't be found.</summary>
    public static string? FindTf2()
    {
        foreach (var root in FindRoots())
        {
            foreach (var library in Libraries(root))
            {
                var steamapps = Path.Combine(library, "steamapps");
                var installDir = "Team Fortress 2";
                var manifest = Path.Combine(steamapps, $"appmanifest_{Tf2AppId}.acf");
                if (File.Exists(manifest))
                {
                    try
                    {
                        installDir = Vdf.Parse(File.ReadAllText(manifest)).Section("AppState")?.Value("installdir") ?? installDir;
                    }
                    catch (IOException) { }
                }
                var tf = Path.Combine(steamapps, "common", installDir, "tf");
                if (File.Exists(Path.Combine(tf, "gameinfo.txt")))
                    return tf;
            }
        }
        return null;
    }

    /// <summary>
    /// TF2 screenshots taken with Steam's overlay (F12), newest first. The
    /// signed-in account's folder comes first; other accounts on this PC follow.
    /// </summary>
    public static IReadOnlyList<string> Screenshots(string? root = null, string? steamId64 = null)
    {
        root ??= FindRoot();
        var userdata = root is null ? null : Path.Combine(root, "userdata");
        if (userdata is null || !Directory.Exists(userdata))
            return [];

        string? preferred = null;
        if (long.TryParse(steamId64 ?? ActiveAccount(root)?.SteamId64, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id64) && id64 > SteamId64Base)
            preferred = (id64 - SteamId64Base).ToString(CultureInfo.InvariantCulture);

        try
        {
            return Directory.EnumerateDirectories(userdata)
                .OrderByDescending(dir => Path.GetFileName(dir) == preferred)
                .Select(dir => Path.Combine(dir, "760", "remote", Tf2AppId.ToString(CultureInfo.InvariantCulture), "screenshots"))
                .Where(Directory.Exists)
                .SelectMany(dir => Directory.EnumerateFiles(dir, "*.jpg").Concat(Directory.EnumerateFiles(dir, "*.png"))
                    .OrderByDescending(File.GetLastWriteTimeUtc))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug($"Couldn't list Steam screenshots: {ex.Message}");
            return [];
        }
    }

    public static bool IsTf2Folder(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(Path.Combine(path, "gameinfo.txt"))
        && File.Exists(Path.Combine(path, "steam.inf"));

    /// <summary>
    /// The account Steam is signed in with right now, falling back to the
    /// most recently used one. Newer clients no longer write MostRecent, so
    /// AutoLogin and Timestamp decide.
    /// </summary>
    public static SteamAccount? ActiveAccount(string? root = null)
    {
        root ??= FindRoot();
        var users = LoginUsers(root);
        var active = ActiveAccountId(root);
        if (active is > 0)
        {
            var id = (SteamId64Base + active.Value).ToString(CultureInfo.InvariantCulture);
            return users.FirstOrDefault(u => u.Account.SteamId64 == id).Account ?? new SteamAccount(id, null);
        }

        return users
            .OrderByDescending(u => u.MostRecent)
            .ThenByDescending(u => u.Timestamp)
            .Select(u => u.Account)
            .FirstOrDefault();
    }

    internal static List<(SteamAccount Account, bool MostRecent, long Timestamp)> LoginUsers(string? root)
    {
        var result = new List<(SteamAccount, bool, long)>();
        if (root is null)
            return result;
        var file = Path.Combine(root, "config", "loginusers.vdf");
        if (!File.Exists(file))
            return result;
        try
        {
            return ParseLoginUsers(File.ReadAllText(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Couldn't read loginusers.vdf: {ex.Message}");
            return result;
        }
    }

    internal static List<(SteamAccount Account, bool MostRecent, long Timestamp)> ParseLoginUsers(string text)
    {
        var result = new List<(SteamAccount, bool, long)>();
        foreach (var (key, value) in Vdf.Parse(text).Section("users")?.Children ?? [])
        {
            if (value is not VdfNode user || key.Length != 17 || !key.All(char.IsAsciiDigit))
                continue;
            var recent = user.Value("MostRecent") == "1" || user.Value("mostrecent") == "1" || user.Value("AutoLogin") == "1";
            long.TryParse(user.Value("Timestamp"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var timestamp);
            result.Add((new SteamAccount(key, user.Value("PersonaName")), recent, timestamp));
        }
        return result;
    }

    private static long? ActiveAccountId(string? root)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
                return key?.GetValue("ActiveUser") is int id ? (uint)id : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return null;
            }
        }

        var registry = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".steam", "registry.vdf");
        if (!File.Exists(registry))
            return null;
        try
        {
            var value = Vdf.Parse(File.ReadAllText(registry))
                .Path("Registry", "HKCU", "Software", "Valve", "Steam", "ActiveProcess")?.Value("ActiveUser");
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> WindowsRegistryRoots()
    {
        if (!OperatingSystem.IsWindows())
            yield break;
        string? user = null, machine = null;
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                user = key?.GetValue("SteamPath") as string;
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
                machine = key?.GetValue("InstallPath") as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Debug($"Steam registry lookup failed: {ex.Message}");
        }
        if (!string.IsNullOrWhiteSpace(user))
            yield return Path.GetFullPath(user);
        if (!string.IsNullOrWhiteSpace(machine))
            yield return Path.GetFullPath(machine);
    }

    private static string ResolveLinks(string path)
    {
        var info = new DirectoryInfo(path);
        var target = info.ResolveLinkTarget(returnFinalTarget: true);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(target?.FullName ?? info.FullName));
    }
}
