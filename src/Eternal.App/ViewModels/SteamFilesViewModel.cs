using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Eternal.App.Services;
using Eternal.Core.Steam;

namespace Eternal.App.ViewModels;

/// <summary>
/// Changes to Steam's own files that wait for Steam to close. They are written
/// as soon as Steam isn't running, checked every few seconds while any wait,
/// or right away through Restart Steam.
/// </summary>
public sealed partial class SteamFilesViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _check;
    private bool _applying;

    public SteamFilesViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
        _check = new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Background, (_, _) => _ = ApplyIfClosedAsync());
        services.SteamWrites.Changed += () => Dispatcher.UIThread.Post(Refresh);
        Refresh();
        _ = ApplyIfClosedAsync();
    }

    [ObservableProperty] private int _waiting;
    [ObservableProperty] private bool _restarting;

    public bool HasWaiting => Waiting > 0;

    public string WaitingText => Waiting == 1
        ? "1 change waits for Steam to close. Steam rewrites that file while it runs."
        : $"{Waiting} changes wait for Steam to close. Steam rewrites those files while it runs.";

    partial void OnWaitingChanged(int value)
    {
        OnPropertyChanged(nameof(HasWaiting));
        OnPropertyChanged(nameof(WaitingText));
        if (value > 0)
            _check.Start();
        else
            _check.Stop();
    }

    private void Refresh() => Waiting = _services.SteamWrites.Count;

    private async Task ApplyIfClosedAsync()
    {
        if (_applying || Restarting || Waiting == 0)
            return;
        _applying = true;
        try
        {
            var result = await Task.Run(_services.SteamWrites.TryApply);
            if (result.Applied > 0)
                _main.Notify($"Steam was closed, so {Plural(result.Applied)} {(result.Applied == 1 ? "was" : "were")} written to its files.");
            if (result.Problems.Count > 0)
                _main.Notify($"Some changes couldn't be written to Steam's files: {string.Join("; ", result.Problems)}", isError: true);
        }
        finally
        {
            _applying = false;
        }
    }

    [RelayCommand]
    private async Task RestartSteamAsync()
    {
        if (Restarting || Waiting == 0)
            return;
        if (GameProcess.IsRunning())
        {
            await _main.AlertAsync("Close TF2 first", "Steam can't be restarted while TF2 is running.");
            return;
        }
        if (!await _main.ConfirmAsync("Restart Steam",
                "Steam will close, the waiting changes are written, and Steam starts again. Downloads in progress resume on their own.",
                "Restart Steam"))
            return;

        Restarting = true;
        try
        {
            var root = SteamLocator.FindRoot();
            if (!await SteamProcess.ShutdownAsync(root, TimeSpan.FromSeconds(60)))
            {
                await _main.AlertAsync("Steam didn't close", "Steam is still running after a minute. Close it yourself and the changes are written right after.");
                return;
            }
            var result = await Task.Run(_services.SteamWrites.TryApply);
            SteamProcess.Start(root);
            if (result.Problems.Count > 0)
                _main.Notify($"Some changes couldn't be written: {string.Join("; ", result.Problems)}", isError: true);
            else
                _main.Notify($"Done. {Plural(result.Applied)} written, and Steam is starting again.");
        }
        finally
        {
            Restarting = false;
            Refresh();
        }
    }

    private static string Plural(int n) => n == 1 ? "1 change" : $"{n} changes";
}
