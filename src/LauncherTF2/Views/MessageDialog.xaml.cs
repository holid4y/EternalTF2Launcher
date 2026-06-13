using System.Windows;
using System.Windows.Media;

namespace LauncherTF2.Views;

public enum DialogType
{
    Error,
    Warn,
    Info,
    Success,
    Confirm,
}

public partial class MessageDialog : Window
{
    private static readonly Dictionary<DialogType, string> _iconPaths = new()
    {
        // X mark
        [DialogType.Error]   = "M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z",
        // Warning triangle
        [DialogType.Warn]    = "M12 2L1 21h22L12 2zm0 3.5L20.5 19h-17L12 5.5zm-1 5v5h2v-5h-2zm0 6v2h2v-2h-2z",
        // Info circle
        [DialogType.Info]    = "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15h-2v-6h2v6zm0-8h-2V7h2v2z",
        // Checkmark circle
        [DialogType.Success] = "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-2 15l-5-5 1.41-1.41L10 14.17l7.59-7.59L19 8l-9 9z",
        // Question mark circle
        [DialogType.Confirm] = "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 17h-2v-2h2v2zm2.07-7.75l-.9.92C13.45 12.9 13 13.5 13 15h-2v-.5c0-1.1.45-2.1 1.17-2.83l1.24-1.26c.37-.36.59-.86.59-1.41 0-1.1-.9-2-2-2s-2 .9-2 2H8c0-2.21 1.79-4 4-4s4 1.79 4 4c0 .88-.36 1.68-.93 2.25z",
    };

    private static readonly Color _errorIconColor   = Color.FromRgb(0xEF, 0x53, 0x50);
    private static readonly Color _warningIconColor = Color.FromRgb(0xFF, 0xB3, 0x00);

    public bool Confirmed { get; private set; }

    private MessageDialog(string title, string message, bool showCancel, string okLabel, DialogType type)
    {
        InitializeComponent();
        TitleText.Text    = title;
        MessageText.Text  = message;
        OkButton.Content  = okLabel;

        if (showCancel)
            CancelButton.Visibility = Visibility.Visible;

        if (_iconPaths.TryGetValue(type, out var pathData))
            IconPath.Data = Geometry.Parse(pathData);

        IconPath.Fill = type switch
        {
            DialogType.Error => new SolidColorBrush(_errorIconColor),
            DialogType.Warn  => new SolidColorBrush(_warningIconColor),
            _                => (Brush)FindResource("AccentBrush"),
        };

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
                DragMove();
        };
    }

    private static MessageDialog CreateOwned(string title, string message, bool showCancel, string okLabel, DialogType type)
    {
        var dialog = new MessageDialog(title, message, showCancel, okLabel, type);
        if (Application.Current?.MainWindow is { IsVisible: true } owner)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        return dialog;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        Confirmed    = true;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Confirmed    = false;
        DialogResult = false;
        Close();
    }

    // ── Public factory methods ──────────────────────────────────────────────

    public static void ShowError(string title, string message)
        => CreateOwned(title, message, false, "OK", DialogType.Error).ShowDialog();

    public static void ShowSuccess(string title, string message)
        => CreateOwned(title, message, false, "OK", DialogType.Success).ShowDialog();

    public static void ShowInfo(string title, string message)
        => CreateOwned(title, message, false, "OK", DialogType.Info).ShowDialog();

    public static bool ShowConfirm(string title, string message, string confirmLabel = "Confirm")
    {
        var dialog = CreateOwned(title, message, true, confirmLabel, DialogType.Confirm);
        dialog.ShowDialog();
        return dialog.Confirmed;
    }
}
