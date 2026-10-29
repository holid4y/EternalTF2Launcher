using System.Text.Json;
using Eternal.Core.Engine;
using Eternal.Core.Mods;

namespace Eternal.Core.Tests;

public sealed class PreloadCoordinatorTests : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("eternal-preload-").FullName;
    private readonly FakeBuilder _builder = new();
    private DesiredPreload? _desired = Desired("hitsound");
    private Action? _onDesired;
    private bool _gameRunning;
    private PreloadCoordinator? _coordinator;

    private static DesiredPreload Desired(params string[] addons) => new(addons, [], new ModBuildOptions(), "sig");
    private string TfPath => _dir;

    private PreloadCoordinator Create(TimeSpan? debounce = null)
    {
        _coordinator = new PreloadCoordinator(_builder, () => { _onDesired?.Invoke(); return _desired; }, () => TfPath, () => _gameRunning,
            Path.Combine(_dir, "preload-state.json"), debounce ?? TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(30));
        _coordinator.Start();
        return _coordinator;
    }

    public async ValueTask DisposeAsync()
    {
        _builder.ReleaseAll();
        if (_coordinator is not null)
            await _coordinator.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Play_during_the_debounce_builds_first_and_then_launches()
    {
        var coordinator = Create(debounce: TimeSpan.FromSeconds(30));
        _builder.AutoComplete = true;
        coordinator.RequestBuild();
        await Task.Delay(200);

        var admission = await coordinator.AdmitLaunchAsync(allowStale: false).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(admission.Allowed);
        Assert.True(admission.PreloadInstalled);
        Assert.Equal(1, _builder.Builds);
    }

    [Fact]
    public async Task Play_waits_for_a_long_build_instead_of_timing_out()
    {
        var coordinator = Create();
        coordinator.BuildNow();
        await _builder.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var admit = coordinator.AdmitLaunchAsync(allowStale: false);
        await Task.Delay(300);
        Assert.False(admit.IsCompleted);

        _builder.Complete(success: true);
        var admission = await admit.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(admission.Allowed);
    }

    [Fact]
    public async Task A_failed_build_refuses_the_launch_unless_the_player_insists()
    {
        var coordinator = Create();
        _builder.AutoComplete = true;
        _builder.NextSucceeds = false;
        coordinator.BuildNow();

        var refused = await coordinator.AdmitLaunchAsync(allowStale: false).WaitAsync(TimeSpan.FromSeconds(10));
        var insisted = await coordinator.AdmitLaunchAsync(allowStale: true).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(refused.Allowed);
        Assert.Contains("boom", refused.Problem);
        Assert.True(insisted.Allowed);
        Assert.False(insisted.PreloadInstalled);
    }

    [Fact]
    public async Task No_build_starts_while_a_launch_holds_the_game_folder()
    {
        var coordinator = Create();
        _builder.AutoComplete = true;
        await coordinator.AdmitLaunchAsync(allowStale: false).WaitAsync(TimeSpan.FromSeconds(10));
        var buildsBefore = _builder.Builds;

        _desired = Desired("hitsound", "hud");
        coordinator.RequestBuild();
        await Task.Delay(400);
        Assert.Equal(buildsBefore, _builder.Builds);

        coordinator.CompleteLaunch();
        await WaitUntil(() => _builder.Builds > buildsBefore);
    }

    [Fact]
    public async Task Builds_wait_for_tf2_to_close_and_then_resume()
    {
        _gameRunning = true;
        var coordinator = Create();
        _builder.AutoComplete = true;
        coordinator.RequestBuild();

        await WaitUntil(() => coordinator.Status.Phase == PreloadPhase.Blocked);
        Assert.Equal(0, _builder.Builds);

        _gameRunning = false;
        await WaitUntil(() => _builder.Builds == 1);
    }

    [Fact]
    public async Task Applied_state_survives_a_restart()
    {
        var first = Create();
        _builder.AutoComplete = true;
        first.BuildNow();
        await WaitUntil(() => _builder.Builds == 1 && first.Status.Phase == PreloadPhase.Idle);
        await first.DisposeAsync();

        var second = Create();
        Assert.False(second.IsDirty());

        _desired = Desired("hitsound", "new-mod");
        Assert.True(second.IsDirty());
    }

    [Fact]
    public async Task A_fresh_install_with_no_mods_never_touches_the_game()
    {
        _desired = Desired();
        var coordinator = Create();
        _builder.AutoComplete = true;

        coordinator.RequestBuild();
        var admission = await coordinator.AdmitLaunchAsync(allowStale: false).WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(300);

        Assert.True(admission.Allowed);
        Assert.False(coordinator.IsDirty());
        Assert.Equal(0, _builder.Builds);
    }

    [Fact]
    public async Task Turning_every_mod_off_removes_an_installed_preload()
    {
        var coordinator = Create();
        _builder.AutoComplete = true;
        coordinator.BuildNow();
        await WaitUntil(() => coordinator.Record.Installed);

        _desired = Desired();
        coordinator.BuildNow();
        await WaitUntil(() => _builder.Builds == 2 && !coordinator.Record.Installed);
    }

    [Fact]
    public async Task Status_follows_engine_progress_in_order_and_ends_idle()
    {
        // Progress queued elsewhere could land after the final status and leave
        // the UI showing "Applying mods" for a build that already finished.
        var coordinator = Create();
        var shown = new List<string>();
        _builder.AutoComplete = true;
        _builder.ProgressReports = 5;
        _builder.AfterReport = _ => shown.Add(coordinator.Status.Message);

        coordinator.BuildNow();
        await WaitUntil(() => coordinator.Record.Installed && coordinator.Status.Phase == PreloadPhase.Idle);

        Assert.Equal(["step 1", "step 2", "step 3", "step 4", "step 5"], shown);
    }

    [Fact]
    public async Task A_launch_waits_for_a_build_that_already_claimed_the_game_folder()
    {
        // The worker reads the desired state right after checking for a launch hold.
        var inPass = new SemaphoreSlim(0);
        var release = new ManualResetEventSlim();
        var first = 0;
        _onDesired = () =>
        {
            if (Interlocked.Exchange(ref first, 1) == 0)
            {
                inPass.Release();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        };
        var coordinator = Create();
        _builder.AutoComplete = true;
        coordinator.BuildNow();
        Assert.True(await inPass.WaitAsync(TimeSpan.FromSeconds(10)));

        var admit = coordinator.AdmitLaunchAsync(allowStale: true);
        await Task.Delay(300);
        Assert.False(admit.IsCompleted);

        release.Set();
        var admission = await admit.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(admission.Allowed);
        Assert.Equal(1, _builder.Builds);
    }

    [Fact]
    public async Task A_build_that_keeps_failing_is_not_retried_on_its_own()
    {
        var coordinator = Create();
        _builder.AutoComplete = true;
        _builder.NextSucceeds = false;

        coordinator.RequestBuild();
        await WaitUntil(() => coordinator.Status.Phase == PreloadPhase.Failed);
        await Task.Delay(500);

        Assert.Equal(1, _builder.Builds);
    }

    [Fact]
    public async Task A_removal_that_fails_halfway_stays_pending_until_it_works()
    {
        var coordinator = Create();
        _builder.AutoComplete = true;
        coordinator.BuildNow();
        await WaitUntil(() => coordinator.Record.Installed);

        _desired = Desired();
        _builder.NextSucceeds = false;
        _builder.NextGameFiles = "unknown";
        coordinator.BuildNow();
        await WaitUntil(() => _builder.Builds == 2 && coordinator.Status.Phase == PreloadPhase.Failed);
        Assert.True(coordinator.IsDirty());

        _builder.NextSucceeds = true;
        coordinator.BuildNow();
        await WaitUntil(() => _builder.Builds == 3 && coordinator.Status.Phase == PreloadPhase.Idle);
        Assert.False(coordinator.Record.Installed);
    }

    [Fact]
    public async Task Restoring_after_a_crash_waits_for_tf2_to_close()
    {
        _gameRunning = true;
        EngineResult? recovered = null;
        var coordinator = Create();
        coordinator.Recovered += result => recovered = result;
        _builder.AutoComplete = true;

        coordinator.RecoverInterrupted();
        await WaitUntil(() => coordinator.Status.Phase == PreloadPhase.Blocked);
        Assert.Equal(0, _builder.Recoveries);

        _gameRunning = false;
        await WaitUntil(() => recovered is not null && coordinator.Record.Installed);
        Assert.Equal(1, _builder.Recoveries);
        Assert.True(recovered!.Success);
    }

    [Fact]
    public async Task Validation_errors_keep_the_previous_preload_record()
    {
        var coordinator = Create();
        _builder.AutoComplete = true;
        coordinator.BuildNow();
        await WaitUntil(() => coordinator.Record.Installed);

        _builder.NextSucceeds = false;
        _builder.NextGameFiles = "unchanged";
        _desired = Desired("missing-mod");
        coordinator.BuildNow();
        await WaitUntil(() => coordinator.Status.Phase == PreloadPhase.Failed);

        Assert.True(coordinator.Record.Installed);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("condition not met");
            await Task.Delay(20);
        }
    }

    private sealed class FakeBuilder : IPreloadBuilder
    {
        private TaskCompletionSource<EngineResult> _current = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Builds;
        public bool AutoComplete;
        public int ProgressReports;
        public Action<EngineProgress>? AfterReport;
        public int Recoveries;

        public Task<EngineResult> RecoverAsync(IProgress<EngineProgress> progress)
        {
            Interlocked.Increment(ref Recoveries);
            return Task.FromResult(new EngineResult { Success = true, Command = "recover" });
        }
        public bool NextSucceeds = true;
        public string NextGameFiles = "vanilla";

        public Task<EngineResult> BuildAsync(string tfPath, DesiredPreload desired, IProgress<EngineProgress> progress)
        {
            Interlocked.Increment(ref Builds);
            _current = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Started.TrySetResult();
            // The engine reports progress on its own reader thread, right up to the result.
            for (var i = 1; i <= ProgressReports; i++)
            {
                var step = new EngineProgress("install", i * 100 / ProgressReports, $"step {i}");
                progress.Report(step);
                AfterReport?.Invoke(step);
            }
            if (AutoComplete)
                Complete(NextSucceeds);
            return _current.Task;
        }

        public void Complete(bool success)
        {
            var data = JsonDocument.Parse($"{{\"game_files\":\"{NextGameFiles}\"}}").RootElement.Clone();
            _current.TrySetResult(success
                ? new EngineResult { Success = true, Command = "install" }
                : new EngineResult { Success = false, Command = "install", Errors = ["boom"], Data = data });
        }

        public void ReleaseAll() => _current.TrySetResult(new EngineResult { Success = true });
    }
}
