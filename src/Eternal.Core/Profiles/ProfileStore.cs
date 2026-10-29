using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Eternal.Core.Logging;
using Eternal.Core.Persistence;
using Eternal.Core.Platform;
using Eternal.Core.Settings;

namespace Eternal.Core.Profiles;

public sealed record ProfileImport(Profile Profile, int UnknownSettings, bool HasAutoexecText);

/// <summary>
/// User profiles live in config/profiles/{id}.json. Ids are generated here and
/// validated on every path operation, so a crafted profile can never name a
/// file outside that folder.
/// </summary>
public sealed partial class ProfileStore
{
    private const long MaxImportBytes = 1024 * 1024;
    private const int MaxAutoexecChars = 64 * 1024;

    private readonly string _directory;
    private readonly List<Profile> _user = [];

    [GeneratedRegex("^[a-z0-9]{8,32}$")]
    private static partial Regex IdPattern();

    public ProfileStore(string directory)
    {
        _directory = directory;
        BuiltIns = LoadBuiltIns();
    }

    public IReadOnlyList<Profile> BuiltIns { get; }
    public IReadOnlyList<Profile> UserProfiles => _user;
    public IEnumerable<Profile> All => BuiltIns.Concat(_user);

    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    internal string PathFor(string id)
    {
        if (!IsValidId(id))
            throw new ArgumentException($"Invalid profile id: {id}", nameof(id));
        var path = Path.Combine(_directory, id + ".json");
        if (!AppPaths.IsInside(_directory, path))
            throw new ArgumentException($"Profile path escapes the profiles folder: {id}", nameof(id));
        return path;
    }

    public void Load()
    {
        _user.Clear();
        if (!Directory.Exists(_directory))
            return;

        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            if (!IsValidId(stem))
            {
                Log.Warn($"Ignoring profile with an unexpected file name: {Path.GetFileName(file)}");
                continue;
            }
            try
            {
                var profile = Parse(File.ReadAllText(file));
                profile.Id = stem;
                profile.BuiltIn = false;
                _user.Add(profile);
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
            {
                Log.Warn($"Skipping unreadable profile {Path.GetFileName(file)}: {ex.Message}");
            }
        }
        _user.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
    }

    public Profile Save(Profile profile)
    {
        if (profile.BuiltIn)
            throw new InvalidOperationException("Built-in profiles are read-only.");
        profile.Name = CleanName(profile.Name);
        profile.Modified = DateTimeOffset.UtcNow;
        JsonFile.Save(PathFor(profile.Id), profile, ProfileJson.Default.Profile);
        _user.RemoveAll(p => p.Id == profile.Id);
        _user.Add(profile);
        _user.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return profile;
    }

    public void Delete(string id)
    {
        var path = PathFor(id);
        _user.RemoveAll(p => p.Id == id);
        if (File.Exists(path))
            File.Delete(path);
    }

    public void Export(Profile profile, string destination)
    {
        var copy = Parse(JsonSerializer.Serialize(profile, ProfileJson.Default.Profile));
        AtomicFile.WriteAllBytes(destination, JsonSerializer.SerializeToUtf8Bytes(copy, ProfileJson.Default.Profile), keepBackup: false);
    }

    /// <summary>Imports a shared profile under a fresh id. The source file's id is never trusted.</summary>
    public ProfileImport Import(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxImportBytes)
            throw new InvalidDataException("That file is too large to be a profile.");

        var profile = Parse(File.ReadAllText(path));
        var unknown = profile.Values.Keys.Count(k => SettingsCatalog.Find(k) is null);
        profile.Values = Sanitize(profile.Values);
        profile.Id = Profile.NewId();
        profile.BuiltIn = false;
        if (string.IsNullOrWhiteSpace(profile.Name))
            profile.Name = Path.GetFileNameWithoutExtension(path);
        Save(profile);
        return new ProfileImport(profile, unknown, !string.IsNullOrWhiteSpace(profile.UserAutoexec));
    }

    public static Profile Capture(GameSettings settings, string name, string? description, string? userAutoexec, ProfileMods? mods) => new()
    {
        Name = CleanName(name),
        Description = description,
        Complete = true,
        Values = settings.CaptureValues(),
        Binds = settings.Binds.Select(b => b.Clone()).ToList(),
        ExtraLaunchArgs = settings.ExtraLaunchArgs,
        UserAutoexec = string.IsNullOrWhiteSpace(userAutoexec) ? null : userAutoexec,
        Mods = mods,
    };

    public static void Apply(Profile profile, GameSettings settings)
    {
        settings.Apply(profile.Values, profile.Complete);
        if (profile.Binds is not null)
            settings.SetBinds(profile.Binds);
        if (profile.ExtraLaunchArgs is not null)
            settings.ExtraLaunchArgs = profile.ExtraLaunchArgs;
    }

    public static bool Matches(Profile profile, GameSettings settings)
    {
        foreach (var (id, value) in profile.Values)
        {
            if (SettingsCatalog.Find(id) is not { } setting)
                continue;
            if (!setting.ValueEquals(settings.Get(setting), value))
                return false;
        }
        return profile.Values.Count > 0;
    }

    public Profile? FindMatch(GameSettings settings) =>
        _user.FirstOrDefault(p => Matches(p, settings)) ?? BuiltIns.FirstOrDefault(p => Matches(p, settings));

    private static string CleanName(string? name)
    {
        var cleaned = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length > 60)
            cleaned = cleaned[..60];
        return cleaned.Length == 0 ? "Untitled profile" : cleaned;
    }

    private static Dictionary<string, JsonNode?> Sanitize(Dictionary<string, JsonNode?> values) =>
        values.Where(kv => SettingsCatalog.Find(kv.Key) is not null)
              .ToDictionary(kv => kv.Key, kv => (JsonNode?)SettingsCatalog.ById[kv.Key].Coerce(kv.Value), StringComparer.Ordinal);

    /// <summary>Reads a v2 profile or converts a 1.x one.</summary>
    internal static Profile Parse(string json)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                   ?? throw new InvalidDataException("Not a profile file.");

        Profile profile;
        try
        {
            profile = root["format"]?.GetValue<string>() == Profile.FormatName
                ? root.Deserialize(ProfileJson.Default.Profile) ?? throw new InvalidDataException("Not a profile file.")
                : FromLegacy(root);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            // A field of the wrong type, e.g. a number where a name belongs.
            throw new InvalidDataException("Not a profile file.", ex);
        }

        if (profile.UserAutoexec is { Length: > MaxAutoexecChars })
            profile.UserAutoexec = profile.UserAutoexec[..MaxAutoexecChars];
        profile.Binds = profile.Binds?.Where(ManagedBlock.IsValidBind).ToList();
        return profile;
    }

    private static Profile FromLegacy(JsonObject root)
    {
        if (root["settings"] is not JsonObject settings)
            throw new InvalidDataException("Not a profile file.");

        var profile = new Profile
        {
            Name = root["name"]?.GetValue<string>() ?? "",
            Description = root["description"]?.GetValue<string>(),
            Complete = root["isUserCreated"]?.GetValue<bool>() ?? false,
            UserAutoexec = root["userContent"]?.GetValue<string>(),
        };

        foreach (var (key, value) in settings)
            profile.Values[key] = value?.DeepClone();

        var (launchValues, extra) = LaunchOptions.FromLegacy(root["launchOptions"]?.GetValue<string>());
        foreach (var (key, value) in launchValues)
            profile.Values[key] = value;
        if (profile.Complete)
            profile.ExtraLaunchArgs = extra;

        return profile;
    }

    private static List<Profile> LoadBuiltIns()
    {
        var assembly = typeof(ProfileStore).Assembly;
        var result = new List<Profile>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.Contains(".Profiles.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)).Order())
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var profile = Parse(reader.ReadToEnd());
            profile.BuiltIn = true;
            result.Add(profile);
        }
        return result;
    }
}
