using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Eternal.App.Services;
using Eternal.App.Theming;
using Eternal.App.ViewModels;
using Eternal.App.Views;
using Eternal.Core;
using Eternal.Core.Games;
using Eternal.Core.Logging;
using Eternal.Core.Platform;
using Eternal.Core.Steam;

namespace Eternal.App;

public partial class App : Application
{
    internal static AppPaths Paths { get; set; } = AppPaths.Detect();
    internal static SingleInstance? Instance { get; set; }

    /// <summary>Set once the player quits; errors from tearing down the desktop integration are expected after it.</summary>
    internal static bool Quitting { get; set; }

    private AppServices? _services;
    private MainViewModel? _main;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private bool _switching;
    private static readonly TimeSpan CleanupLimit = TimeSpan.FromSeconds(10);
    private bool _shuttingDown;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                var theme = new ThemeService(this);
                _tray = TrayIconSetup.Setup(this, () => _window?.Reveal(), theme, () => Quit(desktop));
                var main = Open(StartingGame(), theme, desktop, null);
                Dispatcher.UIThread.UnhandledException += OnUnhandledException;
                var window = _window = new MainWindow { DataContext = main };
                desktop.MainWindow = window;
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                desktop.ShutdownRequested += OnShutdownRequested;
                window.Opened += (_, _) => _ = _main?.StartAsync();
                window.ExitRequested += () => Quit(desktop);
                if (Instance is not null)
                    Instance.ActivationRequested += () => Dispatcher.UIThread.Post(window.Reveal);
            }
            catch (Exception ex)
            {
                Log.Error("Startup failed", ex);
                desktop.MainWindow = StartupErrorWindow.Create(ex, Paths.LogsDir);
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static GameDefinition StartingGame()
    {
        var settings = LauncherSettingsStore.Peek(Paths);
        var game = GameCatalog.Find(settings.SelectedGame);
        return SteamLocator.IsTf2Folder(settings.Game.Path) || SteamLocator.FindGame(game) is not null ? game : GameCatalog.Tf2;
    }

    /// <summary>Builds everything the launcher holds for <paramref name="game"/>. The updater is the one part that carries over.</summary>
    private MainViewModel Open(GameDefinition game, ThemeService theme, IClassicDesktopStyleApplicationLifetime desktop, UpdateViewModel? updates)
    {
        var services = _services = new AppServices(Paths, game);
        RemoteImage.Cache = services.Images;
        theme.Apply(services.Launcher.Settings);
        var main = _main = new MainViewModel(services, theme, updates);
        main.QuitRequested += () => Quit(desktop);
        main.GameSwitchRequested += async next => await SwitchGameAsync(next, desktop);
        TrayIconSetup.Follow(_tray, services.Launcher);
        return main;
    }

    private async Task SwitchGameAsync(GameDefinition game, IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_switching || _shuttingDown || _services is not { } services || _main is not { } leaving || _window is not { } window)
            return;
        _switching = true;
        try
        {
            Log.Info($"Switching to {game.Name}");
            await window.LeaveGameAsync();
            PreviewPlayer.Stop();
            leaving.Close();
            await leaving.PrepareForExitAsync();
            services.Launcher.Settings.SelectedGame = game.Id;
            await services.DisposeAsync();

            MainViewModel main;
            try
            {
                main = Open(game, leaving.Theme, desktop, leaving.Updates);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error($"{game.Name} couldn't be opened", ex);
                main = Open(GameCatalog.Tf2, leaving.Theme, desktop, leaving.Updates);
                main.Notify($"{game.Name} couldn't be opened, so the launcher went back to Team Fortress 2: {ex.Message}", isError: true);
            }
            window.DataContext = main;
            _ = main.StartAsync();
        }
        finally
        {
            await window.EnterGameAsync();
            _switching = false;
        }
    }

    // A failing button (a feed that changed format, a download that broke) should
    // cost that action, not the whole launcher.
    private void OnUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled error in the UI", e.Exception);
        _main?.Notify($"Something went wrong: {e.Exception.Message}", isError: true);
        e.Handled = true;
    }

    /// <summary>Shutdown() skips ShutdownRequested, and saving and the update installer both run from that event.</summary>
    private void Quit(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (!_shuttingDown)
            desktop.TryShutdown();
    }

    private async void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (_services is null || _main is null)
            return;
        e.Cancel = true;
        if (_shuttingDown)
            return;
        _shuttingDown = true;
        Quitting = true;
        Log.Info("Quitting");
        try
        {
            PreviewPlayer.Stop();
            await _main.PrepareForExitAsync();
            await _services.DisposeAsync().AsTask().WaitAsync(CleanupLimit);
        }
        catch (Exception ex)
        {
            Log.Error("Shutdown cleanup failed", ex);
        }
        _main.Updates.FinishOnExit();
        // Replacing the list disposes the tray now, while the UI thread still runs, so
        // its bus objects don't outlive it; Avalonia's own exit then has none left.
        TrayIcon.SetIcons(this, []);
        ((IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!).Shutdown();
    }
}
