using System.Threading;
using System.Threading.Tasks;

namespace LauncherTF2.Services;

/// <summary>
/// Progress event emitted by the Casual Preloader engine while it works
/// (mirrors the NDJSON "progress" event in docs/preloader-integration-contract.md).
/// </summary>
public sealed class PreloaderProgress
{
    public string Stage   { get; init; } = "Working";
    public int    Percent { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Final outcome of a preloader CLI command (the NDJSON "result" event plus
/// process-level information).
/// </summary>
public sealed class PreloaderResult
{
    public bool     Success       { get; init; }
    public string   Command       { get; init; } = string.Empty;
    public string[] Warnings      { get; init; } = [];
    public string[] Errors        { get; init; } = [];
    public long     DurationMs    { get; init; }
    public string?  EngineVersion { get; init; }
    public int      ExitCode      { get; init; }
    /// <summary>Raw "data" payload JSON for callers that need command-specific fields.</summary>
    public string?  DataJson      { get; init; }

    public string ErrorSummary => Errors.Length > 0 ? string.Join("\n", Errors) : "Unknown preloader error.";
}

/// <summary>One model-compatibility problem found by the `compat` scan (read-only).</summary>
public sealed class PreloaderCompatIssue
{
    /// <summary>Library folder name of the affected mod.</summary>
    public string Addon    { get; init; } = string.Empty;
    /// <summary>"error" (will load as vanilla) or "warning" (may misbehave).</summary>
    public string Severity { get; init; } = "warning";
    public string Message  { get; init; } = string.Empty;
}

/// <summary>One mod known to the preloader engine (regular addon or particle mod).</summary>
public sealed class PreloaderAddonInfo
{
    /// <summary>Folder name inside the engine library — the identifier CLI commands take.</summary>
    public string Name        { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Type        { get; init; } = "Unknown";
    /// <summary>Particle mods are always applied when installed (no enable toggle).</summary>
    public bool   IsParticleMod { get; init; }
}

/// <summary>Typed view of the CLI `report` command payload.</summary>
public sealed class PreloaderReport
{
    public string? EngineVersion { get; init; }
    public string? TfPath        { get; init; }
    public string[] EnabledAddons { get; init; } = [];
    public List<PreloaderAddonInfo> Addons { get; init; } = [];
}

/// <summary>
/// Boundary between Eternal Launcher and the Casual Preloader engine.
/// All communication goes through the preloader CLI with JSON contracts —
/// the launcher never depends on Python internals.
/// </summary>
public interface IPreloaderService
{
    /// <summary>True when a preloader engine (CLI exe or Python + sources) was located.</summary>
    bool IsAvailable { get; }

    /// <summary>Path to the engine's last_install_manifest.json, if available.</summary>
    string? ManifestPath { get; }

    /// <summary>
    /// True while an engine command (notably an install/"bake") is executing.
    /// Used to gate launching TF2 so the game never starts while tf/custom is
    /// mid-rebuild.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>Human-readable description of how/where the engine was found (for logs/UI).</summary>
    string EngineDescription { get; }

    /// <summary>Runs `version` to confirm the engine actually executes end-to-end.</summary>
    Task<PreloaderResult> CheckAsync(CancellationToken ct = default);

    /// <summary>Validates the target tf/ directory (`validate`).</summary>
    Task<PreloaderResult> ValidateAsync(string tfPath, CancellationToken ct = default);

    /// <summary>Builds and installs the mod preload into the tf/ directory (`install`).</summary>
    Task<PreloaderResult> InstallAsync(string tfPath, IProgress<PreloaderProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Removes all preloader-generated content from the tf/ directory (`uninstall`).</summary>
    Task<PreloaderResult> UninstallAsync(string tfPath, IProgress<PreloaderProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Imports VPK/ZIP files or folders into the engine's mod library (`import`).</summary>
    Task<PreloaderResult> ImportAsync(string[] paths, IProgress<PreloaderProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Adds an addon to the engine's load order (`enable`).</summary>
    Task<PreloaderResult> EnableAsync(string addonName, CancellationToken ct = default);

    /// <summary>Removes an addon from the engine's load order (`disable`).</summary>
    Task<PreloaderResult> DisableAsync(string addonName, CancellationToken ct = default);

    /// <summary>Deletes a mod from the engine's library (`remove`).</summary>
    Task<PreloaderResult> RemoveAsync(string addonName, CancellationToken ct = default);

    /// <summary>Runs `report` and parses the payload. Returns null when the command fails.</summary>
    Task<PreloaderReport?> GetReportAsync(CancellationToken ct = default);

    /// <summary>
    /// Runs `compat` — scans the given addons (or the active load order when
    /// null) for model-compatibility problems (e.g. IK-chain mismatch) that
    /// would make a custom model load as vanilla in Casual. Read-only; never
    /// mutates. Returns one issue per problem; empty when all clear.
    /// </summary>
    Task<IReadOnlyList<PreloaderCompatIssue>> GetCompatibilityIssuesAsync(string[]? addons = null, CancellationToken ct = default);
}
