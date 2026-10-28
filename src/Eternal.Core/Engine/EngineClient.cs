using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Eternal.Core.Logging;

namespace Eternal.Core.Engine;

public sealed record EngineProgress(string Stage, int Percent, string Message);

public sealed class EngineResult
{
    public bool Success { get; init; }
    public string Command { get; init; } = "";
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
    public long DurationMs { get; init; }
    public int ExitCode { get; init; }
    public JsonElement Data { get; init; }

    public string ErrorSummary => Errors.Count > 0 ? string.Join('\n', Errors) : "The preloader reported an unknown error.";

    public static EngineResult Fail(string command, string error, int exitCode = -1) =>
        new() { Command = command, Errors = [error], ExitCode = exitCode };
}

/// <summary>
/// Whether a command may be stopped. Queries are killed when they hang;
/// mutations write into game files and are never interrupted once started,
/// because a half-finished write is worse than a slow one.
/// </summary>
public enum EngineCommandKind { Query, Mutation }

/// <summary>Runs the engine CLI and reads its NDJSON output, one command at a time.</summary>
public sealed class EngineClient
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromMinutes(2);
    private const int StderrLimit = 64 * 1024;

    private readonly EngineRuntime _runtime;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EngineClient(EngineRuntime runtime) => _runtime = runtime;

    public bool IsBusy => _gate.CurrentCount == 0;

    /// <param name="cancellation">
    /// Cancels waiting for the engine. For mutations it is ignored once the process has started.
    /// </param>
    public async Task<EngineResult> RunAsync(
        IReadOnlyList<string> args,
        EngineCommandKind kind,
        IProgress<EngineProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        var command = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "engine";
        var start = _runtime.CreateStartInfo(args);
        if (start is null)
            return EngineResult.Fail(command, _runtime.Status.Message);

        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            return await ExecuteAsync(start, command, kind, progress,
                kind == EngineCommandKind.Query ? cancellation : CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<EngineResult> ExecuteAsync(ProcessStartInfo start, string command, EngineCommandKind kind,
        IProgress<EngineProgress>? progress, CancellationToken cancellation)
    {
        using var process = new Process { StartInfo = start };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        if (kind == EngineCommandKind.Query)
            timeout.CancelAfter(QueryTimeout);

        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (stderr)
            {
                if (stderr.Length < StderrLimit)
                    stderr.AppendLine(e.Data);
            }
        };

        Log.Debug($"engine: {start.FileName} {string.Join(' ', start.ArgumentList)}");
        try
        {
            if (!process.Start())
                return EngineResult.Fail(command, "The preloader process could not be started.");
            process.BeginErrorReadLine();

            EngineResult? result = null;
            while (await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
            {
                if (Parse(line, progress) is { } parsed)
                    result = parsed;
            }
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            string errors;
            lock (stderr)
                errors = stderr.ToString().Trim();

            if (result is null)
            {
                Log.Warn($"engine {command} exited with {process.ExitCode} and no result. stderr:\n{errors}");
                var tail = string.Join('\n', errors.Split('\n').TakeLast(8));
                return EngineResult.Fail(command, $"The preloader stopped unexpectedly (exit code {process.ExitCode}).{(tail.Length > 0 ? "\n\n" + tail : "")}", process.ExitCode);
            }
            if (errors.Length > 0)
                Log.Debug($"engine {command} stderr:\n{errors}");

            return new EngineResult
            {
                // Partial imports report success with a non-zero exit; the per-item results tell the rest.
                Success = result.Success,
                Command = result.Command,
                Warnings = result.Warnings,
                Errors = result.Errors,
                DurationMs = result.DurationMs,
                ExitCode = process.ExitCode,
                Data = result.Data,
            };
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            return EngineResult.Fail(command, cancellation.IsCancellationRequested
                ? "Cancelled."
                : $"The preloader did not answer within {QueryTimeout.TotalMinutes:0} minutes.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Log.Error($"engine {command} failed to run", ex);
            if (kind == EngineCommandKind.Query)
                Kill(process);
            else
                await WaitIfRunningAsync(process).ConfigureAwait(false);
            return EngineResult.Fail(command, $"The preloader could not run: {ex.Message}");
        }
    }

    // A write that is still going must finish before the next command touches the same files.
    private static async Task WaitIfRunningAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
                await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // never started
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // already gone
        }
    }

    internal static EngineResult? Parse(string line, IProgress<EngineProgress>? progress)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            switch (root.TryGetProperty("event", out var ev) ? ev.GetString() : null)
            {
                case "progress":
                    progress?.Report(new EngineProgress(
                        Str(root, "stage") ?? "Working",
                        root.TryGetProperty("percent", out var pct) && pct.TryGetInt32(out var p) ? p : 0,
                        Str(root, "message") ?? ""));
                    return null;
                case "result":
                    return new EngineResult
                    {
                        Success = root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True,
                        Command = Str(root, "command") ?? "",
                        Warnings = Strings(root, "warnings"),
                        Errors = Strings(root, "errors"),
                        DurationMs = root.TryGetProperty("durationMs", out var d) && d.TryGetInt64(out var ms) ? ms : 0,
                        Data = root.TryGetProperty("data", out var data) ? data.Clone() : default,
                    };
                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            Log.Debug($"engine stdout (not JSON): {line}");
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string[] Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
            : [];
}
