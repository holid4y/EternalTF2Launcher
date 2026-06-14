using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherTF2.Core;

namespace LauncherTF2.Services;

/// <summary>
/// Executes the Casual Preloader CLI and translates its NDJSON event stream
/// into progress callbacks and a final <see cref="PreloaderResult"/>.
/// See docs/preloader-integration-contract.md for the wire contract.
///
/// Engine discovery order:
///   1. LauncherConfig.PreloaderCliPath (either a preloader-cli.exe or an engine directory)
///   2. {exe dir}\preloader\preloader-cli.exe   (packaged release layout)
///   3. {exe dir}\preloader\cli\main.py         (engine sources shipped next to the launcher)
///   4. {repo}\external\cli\main.py             (development checkout, walking up from the exe)
/// Python sources are run through the same Python 3.11 runtime the installer
/// provisions first, then fall back to "py" or "python" from PATH.
/// </summary>
public class PreloaderService : IPreloaderService
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(15);

    private readonly SettingsService _settingsService;
    private readonly SemaphoreSlim _gate = new(1, 1); // engine must never run concurrently with itself

    private string? _exePath;        // frozen CLI build, used when set
    private string? _engineDir;      // python-source mode: directory containing cli\main.py
    private string? _pythonExe;      // interpreter for python-source mode

    public bool IsAvailable { get; private set; }
    public string EngineDescription { get; private set; } = "not found";
    public string? ManifestPath { get; private set; }

    /// <summary>
    /// True while a CLI command holds the single-flight gate — i.e. an engine
    /// operation (import/enable/disable/install/uninstall) is in flight. The
    /// launch flow waits on this so TF2 never starts mid-bake.
    /// </summary>
    public bool IsRunning => _gate.CurrentCount == 0;

    public PreloaderService(SettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        DiscoverEngine();
    }

    private void DiscoverEngine()
    {
        try
        {
            var configured = _settingsService.GetLauncherConfig().PreloaderCliPath;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (File.Exists(configured) && configured.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    _exePath = configured;
                }
                else if (Directory.Exists(configured) && File.Exists(Path.Combine(configured, "cli", "main.py")))
                {
                    _engineDir = configured;
                }
                else
                {
                    Logger.LogWarning($"[Preloader] Configured PreloaderCliPath is not usable: {configured}");
                }
            }

            var baseDir = AppContext.BaseDirectory;

            if (_exePath == null && _engineDir == null)
            {
                var packagedExe = Path.Combine(baseDir, "preloader", "preloader-cli.exe");
                if (File.Exists(packagedExe)) _exePath = packagedExe;
            }

            if (_exePath == null && _engineDir == null)
            {
                var packagedDir = Path.Combine(baseDir, "preloader");
                if (File.Exists(Path.Combine(packagedDir, "cli", "main.py"))) _engineDir = packagedDir;
            }

            if (_exePath == null && _engineDir == null)
            {
                // development layout: walk up from bin output towards the repo root
                var dir = new DirectoryInfo(baseDir);
                for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
                {
                    var candidate = Path.Combine(dir.FullName, "external");
                    if (File.Exists(Path.Combine(candidate, "cli", "main.py")))
                    {
                        _engineDir = candidate;
                        break;
                    }
                }
            }

            if (_engineDir != null)
                _pythonExe = FindPython();

            if (_exePath != null)
            {
                IsAvailable = true;
                EngineDescription = $"CLI executable: {_exePath}";
                ManifestPath = Path.Combine(Path.GetDirectoryName(_exePath) ?? string.Empty, "userdata", "data", "last_install_manifest.json");
            }
            else if (_engineDir != null && _pythonExe != null)
            {
                IsAvailable = true;
                EngineDescription = $"Python sources: {_engineDir} (via {_pythonExe})";
                ManifestPath = Path.Combine(_engineDir, "userdata", "data", "last_install_manifest.json");
            }
            else if (_engineDir != null)
            {
                EngineDescription = $"engine sources found at {_engineDir} but no Python interpreter on PATH";
            }

            Logger.LogInfo($"[Preloader] Engine discovery: {EngineDescription}");
        }
        catch (Exception ex)
        {
            Logger.LogError("[Preloader] Engine discovery failed", ex);
        }
    }

    private static string? FindPython()
    {
        var candidates = new List<string>();
        var bundledPython = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Python", "Python311", "python.exe");

        if (File.Exists(bundledPython))
            candidates.Add(bundledPython);

        foreach (var name in new[] { "py.exe", "python.exe" })
        {
            var found = SearchPath(name);
            if (found != null && !candidates.Contains(found, StringComparer.OrdinalIgnoreCase))
                candidates.Add(found);
        }

        foreach (var candidate in candidates)
        {
            if (CanImportValveParsers(candidate))
                return candidate;
        }

        return null;
    }

    private static bool CanImportValveParsers(string pythonExe)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("import valve_parsers");

            using var process = Process.Start(psi);
            if (process == null)
                return false;

            if (!process.WaitForExit((int)TimeSpan.FromSeconds(10).TotalMilliseconds))
            {
                TryKill(process);
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? SearchPath(string fileName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), fileName);
                // skip the WindowsApps store alias, which is a 0-byte reparse stub
                if (File.Exists(candidate) &&
                    !candidate.Contains(@"\Microsoft\WindowsApps\", StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
            catch
            {
                // malformed PATH entry — ignore
            }
        }
        return null;
    }

    public Task<PreloaderResult> CheckAsync(CancellationToken ct = default)
        => RunCommandAsync(["version"], null, ct);

    public Task<PreloaderResult> ValidateAsync(string tfPath, CancellationToken ct = default)
        => RunCommandAsync(["--tf-path", tfPath, "validate"], null, ct);

    public Task<PreloaderResult> InstallAsync(string tfPath, IProgress<PreloaderProgress>? progress = null, CancellationToken ct = default)
        => RunCommandAsync(["--tf-path", tfPath, "install"], progress, ct);

    public Task<PreloaderResult> UninstallAsync(string tfPath, IProgress<PreloaderProgress>? progress = null, CancellationToken ct = default)
        => RunCommandAsync(["--tf-path", tfPath, "uninstall"], progress, ct);

    public Task<PreloaderResult> ImportAsync(string[] paths, IProgress<PreloaderProgress>? progress = null, CancellationToken ct = default)
        => RunCommandAsync(["import", .. paths], progress, ct);

    public Task<PreloaderResult> EnableAsync(string addonName, CancellationToken ct = default)
        => RunCommandAsync(["enable", addonName], null, ct);

    public Task<PreloaderResult> DisableAsync(string addonName, CancellationToken ct = default)
        => RunCommandAsync(["disable", addonName], null, ct);

    public async Task<IReadOnlyList<PreloaderCompatIssue>> GetCompatibilityIssuesAsync(string[]? addons = null, CancellationToken ct = default)
    {
        // `compat` scans for model problems (IK-chain mismatch, etc.). Read-only.
        string[] args = addons is { Length: > 0 }
            ? ["compat", "--addons", .. addons]
            : ["compat"];
        var result = await RunCommandAsync(args, null, ct);
        if (!result.Success || string.IsNullOrEmpty(result.DataJson))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(result.DataJson);
            var list = new List<PreloaderCompatIssue>();
            if (doc.RootElement.TryGetProperty("issues", out var issues) && issues.ValueKind == JsonValueKind.Array)
                foreach (var i in issues.EnumerateArray())
                    list.Add(new PreloaderCompatIssue
                    {
                        Addon    = GetString(i, "addon") ?? string.Empty,
                        Severity = GetString(i, "severity") ?? "warning",
                        Message  = GetString(i, "message") ?? string.Empty,
                    });
            return list;
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[Preloader] compat payload parse failed: {ex.Message}");
            return [];
        }
    }

    public Task<PreloaderResult> RemoveAsync(string addonName, CancellationToken ct = default)
        => RunCommandAsync(["remove", addonName], null, ct);

    public async Task<PreloaderReport?> GetReportAsync(CancellationToken ct = default)
    {
        var result = await RunCommandAsync(["report"], null, ct);
        if (!result.Success || string.IsNullOrEmpty(result.DataJson))
        {
            if (!result.Success)
                Logger.LogWarning($"[Preloader] report failed: {result.ErrorSummary}");
            return null;
        }

        try
        {
            return ParseReport(result.DataJson, result.EngineVersion);
        }
        catch (Exception ex)
        {
            Logger.LogError("[Preloader] Failed to parse report payload", ex);
            return null;
        }
    }

    private static PreloaderReport ParseReport(string dataJson, string? engineVersion)
    {
        using var doc = JsonDocument.Parse(dataJson);
        var root = doc.RootElement;

        var enabled = GetStringArray(root, "enabled_addons");
        var addons = new List<PreloaderAddonInfo>();

        // "addons": { "<type>": [ { "addon_name", "type", "file_path", ... }, ... ] }
        if (root.TryGetProperty("addons", out var groups) && groups.ValueKind == JsonValueKind.Object)
        {
            foreach (var group in groups.EnumerateObject())
            {
                if (group.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var addon in group.Value.EnumerateArray())
                {
                    if (addon.ValueKind != JsonValueKind.Object) continue;
                    var folder = GetString(addon, "file_path") ?? GetString(addon, "addon_name");
                    if (string.IsNullOrWhiteSpace(folder)) continue;
                    addons.Add(new PreloaderAddonInfo
                    {
                        Name          = folder,
                        DisplayName   = GetString(addon, "addon_name") ?? folder,
                        Type          = GetString(addon, "type") ?? "Unknown",
                        IsParticleMod = false,
                    });
                }
            }
        }

        // "particle_mods": { "<mod name>": [ "<particle file>", ... ] }
        if (root.TryGetProperty("particle_mods", out var particles) && particles.ValueKind == JsonValueKind.Object)
        {
            foreach (var mod in particles.EnumerateObject())
            {
                addons.Add(new PreloaderAddonInfo
                {
                    Name          = mod.Name,
                    DisplayName   = mod.Name,
                    Type          = "Particles",
                    IsParticleMod = true,
                });
            }
        }

        return new PreloaderReport
        {
            EngineVersion = GetString(root, "version") ?? engineVersion,
            TfPath        = GetString(root, "tf_path"),
            EnabledAddons = enabled,
            Addons        = addons,
        };
    }

    private async Task<PreloaderResult> RunCommandAsync(string[] args, IProgress<PreloaderProgress>? progress, CancellationToken ct)
    {
        if (!IsAvailable)
        {
            return new PreloaderResult
            {
                Success = false,
                ExitCode = -1,
                Errors = [$"Casual Preloader engine not available ({EngineDescription})."],
            };
        }

        await _gate.WaitAsync(ct);
        try
        {
            return await ExecuteAsync(args, progress, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PreloaderResult> ExecuteAsync(string[] args, IProgress<PreloaderProgress>? progress, CancellationToken ct)
    {
        var logFile = Path.Combine(AppContext.BaseDirectory, "logs", "preloader.log");
        try { Directory.CreateDirectory(Path.GetDirectoryName(logFile)!); } catch { /* log dir is best-effort */ }

        var psi = new ProcessStartInfo
        {
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };

        if (_exePath != null)
        {
            psi.FileName = _exePath;
            psi.WorkingDirectory = Path.GetDirectoryName(_exePath) ?? string.Empty;
        }
        else
        {
            psi.FileName = _pythonExe!;
            psi.ArgumentList.Add(Path.Combine(_engineDir!, "cli", "main.py"));
            psi.WorkingDirectory = _engineDir!;
        }

        psi.ArgumentList.Add("--json");
        psi.ArgumentList.Add("--log-file");
        psi.ArgumentList.Add(logFile);
        foreach (var a in args) psi.ArgumentList.Add(a);

        Logger.LogInfo($"[Preloader] Running: {psi.FileName} {string.Join(' ', psi.ArgumentList)}");

        using var process = new Process { StartInfo = psi };
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(CommandTimeout);

        try
        {
            if (!process.Start())
                return Fail(args, -1, "Failed to start the preloader process.");

            PreloaderResult? result = null;
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(timeoutCts.Token)) != null)
            {
                var parsed = ParseEvent(line, progress);
                if (parsed != null) result = parsed;
            }

            await process.WaitForExitAsync(timeoutCts.Token);
            var stderr = (await stderrTask).Trim();

            if (result == null)
            {
                // No result event means the engine died early; stderr is then the
                // only record of why. Log it at Warning (not Debug) so the cause
                // isn't dropped, and fold a tail into the user-facing error.
                Logger.LogWarning(
                    $"[Preloader] Process exited (code {process.ExitCode}) without a result event." +
                    (stderr.Length > 0 ? $" stderr:\n{stderr}" : " (no stderr output)"));

                var detail = stderr.Length > 0 ? $"\n\n{LastLines(stderr, 8)}" : string.Empty;
                return Fail(args, process.ExitCode,
                    $"The preloader exited (code {process.ExitCode}) without reporting a result.{detail}");
            }

            if (stderr.Length > 0)
                Logger.LogDebug($"[Preloader] stderr:\n{stderr}");

            return new PreloaderResult
            {
                Success       = result.Success && process.ExitCode == 0,
                Command       = result.Command,
                Warnings      = result.Warnings,
                Errors        = result.Errors,
                DurationMs    = result.DurationMs,
                EngineVersion = result.EngineVersion,
                DataJson      = result.DataJson,
                ExitCode      = process.ExitCode,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            return Fail(args, -1, $"The preloader timed out after {CommandTimeout.TotalMinutes:0} minutes.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return Fail(args, -1, "The preloader operation was cancelled.");
        }
        catch (Exception ex)
        {
            Logger.LogError("[Preloader] Command execution failed", ex);
            TryKill(process);
            return Fail(args, -1, $"Preloader execution failed: {ex.Message}");
        }
    }

    private static PreloaderResult Fail(string[] args, int exitCode, string error) => new()
    {
        Success  = false,
        Command  = args.Length > 0 ? args[^1] : string.Empty,
        ExitCode = exitCode,
        Errors   = [error],
    };

    /// <summary>Returns the last <paramref name="count"/> non-empty lines of
    /// <paramref name="text"/> — used to keep an engine traceback's most relevant
    /// tail in a user-facing error without dumping the whole thing.</summary>
    private static string LastLines(string text, int count)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length <= count
            ? string.Join('\n', lines)
            : string.Join('\n', lines[^count..]);
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    /// <summary>Parses one NDJSON line; returns a result for "result" events, null otherwise.</summary>
    private static PreloaderResult? ParseEvent(string line, IProgress<PreloaderProgress>? progress)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var eventType = root.TryGetProperty("event", out var ev) ? ev.GetString() : null;

            if (eventType == "progress")
            {
                progress?.Report(new PreloaderProgress
                {
                    Stage   = GetString(root, "stage") ?? "Working",
                    Percent = root.TryGetProperty("percent", out var pct) ? pct.GetInt32() : 0,
                    Message = GetString(root, "message") ?? string.Empty,
                });
                return null;
            }

            if (eventType == "result")
            {
                return new PreloaderResult
                {
                    Success       = root.TryGetProperty("success", out var ok) && ok.GetBoolean(),
                    Command       = GetString(root, "command") ?? string.Empty,
                    Warnings      = GetStringArray(root, "warnings"),
                    Errors        = GetStringArray(root, "errors"),
                    DurationMs    = root.TryGetProperty("durationMs", out var dur) ? dur.GetInt64() : 0,
                    EngineVersion = GetString(root, "version"),
                    DataJson      = root.TryGetProperty("data", out var data) ? data.GetRawText() : null,
                };
            }
        }
        catch (JsonException)
        {
            // tolerate non-JSON noise on stdout per contract robustness rules
            Logger.LogDebug($"[Preloader] Ignoring non-JSON stdout line: {line}");
        }

        return null;
    }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static string[] GetStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array) return [];
        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString()!);
        return [.. list];
    }
}
