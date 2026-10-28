using Eternal.Core.Logging;
using Eternal.Core.Persistence;

namespace Eternal.Core.Settings;

/// <summary>
/// game.json and autoexec.cfg can fail separately; a setting that never
/// reached the game is not a success.
/// </summary>
public sealed record SaveReport(bool ConfigSaved, string? ConfigError, AutoexecResult Autoexec)
{
    public bool FullyApplied => ConfigSaved && Autoexec.Applied;
}

/// <summary>
/// Owns game.json. Edits are written shortly after the last change and
/// flushed before launching TF2 or exiting.
/// </summary>
public sealed class GameConfigStore : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

    private readonly string _path;
    private readonly Func<string?> _tfPath;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private string? _pendingUserContent;
    private bool _dirty;

    public GameConfigStore(string path, Func<string?> tfPath)
    {
        _path = path;
        _tfPath = tfPath;
        var config = JsonFile.Load(path, CoreJson.Default.GameConfig, () => new GameConfig(), out var outcome);
        LoadOutcome = outcome;
        Settings = new GameSettings(config);
        Settings.Changed += _ => ScheduleSave();
        _timer = new Timer(_ => _ = SaveAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public GameSettings Settings { get; }
    public LoadOutcome LoadOutcome { get; private set; }
    public SaveReport? LastReport { get; private set; }
    public bool IsNew => LoadOutcome.State == LoadState.New;

    public bool IsDirty
    {
        get
        {
            lock (_gate)
                return _dirty || _pendingUserContent is not null;
        }
    }

    public event Action<SaveReport>? Saved;

    public void ScheduleSave()
    {
        lock (_gate)
            _dirty = true;
        _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Replaces the player's section of autoexec.cfg on the next save (used
    /// when a profile carries its own lines). Kept until a write succeeds.
    /// </summary>
    public void SetPendingUserContent(string? content)
    {
        lock (_gate)
            _pendingUserContent = content;
        ScheduleSave();
    }

    public async Task<SaveReport> SaveAsync()
    {
        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(SaveCore).ConfigureAwait(false);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public Task<SaveReport> FlushAsync()
    {
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        return SaveAsync();
    }

    /// <summary>Re-applies the managed block without changing settings (e.g. after the TF2 path changes).</summary>
    public Task<SaveReport> ReapplyAsync()
    {
        lock (_gate)
            _dirty = true;
        return FlushAsync();
    }

    private SaveReport SaveCore()
    {
        GameConfig snapshot;
        string? userContent;
        lock (_gate)
        {
            snapshot = Settings.Snapshot();
            userContent = _pendingUserContent;
            _dirty = false;
        }

        var saved = false;
        string? error = null;
        if (!LoadOutcome.CanSave)
        {
            error = "game.json couldn't be read at startup, so saving is paused to protect it.";
        }
        else
        {
            try
            {
                JsonFile.Save(_path, snapshot, CoreJson.Default.GameConfig);
                saved = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
                lock (_gate)
                    _dirty = true;
            }
        }

        var autoexec = AutoexecFile.Write(_tfPath(), ManagedBlock.Build(new GameSettings(snapshot)), userContent);
        if (autoexec.Applied && userContent is not null)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pendingUserContent, userContent))
                    _pendingUserContent = null;
            }
        }

        if (!saved)
            Log.Warn($"Saving game settings failed: {error}");
        if (autoexec.Status == AutoexecStatus.Failed)
            Log.Warn($"autoexec.cfg not updated: {autoexec.Error}");

        var report = new SaveReport(saved, error, autoexec);
        LastReport = report;
        Saved?.Invoke(report);
        return report;
    }

    /// <summary>Second attempt for a file that was locked at startup.</summary>
    public bool RetryLoad()
    {
        if (LoadOutcome.CanSave)
            return true;
        var config = JsonFile.Load(_path, CoreJson.Default.GameConfig, () => new GameConfig(), out var outcome);
        if (!outcome.CanSave)
            return false;
        LoadOutcome = outcome;
        Settings.Replace(config);
        return true;
    }

    public void Dispose()
    {
        _timer.Dispose();
        _saveLock.Dispose();
    }
}
