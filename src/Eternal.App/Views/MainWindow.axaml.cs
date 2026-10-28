using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Eternal.App.ViewModels;

namespace Eternal.App.Views;

public partial class MainWindow : Window
{
    private bool _quitting;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, OnPreviewWheel, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                vm.MinimizeRequested += () => WindowState = WindowState.Minimized;
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>Fired when closing the window should end the app.</summary>
    public event Action? ExitRequested;

    public void Reveal()
    {
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_quitting)
            return;
        var closeToTray = Vm?.Launcher.CloseToTray == true;
        e.Cancel = true;
        if (closeToTray)
        {
            Hide();
            return;
        }
        _quitting = true;
        ExitRequested?.Invoke();
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            DropOverlay.IsVisible = true;
            e.DragEffects = DragDropEffects.Copy;
        }
    }

    private void OnDragLeave(object? sender, RoutedEventArgs e) => DropOverlay.IsVisible = false;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
        var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray() ?? [];
        if (paths.Length == 0 || Vm is not { } vm)
            return;
        vm.Navigate("mods");
        await vm.Mods.ImportAsync(paths);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm?.Config.HandleCapture(e.Key) == true)
            e.Handled = true;
    }

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        var button = point.Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonPressed => MouseButton.Left,
            PointerUpdateKind.RightButtonPressed => MouseButton.Right,
            PointerUpdateKind.MiddleButtonPressed => MouseButton.Middle,
            PointerUpdateKind.XButton1Pressed => MouseButton.XButton1,
            PointerUpdateKind.XButton2Pressed => MouseButton.XButton2,
            _ => MouseButton.None,
        };
        // The click that starts capturing lands on the key button itself; ignore it.
        if (button != MouseButton.None && e.Source is not Button && Vm?.Config.HandleCapture(Key.None, button) == true)
            e.Handled = true;
    }

    private void OnPreviewWheel(object? sender, PointerWheelEventArgs e)
    {
        if (Vm?.Config.HandleCapture(Key.None, null, Math.Sign(e.Delta.Y)) == true)
            e.Handled = true;
    }
}
