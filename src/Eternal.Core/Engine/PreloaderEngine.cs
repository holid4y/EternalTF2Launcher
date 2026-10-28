using System.Text.Json;

namespace Eternal.Core.Engine;

public enum ModKind { Addon, Particle }

public sealed record LibraryMod(string Name, string DisplayName, ModKind Kind, string Type, string Description,
    int FileCount, double Modified, IReadOnlyList<string> Particles);

public sealed record ModOverlap(string Winner, string Loser, IReadOnlyList<string> Files);

public sealed record CompatIssue(string Addon, string Model, string Severity, string Message);

public sealed record ImportedMod(ModKind Kind, string Name, bool IsNew);

public sealed record ImportItemResult(string Source, bool Ok, string Message, IReadOnlyList<ImportedMod> Mods);

public sealed record BuildOptions(bool DisablePaintColors, bool ShowConsole, bool FixModelPaths, bool SkipQuickPrecache);

public sealed class PreloaderEngine
{
    private readonly EngineClient _client;

    public PreloaderEngine(EngineClient client) => _client = client;

    public EngineClient Client => _client;

    public async Task<IReadOnlyList<LibraryMod>?> LibraryAsync(CancellationToken cancellation = default)
    {
        var result = await _client.RunAsync(["library"], EngineCommandKind.Query, cancellation: cancellation).ConfigureAwait(false);
        if (!result.Success || result.Data.ValueKind != JsonValueKind.Object)
            return null;

        var mods = new List<LibraryMod>();
        foreach (var addon in Array(result.Data, "addons"))
        {
            mods.Add(new LibraryMod(Str(addon, "name"), Str(addon, "display_name", Str(addon, "name")), ModKind.Addon,
                Str(addon, "type", "Unknown"), Str(addon, "description"), Int(addon, "file_count"), Num(addon, "modified"), []));
        }
        foreach (var particle in Array(result.Data, "particles"))
        {
            var files = Array(particle, "particles").Select(p => p.GetString() ?? "").ToArray();
            mods.Add(new LibraryMod(Str(particle, "name"), Str(particle, "name"), ModKind.Particle, "Particles", "",
                files.Length, Num(particle, "modified"), files));
        }
        return mods;
    }

    public async Task<(IReadOnlyList<ModOverlap> Addons, IReadOnlyList<ModOverlap> Particles)> ConflictsAsync(
        IReadOnlyList<string> addons, IReadOnlyList<string> particles, CancellationToken cancellation = default)
    {
        var args = new List<string> { "conflicts" };
        args.AddRange(addons.Select(a => $"--addon={a}"));
        args.AddRange(particles.Select(p => $"--particle={p}"));
        var result = await _client.RunAsync(args, EngineCommandKind.Query, cancellation: cancellation).ConfigureAwait(false);
        if (!result.Success)
            return ([], []);
        return (Overlaps(result.Data, "addons"), Overlaps(result.Data, "particles"));
    }

    public async Task<IReadOnlyList<CompatIssue>> CompatAsync(IReadOnlyList<string> addons, CancellationToken cancellation = default)
    {
        if (addons.Count == 0)
            return [];
        var result = await _client.RunAsync(["compat", .. addons.Select(a => $"--addon={a}")], EngineCommandKind.Query,
            cancellation: cancellation).ConfigureAwait(false);
        return result.Success
            ? Array(result.Data, "issues").Select(i => new CompatIssue(Str(i, "addon"), Str(i, "model"), Str(i, "severity", "warning"), Str(i, "message"))).ToArray()
            : [];
    }

    public async Task<(EngineResult Result, IReadOnlyList<ImportItemResult> Items)> ImportAsync(
        IReadOnlyList<string> paths, IReadOnlyList<string> extracted, IProgress<EngineProgress>? progress, CancellationToken cancellation = default)
    {
        var args = new List<string> { "import" };
        foreach (var dir in extracted)
            args.Add($"--extracted={dir}");
        if (paths.Count > 0)
        {
            args.Add("--");
            args.AddRange(paths);
        }
        var result = await _client.RunAsync(args, EngineCommandKind.Mutation, progress, cancellation).ConfigureAwait(false);
        var items = Array(result.Data, "results").Select(r => new ImportItemResult(
            Str(r, "source"), Bool(r, "ok"), Str(r, "message"),
            Array(r, "mods").Select(m => new ImportedMod(Str(m, "kind") == "particle" ? ModKind.Particle : ModKind.Addon, Str(m, "name"), Bool(m, "new"))).ToArray()))
            .ToArray();
        return (result, items);
    }

    public Task<EngineResult> RemoveAsync(IReadOnlyList<string> names, CancellationToken cancellation = default) =>
        _client.RunAsync(["remove", "--", .. names], EngineCommandKind.Mutation, cancellation: cancellation);

    public Task<EngineResult> InstallAsync(string tfPath, IReadOnlyList<string> addons, IReadOnlyList<string> particles,
        BuildOptions options, IProgress<EngineProgress>? progress, CancellationToken cancellation = default)
    {
        var args = new List<string> { "install", "--tf-path", tfPath };
        args.AddRange(addons.Select(a => $"--addon={a}"));
        args.AddRange(particles.Select(p => $"--particle={p}"));
        if (options.DisablePaintColors) args.Add("--disable-paint-colors");
        if (!options.ShowConsole) args.Add("--hide-console");
        if (!options.FixModelPaths) args.Add("--no-fix-mdl-paths");
        if (options.SkipQuickPrecache) args.Add("--skip-quickprecache");
        return _client.RunAsync(args, EngineCommandKind.Mutation, progress, cancellation);
    }

    public Task<EngineResult> UninstallAsync(string tfPath, IProgress<EngineProgress>? progress, CancellationToken cancellation = default) =>
        _client.RunAsync(["uninstall", "--tf-path", tfPath], EngineCommandKind.Mutation, progress, cancellation);

    public Task<EngineResult> RecoverAsync(IProgress<EngineProgress>? progress, CancellationToken cancellation = default) =>
        _client.RunAsync(["recover"], EngineCommandKind.Mutation, progress, cancellation);

    public async Task<(EngineResult Result, IReadOnlyList<string> Enabled)> MigrateUserdataAsync(string source, CancellationToken cancellation = default)
    {
        var result = await _client.RunAsync(["migrate-userdata", "--from", source], EngineCommandKind.Mutation, cancellation: cancellation).ConfigureAwait(false);
        return (result, Array(result.Data, "enabled").Select(e => e.GetString() ?? "").Where(e => e.Length > 0).ToArray());
    }

    private static IReadOnlyList<ModOverlap> Overlaps(JsonElement data, string name) =>
        Array(data, name).Select(o => new ModOverlap(Str(o, "winner"), Str(o, "loser"),
            Array(o, "files").Select(f => f.GetString() ?? "").ToArray())).ToArray();

    private static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray()
            : [];

    private static string Str(JsonElement e, string name, string fallback = "") =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
