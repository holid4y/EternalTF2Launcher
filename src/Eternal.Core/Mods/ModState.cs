using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Eternal.Core.Engine;

namespace Eternal.Core.Mods;

public sealed class ModEntry
{
    public string Name { get; set; } = "";
    public ModKind Kind { get; set; }
    public bool Enabled { get; set; }

    /// <summary>Where it came from, e.g. "gamebanana:12345", for update checks.</summary>
    public string? Source { get; set; }
}

public sealed class ModBuildOptions
{
    public bool DisablePaintColors { get; set; }
    public bool ShowConsole { get; set; } = true;
    public bool FixModelPaths { get; set; } = true;
    public bool SkipQuickPrecache { get; set; }

    public BuildOptions ToEngine() => new(DisablePaintColors, ShowConsole, FixModelPaths, SkipQuickPrecache);
}

/// <summary>The player's intent: which mods are on and in what priority (first wins).</summary>
public sealed class ModState
{
    public int Version { get; set; } = 1;
    public List<ModEntry> Order { get; set; } = [];
    public ModBuildOptions Options { get; set; } = new();
}

/// <summary>What was last written into the game folder, so a restart knows whether work is pending.</summary>
public sealed class PreloadRecord
{
    public string? AppliedFingerprint { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }
    public bool Installed { get; set; }
    public string? TfPath { get; set; }
    public List<string> Addons { get; set; } = [];
    public List<string> Particles { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

/// <summary>A concrete build request: enabled mods in priority order plus options.</summary>
public sealed record DesiredPreload(IReadOnlyList<string> Addons, IReadOnlyList<string> Particles, ModBuildOptions Options, string LibrarySignature)
{
    public bool IsEmpty => Addons.Count == 0 && Particles.Count == 0;

    public string Fingerprint(string tfPath)
    {
        var text = new StringBuilder()
            .AppendLine(Path.GetFullPath(tfPath))
            .AppendJoin('\n', Addons).AppendLine().AppendLine("--")
            .AppendJoin('\n', Particles).AppendLine().AppendLine("--")
            .Append(Options.DisablePaintColors).Append(Options.ShowConsole).Append(Options.FixModelPaths).Append(Options.SkipQuickPrecache)
            .AppendLine().Append(LibrarySignature);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}

[JsonSourceGenerationOptions(RespectNullableAnnotations = true, WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ModState))]
[JsonSerializable(typeof(PreloadRecord))]
internal sealed partial class ModJson : JsonSerializerContext;
