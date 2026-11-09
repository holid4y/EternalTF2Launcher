using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Eternal.App.Services;
using Eternal.App.Theming;
using Eternal.App.ViewModels;
using Eternal.App.Views;
using Eternal.Core.Logging;
using Eternal.Core.Platform;

namespace Eternal.App;

public partial class App : Application
{
    internal static AppPaths Paths { get; set; } = AppPaths.Detect();
    internal static SingleInstance? Instance { get; set; }

    /// <summary>Set once the player quits; errors from tearing down the desktop integration are expected after it.</summary>
    internal static bool Quitting { get; set; }

    private AppServices? _services;
    private MainViewModel? _main;
    private static readonly TimeSpan CleanupLimit = TimeSpan.FromSeconds(10);
    private bool _shuttingDown;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                _services = new AppServices(Paths);
                RemoteImage.Cache = _services.Images;
                var theme = new ThemeService(this);
                theme.Apply(_services.Launcher.Settings);
                _main = new MainViewModel(_services, theme);
                Dispatcher.UIThread.UnhandledException += OnUnhandledException;
                var window = new MainWindow { DataContext = _main };
                desktop.MainWindow = window;
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                desktop.ShutdownRequested += OnShutdownRequested;
                window.Opened += (_, _) => _ = _main.StartAsync();
                window.ExitRequested += () => Quit(desktop);
                _main.QuitRequested += () => Quit(desktop);
                if (Instance is not null)
                    Instance.ActivationRequested += () => Dispatcher.UIThread.Post(window.Reveal);
                TrayIconSetup.Setup(this, window, _services, theme, () => Quit(desktop));
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
