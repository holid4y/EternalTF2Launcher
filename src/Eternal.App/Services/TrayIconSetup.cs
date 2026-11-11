using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Eternal.App.Theming;
using Eternal.Core;
using Eternal.Core.Logging;

namespace Eternal.App.Services;

/// <summary>
/// Optional tray icon. Linux desktops without a status-notifier host
/// show nothing; starting the launcher again always brings the window back.
/// </summary>
internal static class TrayIconSetup
{
    public static TrayIcon? Setup(Application app, Action reveal, ThemeService theme, Action quit)
    {
        try
        {
            var show = new NativeMenuItem("Show Eternal");
            show.Click += (_, _) => reveal();
            var exit = new NativeMenuItem("Quit");
            exit.Click += (_, _) => quit();

            var tray = new TrayIcon
            {
                Icon = theme.Icon ?? new WindowIcon(AssetLoader.Open(new Uri("avares://EternalLauncher/Assets/Logos/app.ico"))),
                ToolTipText = "Eternal TF2 Launcher",
                Menu = new NativeMenu { Items = { show, new NativeMenuItemSeparator(), exit } },
                IsVisible = false,
            };
            tray.Clicked += (_, _) => reveal();
            theme.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ThemeService.Icon) && theme.Icon is { } icon)
                    tray.Icon = icon;
            };
            TrayIcon.SetIcons(app, [tray]);
            return tray;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException or IOException)
        {
            Log.Info($"No tray icon on this desktop: {ex.Message}");
            return null;
        }
    }

    /// <summary>Each game's context loads the launcher settings anew, so the tray follows whichever is current. Dispose the previous follow before following another store.</summary>
    public static IDisposable Follow(TrayIcon? tray, LauncherSettingsStore launcher)
    {
        if (tray is null)
            return new Unsubscriber(null);
        tray.IsVisible = launcher.Settings.CloseToTray;
        // Settings can change from background work (start-up migration); the tray is a UI object.
        void Handler() => Dispatcher.UIThread.Post(() => tray.IsVisible = launcher.Settings.CloseToTray);
        launcher.Changed += Handler;
        return new Unsubscriber(() => launcher.Changed -= Handler);
    }

    private sealed class Unsubscriber(Action? onDispose) : IDisposable
    {
        public void Dispose() => onDispose?.Invoke();
    }
}
