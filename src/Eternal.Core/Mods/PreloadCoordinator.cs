using Eternal.Core.Engine;
using Eternal.Core.Logging;
using Eternal.Core.Persistence;

namespace Eternal.Core.Mods;

public enum PreloadPhase { Idle, Pending, Building, Blocked, Failed, Launching }

public sealed record PreloadStatus(PreloadPhase Phase, string Message)
{
    public int Percent { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public DateTimeOffset? AppliedAt { get; init; }
    public bool Installed { get; init; }
    public int ActiveMods { get; init; }
}

public sealed record LaunchAdmission(bool Allowed, bool PreloadInstalled, string? Problem = null)
{
    public static LaunchAdmission Refused(string problem) => new(false, false, problem);
}

public interface IPreloadBuilder
{
    /// <summary>Builds the preload, or removes it when nothing is enabled.</summary>
    Task<EngineResult> BuildAsync(string tfPath, DesiredPreload desired, IProgress<EngineProgress> progress);

    /// <summary>Restores vanilla files after a build that was cut off.</summary>
    Task<EngineResult> RecoverAsync(IProgress<EngineProgress> progress);
}

public sealed class EnginePreloadBuilder(PreloaderEngine engine) : IPreloadBuilder
{
    public Task<EngineResult> BuildAsync(string tfPath, DesiredPreload desired, IProgress<EngineProgress> progress) =>
        desired.IsEmpty
            ? engine.UninstallAsync(tfPath, progress)
            : engine.InstallAsync(tfPath, desired.Addons, desired.Particles, desired.Options.ToEngine(), progress);

    public Task<EngineResult> RecoverAsync(IProgress<EngineProgress> progress) => engine.RecoverAsync(progress);
}

/// <summary>
/// Owns every write into the game folder. Changes are coalesced into one
/// build; builds never run while TF2 is open or a launch is starting; and
/// Play only proceeds once a build covering the latest change has finished.
/// What was applied is persisted, so unsynced work survives a restart.
/// </summary>
public sealed class PreloadCoordinator : IAsyncDisposable
{
    private readonly IPreloadBuilder _builder;
    private readonly Func<DesiredPreload?> _desired;
    private readonly Func<string?> _tfPath;
    private readonly Func<bool> _gameRunning;
    private readonly string _recordPath;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _gamePoll;

    private readonly SemaphoreSlim _wake = new(0);
    private readonly SemaphoreSlim _launchGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();

    private TaskCompletionSource _pass = NewPass();
    private Task? _worker;
    private Timer? _gameWatch;
    private long _requests;
    private bool _immediate;
    private TaskCompletionSource? _skipDebounce;
    private bool _launchHold;
    private bool _building;
    private bool _recover;
    private string? _lastError;
    private PreloadStatus _status = new(PreloadPhase.Idle, "");

    public PreloadCoordinator(IPreloadBuilder builder, Func<DesiredPreload?> desired, Func<string?> tfPath,
        Func<bool> gameRunning, string recordPath, TimeSpan? debounce = null, TimeSpan? gamePoll = null)
    {
        _builder = builder;
        _desired = desired;
        _tfPath = tfPath;
        _gameRunning = gameRunning;
        _recordPath = recordPath;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(1500);
        _gamePoll = gamePoll ?? TimeSpan.FromSeconds(3);
        Record = JsonFile.Load(recordPath, ModJson.Default.PreloadRecord, () => new PreloadRecord(), out _);
    }

    public PreloadRecord Record { get; private set; }

    public PreloadStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public event Action<PreloadStatus>? StatusChanged;

    public event Action<EngineResult>? Recovered;

    /// <summary>
    /// A build was cut off (crash, power loss). The game files are restored on the
    /// next pass, under the same rules as a build: never while TF2 is running.
    /// </summary>
    public void RecoverInterrupted()
    {
        lock (_gate)
            _recover = true;
        Record = new PreloadRecord();
        SaveRecord();
        BuildNow();
    }

    public void Start()
    {
        _worker ??= Task.Run(WorkerAsync);
        _wake.Release();
    }

    /// <summary>Schedules a build after the debounce window; repeated calls coalesce.</summary>
    public void RequestBuild()
    {
        Interlocked.Increment(ref _requests);
        if (Status.Phase is PreloadPhase.Idle or PreloadPhase.Failed or PreloadPhase.Blocked)
            SetStatus(PreloadPhase.Pending, "Waiting to apply your changes…");
        _wake.Release();
    }

    public void BuildNow()
    {
        lock (_gate)
        {
            _immediate = true;
            _skipDebounce?.TrySetResult();
        }
        RequestBuild();
    }

    /// <summary>The game files no longer match <see cref="Record"/> (e.g. after crash recovery).</summary>
    public void Invalidate()
    {
        Record = new PreloadRecord();
        SaveRecord();
        RequestBuild();
    }

    public bool IsDirty()
    {
        var tf = _tfPath();
        var desired = _desired();
        return desired is not null && !string.IsNullOrWhiteSpace(tf) && NeedsBuild(desired, tf);
    }

    /// <summary>
    /// Nothing enabled and nothing of ours installed means there is nothing to
    /// do: a player without mods never gets their game files rewritten, and a
    /// preload made by another tool is left alone.
    /// </summary>
    private bool NeedsBuild(DesiredPreload desired, string tf) =>
        desired.IsEmpty ? Record.Installed : desired.Fingerprint(tf) != Record.AppliedFingerprint;

    /// <summary>
    /// Waits for pending or running work, then reserves the game folder until
    /// <see cref="CompleteLaunch"/>.
    /// </summary>
    public async Task<LaunchAdmission> AdmitLaunchAsync(bool allowStale, CancellationToken cancellation = default)
    {
        await _launchGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_launchHold)
                    return LaunchAdmission.Refused("TF2 is already starting.");
            }

            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                Task pass;
                bool building;
                lock (_gate)
                {
                    pass = _pass.Task;
                    building = _building;
                }
                if (building)
                {
                    await pass.WaitAsync(cancellation).ConfigureAwait(false);
                    continue;
                }

                var phase = Status.Phase;

                if (!allowStale && IsDirty() && !_gameRunning())
                {
                    if (phase == PreloadPhase.Failed && _lastError is not null)
                        return LaunchAdmission.Refused(_lastError);
                    if (string.IsNullOrWhiteSpace(_tfPath()))
                        return LaunchAdmission.Refused("Set your TF2 folder first.");

                    BuildNow();
                    await pass.WaitAsync(cancellation).ConfigureAwait(false);
                    var after = Status;
                    if (after.Phase is PreloadPhase.Failed or PreloadPhase.Blocked && IsDirty())
                        return LaunchAdmission.Refused(after.Message);
                    continue;
                }

                lock (_gate)
                {
                    // A pass may have claimed the folder while the checks above ran.
                    if (_building)
                        continue;
                    _launchHold = true;
                }
                SetStatus(PreloadPhase.Launching, "Starting TF2…");
                return new LaunchAdmission(true, Record.Installed);
            }
        }
        finally
        {
            _launchGate.Release();
        }
    }

    public void CompleteLaunch()
    {
        lock (_gate)
            _launchHold = false;
        SetStatus(IsDirty() ? PreloadPhase.Pending : PreloadPhase.Idle, IsDirty() ? "Waiting to apply your changes…" : "");
        _wake.Release();
    }

    /// <summary>Lets an in-flight build finish before the app exits.</summary>
    public async Task ShutdownAsync()
    {
        Task pass;
        lock (_gate)
            pass = _pass.Task;
        if (Status.Phase == PreloadPhase.Building)
            await pass.ConfigureAwait(false);
    }

    private async Task WorkerAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(token).ConfigureAwait(false);
                while (_wake.CurrentCount > 0)
                    await _wake.WaitAsync(token).ConfigureAwait(false);

                await PassAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error("Preload worker failed", ex);
                SetStatus(PreloadPhase.Failed, $"Applying mods failed: {ex.Message}");
            }
            finally
            {
                CompletePass();
            }
        }
    }

    private async Task PassAsync(CancellationToken token)
    {
        // Wait until changes stop arriving for the debounce window; BuildNow cuts the wait short.
        while (true)
        {
            Task skip;
            lock (_gate)
            {
                if (_immediate)
                {
                    _immediate = false;
                    break;
                }
                skip = (_skipDebounce = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
            var seen = Interlocked.Read(ref _requests);
            await Task.WhenAny(Task.Delay(_debounce, token), skip).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (skip.IsCompleted || Interlocked.Read(ref _requests) == seen)
            {
                lock (_gate)
                    _immediate = false;
                break;
            }
        }

        lock (_gate)
        {
            // Checked and claimed together, so a launch can't be admitted in between.
            if (_launchHold)
                return;
            _building = true;
        }
        try
        {
            await ApplyAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
                _building = false;
        }
    }

    private async Task ApplyAsync()
    {
        bool recover;
        lock (_gate)
            recover = _recover;
        if (recover)
        {
            if (_gameRunning())
            {
                SetStatus(PreloadPhase.Blocked, "Close TF2 so the launcher can restore the game files.");
                WatchForGameExit();
                return;
            }
            if (!await RecoverAsync().ConfigureAwait(false))
                return;
        }

        var tf = _tfPath();
        var desired = _desired();
        if (desired is null)
        {
            if (Status.Phase is PreloadPhase.Pending)
                SetStatus(PreloadPhase.Blocked, "The mod engine isn't ready yet.");
            return;
        }
        if (string.IsNullOrWhiteSpace(tf))
        {
            SetStatus(PreloadPhase.Blocked, "Set your TF2 folder in the Launcher tab to apply mods.");
            return;
        }
        if (!NeedsBuild(desired, tf))
        {
            SetStatus(PreloadPhase.Idle, "");
            return;
        }
        if (_gameRunning())
        {
            SetStatus(PreloadPhase.Blocked, "Close TF2 to apply your mod changes.");
            WatchForGameExit();
            return;
        }

        // A failed build waits for a new change or an explicit retry instead of looping.
        if (await BuildAsync(tf, desired).ConfigureAwait(false) && IsDirty())
        {
            Interlocked.Increment(ref _requests);
            _wake.Release();
        }
    }

    private async Task<bool> RecoverAsync()
    {
        SetStatus(PreloadPhase.Building, "Restoring the game files…");
        var progress = new InlineProgress<EngineProgress>(p => SetStatus(PreloadPhase.Building, p.Message, p.Percent));
        var result = await _builder.RecoverAsync(progress).ConfigureAwait(false);
        lock (_gate)
            _recover = false;
        if (result.Success)
            Log.Info("Game files restored after an interrupted build");
        else
        {
            _lastError = result.ErrorSummary;
            Record = new PreloadRecord { Installed = true };
            SaveRecord();
            SetStatus(PreloadPhase.Failed, $"Restoring the game files failed: {result.ErrorSummary}");
        }
        Recovered?.Invoke(result);
        return result.Success;
    }

    private async Task<bool> BuildAsync(string tf, DesiredPreload desired)
    {
        SetStatus(PreloadPhase.Building, desired.IsEmpty ? "Restoring the game files…" : "Applying mods to the game…");
        var progress = new InlineProgress<EngineProgress>(p => SetStatus(PreloadPhase.Building, p.Message, p.Percent));
        var result = await _builder.BuildAsync(tf, desired, progress).ConfigureAwait(false);

        if (result.Success)
        {
            _lastError = null;
            Record = new PreloadRecord
            {
                AppliedFingerprint = desired.Fingerprint(tf),
                AppliedAt = DateTimeOffset.Now,
                Installed = !desired.IsEmpty,
                TfPath = tf,
                Addons = [.. desired.Addons],
                Particles = [.. desired.Particles],
                Warnings = [.. result.Warnings],
            };
            SaveRecord();
            SetStatus(PreloadPhase.Idle, desired.IsEmpty ? "Game files are vanilla." : "Mods are ready.");
            Log.Info($"Preload applied: {desired.Addons.Count} addon(s), {desired.Particles.Count} particle mod(s) in {result.DurationMs} ms");
            return true;
        }

        // The engine says what it left behind: "unchanged" keeps the previous
        // preload, "vanilla" means it rolled back. Anything else (including no
        // answer at all) may have left our files in the game, so the record
        // stays dirty until a later build or removal succeeds.
        _lastError = result.ErrorSummary;
        var gameFiles = result.Data.ValueKind == System.Text.Json.JsonValueKind.Object
            && result.Data.TryGetProperty("game_files", out var state) ? state.GetString() : null;
        if (gameFiles != "unchanged")
        {
            Record = new PreloadRecord { TfPath = tf, Installed = gameFiles != "vanilla" };
            SaveRecord();
        }
        SetStatus(PreloadPhase.Failed, result.ErrorSummary);
        Log.Warn($"Preload build failed: {result.ErrorSummary}");
        return false;
    }

    private void WatchForGameExit()
    {
        lock (_gate)
        {
            _gameWatch ??= new Timer(_ =>
            {
                if (_gameRunning())
                    return;
                lock (_gate)
                {
                    _gameWatch?.Dispose();
                    _gameWatch = null;
                }
                lock (_gate)
                    _immediate = true;
                _wake.Release();
            }, null, _gamePoll, _gamePoll);
        }
    }

    private void CompletePass()
    {
        TaskCompletionSource done;
        lock (_gate)
        {
            done = _pass;
            _pass = NewPass();
        }
        done.TrySetResult();
    }

    private static TaskCompletionSource NewPass() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void SaveRecord()
    {
        try
        {
            JsonFile.Save(_recordPath, Record, ModJson.Default.PreloadRecord);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Couldn't save preload state: {ex.Message}");
        }
    }

    private void SetStatus(PreloadPhase phase, string message, int percent = 0)
    {
        PreloadStatus status;
        lock (_gate)
        {
            _status = status = new PreloadStatus(phase, message)
            {
                Percent = percent,
                Warnings = Record.Warnings,
                AppliedAt = Record.AppliedAt,
                Installed = Record.Installed,
                ActiveMods = Record.Addons.Count + Record.Particles.Count,
            };
        }
        StatusChanged?.Invoke(status);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_worker is not null)
        {
            try { await _worker.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _gameWatch?.Dispose();
        _stop.Dispose();
    }
}
