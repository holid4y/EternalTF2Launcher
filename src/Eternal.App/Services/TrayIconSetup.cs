using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Eternal.App.Theming;
using Eternal.App.Views;
using Eternal.Core.Logging;

namespace Eternal.App.Services;

/// <summary>
/// Optional tray icon. Linux desktops without a status-notifier host
/// show nothing; starting the launcher again always brings the window back.
/// </summary>
internal static class TrayIconSetup
{
    public static void Setup(Application app, MainWindow window, AppServices services, ThemeService theme, Action quit)
    {
        try
        {
            var show = new NativeMenuItem("Show Eternal");
            show.Click += (_, _) => window.Reveal();
            var exit = new NativeMenuItem("Quit");
            exit.Click += (_, _) => quit();

            var tray = new TrayIcon
            {
                Icon = theme.Icon ?? new WindowIcon(AssetLoader.Open(new Uri("avares://EternalLauncher/Assets/Logos/app.ico"))),
                ToolTipText = "Eternal TF2 Launcher",
                Menu = new NativeMenu { Items = { show, new NativeMenuItemSeparator(), exit } },
                IsVisible = services.Launcher.Settings.CloseToTray,
            };
            tray.Clicked += (_, _) => window.Reveal();
            theme.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ThemeService.Icon) && theme.Icon is { } icon)
                    tray.Icon = icon;
            };
            // Settings can change from background work (start-up migration); the tray is a UI object.
            services.Launcher.Changed += () => Dispatcher.UIThread.Post(() => tray.IsVisible = services.Launcher.Settings.CloseToTray);
            TrayIcon.SetIcons(app, [tray]);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException or IOException)
        {
            Log.Info($"No tray icon on this desktop: {ex.Message}");
        }
    }
}
