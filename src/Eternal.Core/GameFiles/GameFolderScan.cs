using System.Text.RegularExpressions;
using Eternal.Core.Platform;
using Eternal.Core.Settings;

namespace Eternal.Core.GameFiles;

public enum FoundKind { Mod, Hud, Sounds, Config, Mastercomfig, OldPreloader, Other }

/// <summary>Something the player already has in the game folder. <see cref="Files"/> holds everything that belongs to it.</summary>
public sealed record FoundItem(string Name, FoundKind Kind, long Size, string Area, IReadOnlyList<string> Files, bool IsFolder, string ImportPath)
{
    public bool CanImport => Kind is FoundKind.Mod or FoundKind.Hud or FoundKind.Sounds;
}

/// <summary>Reads what is in tf/custom and tf/cfg before the launcher writes anything there.</summary>
public static partial class GameFolderScan
{
    private static readonly HashSet<string> IgnoredCustom = new(StringComparer.OrdinalIgnoreCase) { "workshop", "readme.txt", ".DS_Store", "desktop.ini" };
    // What TF2, Classified and Gold Rush write or ship in cfg themselves, read from clean installs of each.
    private static readonly HashSet<string> GameConfigs = new(StringComparer.OrdinalIgnoreCase)
    {
        "config.cfg", "config_default.cfg", "botmaxconfig.cfg", "sixense_bindings.cfg", "undo.cfg", "listenserver.cfg", "server.cfg", "valve.rc",
        "360controller.cfg", "360controller-linux.cfg", "undo360controller.cfg", "mtp.cfg", "replay_example.cfg", "sourcevr_tf.cfg",
        "default.cfg", "randomizer_default.cfg", "vplugins_default.cfg", "birbexec.cfg",
    };
    private static readonly HashSet<string> IgnoredCfgFolders = new(StringComparer.OrdinalIgnoreCase) { "w", "addons" };
    private static readonly EnumerationOptions TopLevel = new() { IgnoreInaccessible = true, AttributesToSkip = 0 };
    private static readonly EnumerationOptions Everything = new() { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = true };
    private static readonly string[] OldPreloaderPrefixes = ["_casual_preloader", "_quickprecache", "quickprecache", "zz_run_preloader_again"];

    [GeneratedRegex(@"^(?<base>.+)_(?<part>dir|\d{3})$", RegexOptions.IgnoreCase)]
    private static partial Regex SplitVpk();

    /// <summary>Training, matchmaking server presets (TF2 adds some with updates) and the chapter stubs.</summary>
    [GeneratedRegex(@"^(tr_|server_|chapter\d+\.cfg$)", RegexOptions.IgnoreCase)]
    private static partial Regex ShippedConfig();

    public static IReadOnlyList<FoundItem> Run(string tfPath)
    {
        var found = new List<FoundItem>();
        ScanCustom(Path.Combine(tfPath, "custom"), found);
        ScanCfg(Path.Combine(tfPath, "cfg"), found);
        return found;
    }

    private static void ScanCustom(string custom, List<FoundItem> found)
    {
        if (!Directory.Exists(custom))
            return;
        var preloader = new List<string>();
        var vpks = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in Directory.EnumerateFileSystemEntries(custom, "*", TopLevel))
        {
            var name = Path.GetFileName(entry);
            if (IgnoredCustom.Contains(name) || name.EndsWith(".cache", StringComparison.OrdinalIgnoreCase)
                || name.Equals(TransparentViewmodels.FolderName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (OldPreloaderPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                preloader.Add(entry);
                continue;
            }
            if (Directory.Exists(entry))
            {
                if (HasFiles(entry))
                    found.Add(new FoundItem(name, FolderKind(entry, name), FolderSize(entry), "custom", [entry], true, entry));
                continue;
            }
            if (name.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
                vpks.TryAdd(entry, []);
            else
                found.Add(new FoundItem(name, FoundKind.Other, new FileInfo(entry).Length, "custom", [entry], false, entry));
        }

        foreach (var group in GroupSplitVpks(vpks.Keys))
        {
            var kind = group.Name.StartsWith("mastercomfig", StringComparison.OrdinalIgnoreCase) ? FoundKind.Mastercomfig : VpkKind(group.Main);
            found.Add(new FoundItem(group.Name, kind, group.Files.Sum(f => new FileInfo(f).Length), "custom", group.Files, false, group.Main));
        }
        if (preloader.Count > 0)
            found.Add(new FoundItem("An earlier casual-pre-loader install", FoundKind.OldPreloader, preloader.Sum(EntrySize), "custom", preloader, false, ""));
    }

    private static IEnumerable<(string Name, string Main, IReadOnlyList<string> Files)> GroupSplitVpks(IEnumerable<string> vpks)
    {
        var all = vpks.ToList();
        var splitBases = all.Select(v => Path.GetFileNameWithoutExtension(v))
            .Where(s => s.EndsWith("_dir", StringComparison.OrdinalIgnoreCase))
            .Select(s => s[..^4]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var split in splitBases)
        {
            var pieces = all.Where(v => SplitVpk().Match(Path.GetFileNameWithoutExtension(v)) is { Success: true } m
                && m.Groups["base"].Value.Equals(split, StringComparison.OrdinalIgnoreCase)).ToList();
            taken.UnionWith(pieces);
            yield return (split, pieces.First(p => Path.GetFileNameWithoutExtension(p).EndsWith("_dir", StringComparison.OrdinalIgnoreCase)), pieces);
        }
        foreach (var single in all.Where(v => !taken.Contains(v)))
            yield return (Path.GetFileNameWithoutExtension(single), single, [single]);
    }

    private static void ScanCfg(string cfg, List<FoundItem> found)
    {
        if (!Directory.Exists(cfg))
            return;
        foreach (var file in Directory.EnumerateFiles(cfg, "*.cfg", TopLevel))
        {
            var name = Path.GetFileName(file);
            if (GameConfigs.Contains(name) || ShippedConfig().IsMatch(name) || !HasSettings(file))
                continue;
            found.Add(new FoundItem(name, FoundKind.Config, new FileInfo(file).Length, "cfg", [file], false, ""));
        }
        foreach (var folder in Directory.EnumerateDirectories(cfg, "*", TopLevel))
        {
            var name = Path.GetFileName(folder);
            if (!IgnoredCfgFolders.Contains(name) && Directory.EnumerateFiles(folder, "*.cfg", Everything).Any())
                found.Add(new FoundItem(name, FoundKind.Config, FolderSize(folder), "cfg", [folder], true, ""));
        }
    }

    /// <summary>A file the launcher can't read counts as written by the player, so it still gets asked about.</summary>
    private static bool HasSettings(string file)
    {
        try
        {
            return File.ReadLines(file).Any(line => line.Trim() is { Length: > 0 } text && !text.StartsWith("//", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool HasFiles(string folder) => Directory.EnumerateFiles(folder, "*", Everything).Any();

    private static long FolderSize(string folder) => Directory.EnumerateFiles(folder, "*", Everything).Sum(f => new FileInfo(f).Length);

    private static long EntrySize(string entry) => Directory.Exists(entry) ? FolderSize(entry) : new FileInfo(entry).Length;

    private static FoundKind FolderKind(string folder, string name)
    {
        if (name.StartsWith("mastercomfig", StringComparison.OrdinalIgnoreCase))
            return FoundKind.Mastercomfig;
        bool Has(params string[] parts) => File.Exists(Path.Combine([folder, .. parts]));
        if (Has("resource", "clientscheme.res") || Has("scripts", "hudlayout.res"))
            return FoundKind.Hud;
        return Has("sound", "ui", "hitsound.wav") || Has("sound", "ui", "killsound.wav") ? FoundKind.Sounds : FoundKind.Mod;
    }

    private static FoundKind VpkKind(string vpk)
    {
        if (Contains(vpk, "resource/clientscheme.res") || Contains(vpk, "scripts/hudlayout.res"))
            return FoundKind.Hud;
        return Contains(vpk, "sound/ui/hitsound.wav") || Contains(vpk, "sound/ui/killsound.wav") ? FoundKind.Sounds : FoundKind.Mod;
    }

    private static bool Contains(string vpk, string path)
    {
        try
        {
            return Vpk.Read(vpk, path) is not null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>Moves an item out of the game into <paramref name="asideRoot"/>. Only paths inside the game folder are touched, and an earlier copy is never overwritten.</summary>
    public static void SetAside(FoundItem item, string tfPath, string asideRoot)
    {
        foreach (var source in item.Files)
        {
            if (!AppPaths.IsInside(tfPath, source))
                throw new InvalidOperationException($"{source} isn't inside the game folder.");
            var area = Path.Combine(asideRoot, item.Area);
            Directory.CreateDirectory(area);
            Move(source, FreeName(area, Path.GetFileName(source)));
        }
    }

    private static string FreeName(string folder, string name)
    {
        var target = Path.Combine(folder, name);
        for (var n = 2; File.Exists(target) || Directory.Exists(target); n++)
            target = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}");
        return target;
    }

    private static void Move(string source, string target)
    {
        if (!Directory.Exists(source))
        {
            File.Move(source, target);
            return;
        }
        try
        {
            Directory.Move(source, target);
        }
        catch (IOException)
        {
            // Directory.Move can't cross drives, and the game often lives on another one.
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            Directory.Delete(source, recursive: true);
        }
    }
}
