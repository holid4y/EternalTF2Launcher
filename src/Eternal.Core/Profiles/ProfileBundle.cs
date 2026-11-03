using System.IO.Compression;
using System.Text;
using Eternal.Core.Engine;
using Eternal.Core.Mods;

namespace Eternal.Core.Profiles;

public sealed record BundledMod(string Name, ModKind Kind, string Folder);

public sealed record BundleContents(ProfileImport Import, IReadOnlyList<BundledMod> Mods, string Staging);

/// <summary>
/// A setup in one file: the profile plus the files of mods that can't be
/// downloaded again from GameBanana. It is a zip ("profile.json", then
/// "mods/addons/&lt;name&gt;/..." and "mods/particles/&lt;name&gt;/..."), read
/// with hard limits since anyone can hand one over: no paths outside the mod
/// folders, a cap on entries and size, and no runaway compression.
/// </summary>
public static class ProfileBundle
{
    public const string Extension = "eternal";
    private const string ProfileEntry = "profile.json";
    private const int MaxEntries = 50_000;
    private const long MaxTotalBytes = 6L * 1024 * 1024 * 1024;
    private const long MaxProfileBytes = 1024 * 1024;
    private const int MaxRatio = 200;

    public static void Write(string destination, Profile profile, IReadOnlyList<BundledMod> mods, IProgress<double>? progress = null, CancellationToken cancellation = default)
    {
        var temp = destination + ".part";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry(ProfileEntry, CompressionLevel.Optimal).Open(), new UTF8Encoding(false)))
                    writer.Write(ProfileStore.ToJson(profile));

                var files = mods.SelectMany(m => Directory.EnumerateFiles(m.Folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                    .Select(f => (Mod: m, File: f))).ToList();
                for (var i = 0; i < files.Count; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var (mod, file) = files[i];
                    var relative = Path.GetRelativePath(mod.Folder, file).Replace('\\', '/');
                    zip.CreateEntryFromFile(file, $"mods/{KindFolder(mod.Kind)}/{mod.Name}/{relative}", CompressionLevel.Fastest);
                    progress?.Report((i + 1.0) / files.Count);
                }
            }
            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>Unpacks into a folder of its own under <paramref name="stagingRoot"/>; the caller deletes it when done.</summary>
    public static BundleContents Open(string path, string stagingRoot, CancellationToken cancellation = default)
    {
        var staging = Path.Combine(stagingRoot, "bundle-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var zip = ZipFile.OpenRead(path);
            if (zip.Entries.Count > MaxEntries)
                throw new InvalidDataException("That bundle has more files than any setup.");
            if (zip.GetEntry(ProfileEntry) is not { } profileEntry || profileEntry.Length > MaxProfileBytes)
                throw new InvalidDataException("That file isn't an Eternal setup bundle.");

            string json;
            using (var reader = new StreamReader(profileEntry.Open(), Encoding.UTF8))
                json = reader.ReadToEnd();
            ProfileImport import;
            try
            {
                import = ProfileStore.FromText(json, Path.GetFileNameWithoutExtension(path));
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new InvalidDataException("That bundle's setup can't be read.", ex);
            }

            var mods = new Dictionary<(string, ModKind), BundledMod>();
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                cancellation.ThrowIfCancellationRequested();
                if (entry.FullName == ProfileEntry || entry.FullName.EndsWith('/'))
                    continue;
                var (kind, name, relative) = Split(entry.FullName);
                if (entry.Length > MaxRatio * Math.Max(entry.CompressedLength, 1024))
                    throw new InvalidDataException($"{entry.FullName} is packed suspiciously tightly, so the bundle was refused.");
                total += entry.Length;
                if (total > MaxTotalBytes)
                    throw new InvalidDataException("That bundle unpacks to more than any setup needs.");

                var folder = Path.Combine(staging, KindFolder(kind), name);
                var target = Path.GetFullPath(Path.Combine(folder, relative));
                if (!Platform.AppPaths.IsInside(folder, target))
                    throw new InvalidDataException($"{entry.FullName} points outside its mod folder.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Extract(entry, target);
                mods.TryAdd((name, kind), new BundledMod(name, kind, folder));
            }
            return new BundleContents(import, mods.Values.ToList(), staging);
        }
        catch
        {
            ArchiveIngest.TryDelete(staging);
            throw;
        }
    }

    private static (ModKind Kind, string Name, string Relative) Split(string entry)
    {
        var parts = entry.Split('/');
        if (parts.Length < 4 || parts[0] != "mods" || entry.Contains('\\'))
            throw new InvalidDataException($"{entry} isn't where a bundle keeps mod files.");
        var kind = parts[1] switch
        {
            "addons" => ModKind.Addon,
            "particles" => ModKind.Particle,
            _ => throw new InvalidDataException($"{entry} isn't where a bundle keeps mod files."),
        };
        var name = parts[2];
        if (name.Length == 0 || ArchiveIngest.SafeFolderName(name) != name || name is "." or "..")
            throw new InvalidDataException($"\"{name}\" can't be a mod name.");
        var relative = parts[3..];
        if (relative.Any(p => p.Length == 0 || p is "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException($"{entry} has a path the launcher won't write.");
        return (kind, name, Path.Combine(relative));
    }

    // Copies no more than the size the zip declares, so a lying entry can't grow past its budget.
    private static void Extract(ZipArchiveEntry entry, string target)
    {
        using var input = entry.Open();
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
        var buffer = new byte[81920];
        long written = 0;
        int n;
        while ((n = input.Read(buffer)) > 0)
        {
            written += n;
            if (written > entry.Length)
                throw new InvalidDataException($"{entry.FullName} is bigger than the bundle says.");
            output.Write(buffer, 0, n);
        }
    }

    private static string KindFolder(ModKind kind) => kind == ModKind.Particle ? "particles" : "addons";
}
