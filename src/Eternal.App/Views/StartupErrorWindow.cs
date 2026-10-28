using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Eternal.Core.Platform;

namespace Eternal.App.Views;

/// <summary>Shown instead of the launcher when something prevents it from starting.</summary>
internal static class StartupErrorWindow
{
    public static Window Create(Exception error, string logsDir)
    {
        var open = new Button { Content = "Open logs folder", HorizontalAlignment = HorizontalAlignment.Left };
        open.Click += (_, _) => Shell.OpenFolder(logsDir);
        return new Window
        {
            Title = "Eternal TF2 Launcher",
            Width = 560,
            Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = new SolidColorBrush(Color.Parse("#15110E")),
            Content = new StackPanel
            {
                Margin = new Thickness(28),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = "The launcher couldn't start", FontSize = 22, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse("#EEE4CC")) },
                    new TextBlock { Text = error.Message, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#B9AA90")) },
                    new TextBlock { Text = "Your settings and mods were not touched. The log folder has the details.", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#8E8170")) },
                    open,
                },
            },
        };
    }
}
