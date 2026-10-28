using System.Diagnostics;
using System.Text.Json;
using Eternal.Core.Logging;
using Eternal.Core.Platform;

namespace Eternal.Core.Engine;

public enum EngineState { Checking, Ready, NeedsSetup, SettingUp, NoPython, Missing, Broken }

public sealed record EngineStatus(EngineState State, string Message)
{
    public string? PythonVersion { get; init; }
    public bool? WineAvailable { get; init; }
    public string? UpstreamVersion { get; init; }
    public string? UpstreamCommit { get; init; }
    public bool Interrupted { get; init; }
    public bool PreloadPresent { get; init; }
    public IReadOnlyList<string> LegacyConflicts { get; init; } = [];
    public bool IsReady => State == EngineState.Ready;
}

/// <summary>
/// Locates the preloader engine and the Python that runs it. Release builds
/// ship a frozen engine and need nothing else; source installs get a private
/// virtual environment under the data folder, created on request.
/// </summary>
public sealed class EngineRuntime
{
    private const string MinPython = "3.12";
    private readonly AppPaths _paths;
    private readonly Func<string?> _pythonOverride;
    private EngineStatus _status = new(EngineState.Checking, "Checking the mod engine…");

    public EngineRuntime(AppPaths paths, Func<string?> pythonOverride, string? searchFrom = null)
    {
        _paths = paths;
        _pythonOverride = pythonOverride;
        (FrozenExecutable, SourceDir) = Discover(searchFrom ?? AppContext.BaseDirectory);
    }

    public string? FrozenExecutable { get; }
    public string? SourceDir { get; }
    public string? Python { get; private set; }
    public string VenvDir => Path.Combine(_paths.EngineDataDir, "venv");
    private string VenvPython => OperatingSystem.IsWindows()
        ? Path.Combine(VenvDir, "Scripts", "python.exe")
        : Path.Combine(VenvDir, "bin", "python");

    public EngineStatus Status
    {
        get => _status;
        private set
        {
            _status = value;
            StatusChanged?.Invoke(value);
        }
    }

    public event Action<EngineStatus>? StatusChanged;

    private static (string? Frozen, string? Source) Discover(string baseDir)
    {
        var frozen = Path.Combine(baseDir, "engine", OperatingSystem.IsWindows() ? "eternal-engine.exe" : "eternal-engine");
        if (File.Exists(frozen))
            return (frozen, null);

        for (var dir = new DirectoryInfo(baseDir); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "engine");
            if (File.Exists(Path.Combine(candidate, "run.py")) && Directory.Exists(Path.Combine(candidate, "vendor")))
                return (null, candidate);
        }
        return (null, null);
    }

    /// <summary>Command line for one engine call, or null when no engine can run.</summary>
    public ProcessStartInfo? CreateStartInfo(IEnumerable<string> engineArgs)
    {
        ProcessStartInfo psi;
        if (FrozenExecutable is not null)
        {
            psi = new ProcessStartInfo(FrozenExecutable);
        }
        else if (SourceDir is not null && Python is not null)
        {
            psi = new ProcessStartInfo(Python);
            // -I ignores PYTHONPATH and user site-packages that could shadow the engine's dependencies.
            foreach (var arg in new[] { "-I", "-X", "utf8", "-X", $"pycache_prefix={Path.Combine(_paths.EngineCacheDir, "pycache")}", Path.Combine(SourceDir, "run.py") })
                psi.ArgumentList.Add(arg);
        }
        else
        {
            return null;
        }

        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = System.Text.Encoding.UTF8;
        psi.StandardErrorEncoding = System.Text.Encoding.UTF8;
        psi.WorkingDirectory = _paths.EngineDataDir;

        // QuickPrecache compiles models with studiomdl.exe through Wine on Linux.
        // A private prefix keeps the player's own Wine setup out of it and the
        // overrides skip the Mono/Gecko install prompts.
        psi.Environment["WINEPREFIX"] = Path.Combine(_paths.EngineDataDir, "wine");
        psi.Environment["WINEDLLOVERRIDES"] = "mscoree=;mshtml=";
        psi.Environment["WINEDEBUG"] = "-all";

        foreach (var arg in new[] { "--json", "--data-dir", _paths.EngineDataDir, "--cache-dir", _paths.EngineCacheDir,
                                    "--log-file", Path.Combine(_paths.LogsDir, "engine.log") })
            psi.ArgumentList.Add(arg);
        foreach (var arg in engineArgs)
            psi.ArgumentList.Add(arg);
        return psi;
    }

    /// <summary>Finds an interpreter and asks the engine to check itself. Never throws.</summary>
    public async Task<EngineStatus> CheckAsync(EngineClient client, string? tfPath, CancellationToken cancellation = default)
    {
        Status = new EngineStatus(EngineState.Checking, "Checking the mod engine…");
        try
        {
            if (FrozenExecutable is null)
            {
                if (SourceDir is null)
                    return Status = new EngineStatus(EngineState.Missing, "The preloader engine files are missing. Reinstall the launcher.");

                Python = File.Exists(VenvPython) ? VenvPython : null;
                if (Python is null)
                {
                    var (found, version) = await FindSystemPythonAsync(cancellation).ConfigureAwait(false);
                    if (found is null)
                        return Status = new EngineStatus(EngineState.NoPython, NoPythonMessage(version)) { PythonVersion = version };
                    Python = found;
                }
            }

            var args = new List<string> { "doctor" };
            if (!string.IsNullOrWhiteSpace(tfPath))
                args.AddRange(["--tf-path", tfPath]);
            var result = await client.RunAsync(args, EngineCommandKind.Query, cancellation: cancellation).ConfigureAwait(false);
            return Status = Interpret(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("Engine check failed", ex);
            return Status = new EngineStatus(EngineState.Broken, $"The mod engine could not be checked: {ex.Message}");
        }
    }

    private EngineStatus Interpret(EngineResult result)
    {
        var data = result.Data;
        string? Text(params string[] path)
        {
            var e = data;
            foreach (var key in path)
            {
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out e))
                    return null;
            }
            return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        }
        bool? Flag(string key) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out var e)
            ? e.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;

        if (data.ValueKind != JsonValueKind.Object)
            return new EngineStatus(EngineState.Broken, result.ErrorSummary);

        var missingDeps = data.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object
            && deps.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.False);

        var status = new EngineStatus(EngineState.Ready, "Mod engine ready.")
        {
            PythonVersion = Text("python"),
            WineAvailable = Flag("wine"),
            UpstreamVersion = Text("upstream", "version"),
            UpstreamCommit = Text("upstream", "commit"),
            Interrupted = data.TryGetProperty("interrupted", out var interrupted) && interrupted.ValueKind == JsonValueKind.Object,
            PreloadPresent = Flag("modified") == true,
            LegacyConflicts = data.TryGetProperty("legacy_conflicts", out var legacy) && legacy.ValueKind == JsonValueKind.Array
                ? legacy.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()
                : [],
        };

        if (Flag("python_ok") == false)
            return status with { State = EngineState.NoPython, Message = NoPythonMessage(status.PythonVersion) };
        if (missingDeps && FrozenExecutable is null)
            return status with { State = EngineState.NeedsSetup, Message = "The mod engine needs a one-time setup to download its Python packages." };
        if (!result.Success)
            return status with { State = EngineState.Broken, Message = result.ErrorSummary };
        return status;
    }

    private static string NoPythonMessage(string? found)
    {
        var have = found is null ? "No Python installation was found." : $"Python {found} is too old.";
        var get = OperatingSystem.IsWindows()
            ? "Install Python 3.12 or newer from python.org, then press Check again."
            : "Install Python 3.12 or newer with your package manager (for example python3 and python3-venv), then press Check again.";
        return $"{have} The mod engine needs Python {MinPython}+. {get}";
    }

    /// <summary>Creates the private virtual environment and installs the engine's packages.</summary>
    public async Task<bool> SetupAsync(IProgress<string> output, CancellationToken cancellation = default)
    {
        if (SourceDir is null)
            return false;
        Status = new EngineStatus(EngineState.SettingUp, "Setting up the mod engine…");

        var (python, version) = await FindSystemPythonAsync(cancellation).ConfigureAwait(false);
        if (python is null)
        {
            Status = new EngineStatus(EngineState.NoPython, NoPythonMessage(version));
            return false;
        }

        output.Report($"Using Python {version} at {python}");
        if (!File.Exists(VenvPython))
        {
            output.Report("Creating the engine environment…");
            var (code, log) = await RunToolAsync(python, ["-m", "venv", VenvDir], output, cancellation).ConfigureAwait(false);
            if (code != 0)
            {
                var hint = log.Contains("ensurepip", StringComparison.OrdinalIgnoreCase)
                    ? " Your distribution ships venv separately: install python3-venv (or the equivalent) and retry."
                    : "";
                Status = new EngineStatus(EngineState.Broken, $"Creating the Python environment failed.{hint}");
                return false;
            }
        }

        output.Report("Installing engine packages…");
        var (pipCode, _) = await RunToolAsync(VenvPython,
            ["-m", "pip", "install", "--disable-pip-version-check", "--no-input", "-r", Path.Combine(SourceDir, "requirements.txt")],
            output, cancellation).ConfigureAwait(false);
        if (pipCode != 0)
        {
            Status = new EngineStatus(EngineState.Broken, "Installing the engine packages failed. Check your internet connection and retry.");
            return false;
        }

        Python = VenvPython;
        output.Report("Engine environment ready.");
        return true;
    }

    private async Task<(string? Path, string? Version)> FindSystemPythonAsync(CancellationToken cancellation)
    {
        string? newestTooOld = null;
        foreach (var (file, prefix) in PythonCandidates())
        {
            var version = await ProbeVersionAsync(file, prefix, cancellation).ConfigureAwait(false);
            if (version is null)
                continue;
            if (Version.TryParse(version, out var v) && v >= new Version(3, 12))
                return (prefix.Length == 0 ? file : ResolveLauncher(file, prefix), version);
            newestTooOld ??= version;
        }
        return (null, newestTooOld);
    }

    private IEnumerable<(string File, string[] Prefix)> PythonCandidates()
    {
        if (_pythonOverride() is { Length: > 0 } custom && File.Exists(custom))
            yield return (custom, []);

        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (var minor in new[] { 14, 13, 12 })
            {
                var path = Path.Combine(local, "Programs", "Python", $"Python3{minor}", "python.exe");
                if (File.Exists(path))
                    yield return (path, []);
            }
            if (Shell.FindOnPath("py") is { } launcher)
                yield return (launcher, ["-3"]);
            if (Shell.FindOnPath("python") is { } python)
                yield return (python, []);
            yield break;
        }

        foreach (var name in new[] { "python3.14", "python3.13", "python3.12", "python3" })
        {
            if (Shell.FindOnPath(name) is { } path)
                yield return (path, []);
        }
    }

    /// <summary>Turns "py -3" into the concrete interpreter path so the venv records it.</summary>
    private static string ResolveLauncher(string file, string[] prefix)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var p in prefix)
                psi.ArgumentList.Add(p);
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("import sys; print(sys.executable)");
            using var process = Process.Start(psi)!;
            var path = process.StandardOutput.ReadLine()?.Trim();
            process.WaitForExit(10_000);
            return File.Exists(path) ? path! : file;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return file;
        }
    }

    private static async Task<string?> ProbeVersionAsync(string file, string[] prefix, CancellationToken cancellation)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var p in prefix)
                psi.ArgumentList.Add(p);
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("import sys; print('%d.%d.%d' % sys.version_info[:3])");
            using var process = Process.Start(psi);
            if (process is null)
                return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var version = output.Trim();
            return process.ExitCode == 0 && Version.TryParse(version, out _) ? version : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<(int Code, string Log)> RunToolAsync(string file, string[] args, IProgress<string> output, CancellationToken cancellation)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        var log = new System.Text.StringBuilder();
        using var process = new Process { StartInfo = psi };
        void OnLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;
            lock (log)
                log.AppendLine(line);
            output.Report(line);
        }
        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
            Log.Info($"{Path.GetFileName(file)} {string.Join(' ', args)} exited with {process.ExitCode}");
            return (process.ExitCode, log.ToString());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            output.Report(ex.Message);
            return (-1, ex.Message);
        }
    }
}
