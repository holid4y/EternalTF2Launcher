using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Eternal.App.ViewModels;

namespace Eternal.App.Views;

public partial class MainWindow : Window
{
    // The size every page is laid out for; MinWidth and MinHeight match it.
    private const double LayoutWidth = 1040, LayoutHeight = 660;

    private bool _quitting;
    private IInputElement? _focusBeforeDialog;

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
            {
                vm.MinimizeRequested += () => WindowState = WindowState.Minimized;
                vm.PropertyChanged += OnViewModelChanged;
                vm.Theme.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(vm.Theme.Scale))
                        UpdateZoom();
                };
                UpdateZoom();
            }
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty)
            UpdateZoom();
    }

    // A larger interface size only applies as far as the window has room for it;
    // past that the pages would be cut off.
    private void UpdateZoom()
    {
        if (Vm is not { } vm || ClientSize.Width <= 0)
            return;
        var fits = Math.Min(ClientSize.Width / LayoutWidth, ClientSize.Height / LayoutHeight);
        var scale = Math.Max(0.6, Math.Min(vm.Theme.Scale, fits));
        if (Zoom.LayoutTransform is not ScaleTransform { ScaleX: var current } || Math.Abs(current - scale) > 0.001)
            Zoom.LayoutTransform = new ScaleTransform(scale, scale);
    }

    // Dialogs are drawn over the page, so focus has to be moved in and back by hand.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.Dialog))
            return;
        if (Vm?.Dialog is { } dialog)
        {
            _focusBeforeDialog ??= FocusManager?.GetFocusedElement();
            Dispatcher.UIThread.Post(() =>
            {
                if (dialog.HasInput)
                {
                    DialogInput.Focus();
                    DialogInput.SelectAll();
                }
                else
                {
                    DialogConfirm.Focus();
                }
            }, DispatcherPriority.Loaded);
        }
        else
        {
            _focusBeforeDialog?.Focus();
            _focusBeforeDialog = null;
        }
    }

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
