using Eternal.Core.Logging;
using Eternal.Core.Persistence;
using Eternal.Core.Platform;

namespace Eternal.Core;

public sealed class LauncherSettingsStore : IDisposable
{
    private readonly string _path;
    private readonly Timer _timer;

    public LauncherSettingsStore(AppPaths paths)
    {
        _path = paths.LauncherSettingsFile;
        Settings = JsonFile.Load(_path, CoreJson.Default.LauncherSettings, () => new LauncherSettings(), out var outcome);
        LoadOutcome = outcome;
        _timer = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public LauncherSettings Settings { get; }
    public LoadOutcome LoadOutcome { get; }
    public bool IsNew => LoadOutcome.State == LoadState.New;

    public event Action? Changed;

    /// <summary>Reads settings before anything else is set up (log level, portable checks).</summary>
    public static LauncherSettings Peek(AppPaths paths) =>
        JsonFile.Load(paths.LauncherSettingsFile, CoreJson.Default.LauncherSettings, () => new LauncherSettings(), out _);

    public void Update(Action<LauncherSettings> change)
    {
        change(Settings);
        Changed?.Invoke();
        _timer.Change(400, Timeout.Infinite);
    }

    public void Save()
    {
        if (!LoadOutcome.CanSave)
            return;
        try
        {
            JsonFile.Save(_path, Settings, CoreJson.Default.LauncherSettings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Couldn't save launcher settings: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        Save();
    }
}
