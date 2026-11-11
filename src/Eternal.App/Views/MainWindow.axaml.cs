using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Eternal.App.ViewModels;

namespace Eternal.App.Views;

public partial class MainWindow : Window
{
    // The size every page is laid out for; MinWidth and MinHeight match it.
    private const double LayoutWidth = 1040, LayoutHeight = 660;

    private static readonly Animation StageLeaves = Move(140, new CubicEaseIn(), TranslateTransform.YProperty, (1, 0), (0, 6));
    private static readonly Animation StageArrives = Move(260, new CubicEaseOut(), TranslateTransform.YProperty, (0, 10), (1, 0));
    private static readonly Animation IdentityArrives = Move(300, new CubicEaseOut(), TranslateTransform.XProperty, (0, -10), (1, 0), delay: 70);
    private static readonly Animation MenuOpens = Move(150, new CubicEaseOut(), TranslateTransform.YProperty, (0, 8), (1, 0));

    private bool _quitting;
    private bool _followsTheme;
    private MainViewModel? _vm;
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
            // The window outlives every game switch, so the previous view model's handlers come off.
            if (_vm is not null)
            {
                _vm.MinimizeRequested -= OnMinimizeRequested;
                _vm.PropertyChanged -= OnViewModelChanged;
            }
            if (DataContext is MainViewModel vm)
            {
                _vm = vm;
                vm.MinimizeRequested += OnMinimizeRequested;
                vm.PropertyChanged += OnViewModelChanged;
                if (!_followsTheme)
                {
                    _followsTheme = true;
                    vm.Theme.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName == nameof(vm.Theme.Scale))
                            UpdateZoom();
                    };
                }
                UpdateZoom();
            }
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnMinimizeRequested() => WindowState = WindowState.Minimized;

    private static Animation Move(int milliseconds, Easing easing, AvaloniaProperty offset, (double Opacity, double Offset) from,
        (double Opacity, double Offset) to, int delay = 0) => new()
    {
        Duration = TimeSpan.FromMilliseconds(milliseconds),
        Delay = TimeSpan.FromMilliseconds(delay),
        Easing = easing,
        FillMode = FillMode.Both,
        Children =
        {
            new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, from.Opacity), new Setter(offset, from.Offset) } },
            new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, to.Opacity), new Setter(offset, to.Offset) } },
        },
    };

    /// <summary>The page sinks and fades out; the game is swapped while nothing shows.</summary>
    public Task LeaveGameAsync() => StageLeaves.RunAsync(Stage);

    /// <summary>The new game's page rises into place, and its name and logo slide in just after.</summary>
    public Task EnterGameAsync() => Task.WhenAll(StageArrives.RunAsync(Stage), IdentityArrives.RunAsync(GameIdentity));

    private void OnGamesDismissed(object? sender, PointerPressedEventArgs e)
    {
        Vm?.GamesOpen = false;
        e.Handled = true;
    }

    private void FocusCurrentGame() =>
        GameMenu.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.DataContext is GameChoiceViewModel { IsCurrent: true })?.Focus();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty)
        {
            FollowForcedSize();
            UpdateZoom();
        }
    }

    // A tiling compositor can give the window less than its minimum size. Avalonia would
    // still measure the page at the minimum, wider than the room it then gets, and the
    // scaled page would sit at its own size in the middle of the window instead of filling it.
    private void FollowForcedSize()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
            return;
        MinWidth = Math.Min(LayoutWidth, ClientSize.Width);
        MinHeight = Math.Min(LayoutHeight, ClientSize.Height);
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
        if (e.PropertyName == nameof(MainViewModel.GamesOpen))
        {
            if (Vm?.GamesOpen == true)
            {
                _ = MenuOpens.RunAsync(GameMenu);
                Dispatcher.UIThread.Post(FocusCurrentGame, DispatcherPriority.Loaded);
            }
            else if (GameMenu.IsKeyboardFocusWithin)
            {
                GameSwitch.Focus();
            }
            return;
        }
        if (e.PropertyName == nameof(MainViewModel.ImportChoice))
        {
            if (Vm?.ImportChoice is not null)
            {
                _focusBeforeDialog ??= FocusManager?.GetFocusedElement();
                Dispatcher.UIThread.Post(() => ChoiceInstall.Focus(), DispatcherPriority.Loaded);
            }
            else if (Vm?.Dialog is null)
            {
                _focusBeforeDialog?.Focus();
                _focusBeforeDialog = null;
            }
            return;
        }
        if (e.PropertyName == nameof(MainViewModel.FolderReview))
        {
            if (Vm?.FolderReview is not null)
                Dispatcher.UIThread.Post(() => ReviewKeep.Focus(), DispatcherPriority.Loaded);
            return;
        }
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
        if (vm.SelectedTab?.Id == "config" && vm.Config.ShowsSounds)
        {
            await vm.Config.Sounds.AddAsync(paths);
            return;
        }
        vm.Navigate("mods");
        vm.Mods.Browsing = false;
        await vm.Mods.ImportAsync(paths);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Vm is { GamesOpen: true } vm)
        {
            vm.GamesOpen = false;
            e.Handled = true;
        }
        else if (Vm?.Config.HandleCapture(e.Key) == true)
        {
            e.Handled = true;
        }
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
