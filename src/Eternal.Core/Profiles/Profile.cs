using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Eternal.Core.Persistence;
using Eternal.Core.Settings;

namespace Eternal.Core.Profiles;

public sealed partial class ProfileMods : IJsonOnDeserialized
{
    public List<string> Addons { get; set; } = [];
    public List<string> Particles { get; set; } = [];

    /// <summary>Where a mod came from ("gamebanana:123:456"), so whoever imports the profile can download the same file.</summary>
    public Dictionary<string, string>? Sources { get; set; }

    [System.Text.RegularExpressions.GeneratedRegex(@"^gamebanana(?:-sound)?:\d{1,12}:\d{1,12}$")]
    private static partial System.Text.RegularExpressions.Regex SourcePattern();

    public static bool IsShareableSource(string? source) => source is not null && SourcePattern().IsMatch(source);

    void IJsonOnDeserialized.OnDeserialized()
    {
        JsonChecks.NoEmptyEntries(Addons, "addons");
        JsonChecks.NoEmptyEntries(Particles, "particles");
        Sources = Sources?.Where(s => s.Key.Length is > 0 and <= 128 && IsShareableSource(s.Value))
            .ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);
    }
}

/// <summary>
/// A shareable configuration. A complete profile is a snapshot: applying it
/// resets everything it does not list. A preset only changes what it lists.
/// Null sections (binds, launch options, autoexec text, mods) are left alone.
/// Machine-specific data such as the TF2 folder is never part of a profile.
/// </summary>
public sealed class Profile : IJsonOnDeserialized
{
    public const string FormatName = "eternal-profile";
    public const int CurrentVersion = 2;

    public string Format { get; set; } = FormatName;
    public int Version { get; set; } = CurrentVersion;
    public string Id { get; set; } = NewId();
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool Complete { get; set; }
    public DateTimeOffset Modified { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, JsonNode?> Values { get; set; } = new(StringComparer.Ordinal);
    public List<KeyBind>? Binds { get; set; }
    public string? ExtraLaunchArgs { get; set; }
    public string? UserAutoexec { get; set; }
    public ProfileMods? Mods { get; set; }

    [JsonIgnore]
    public bool BuiltIn { get; set; }

    void IJsonOnDeserialized.OnDeserialized() => JsonChecks.NoEmptyEntries(Binds, "binds");

    public static string NewId() => Guid.NewGuid().ToString("N");

    public override string ToString() => Name;
}
