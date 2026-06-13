using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LauncherTF2.Core;
using LauncherTF2.Models.Settings;
using LauncherTF2.ViewModels;

namespace LauncherTF2.Views;

public partial class SettingsView : UserControl
{
    private SettingsViewModel? _vm;

    public SettingsView()
    {
        InitializeComponent();
        KeyDown += SettingsView_KeyDown;
        PreviewMouseDown += SettingsView_PreviewMouseDown;
        DataContextChanged += OnDataContextChanged;

        // Cancel any in-flight auto-scroll the moment the user touches the
        // scroll wheel or taps/clicks inside the content scroller. Without this,
        // the animation fights the user's input: every render frame writes a new
        // offset, overriding what the wheel/touch/scrollbar just moved.
        //
        // The wheel handler MUST be registered with handledEventsToo: the
        // ScrollViewer's SmoothScrollBehavior (ModernScrollViewerStyle) marks
        // PreviewMouseWheel as handled, and it subscribed first — a plain `+=`
        // here would never fire, leaving the category-jump animation fighting
        // the wheel (the "scrolling gets stuck" bug).
        ContentScroller.AddHandler(PreviewMouseWheelEvent,
            new MouseWheelEventHandler((_, _) => AnimatedScrollHelper.StopAnimation(ContentScroller)),
            handledEventsToo: true);
        ContentScroller.PreviewMouseDown  += ContentScroller_UserScrollIntent;
        // Touch needs a separate lambda — EventHandler<TouchEventArgs> has a
        // nullable sender that doesn't match the shared RoutedEventArgs handler.
        ContentScroller.PreviewTouchDown  += (_, _) => AnimatedScrollHelper.StopAnimation(ContentScroller);
    }

    // ── Cancel auto-scroll on user input ─────────────────────────────────────

    private void ContentScroller_UserScrollIntent(object sender, RoutedEventArgs e)
    {
        // Only cancel when actually animating — avoids the log noise of stopping
        // a non-existent animation on every normal click inside the settings panel.
        AnimatedScrollHelper.StopAnimation(ContentScroller);
    }

    // ── Sidebar scroll requests ───────────────────────────────────────────────

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null) _vm.ScrollToCategoryRequested -= OnScrollRequested;
        _vm = DataContext as SettingsViewModel;
        if (_vm != null) _vm.ScrollToCategoryRequested += OnScrollRequested;
    }

    private void OnScrollRequested(object? sender, string anchorId)
    {
        var target = ScrollAnchor.Find(ContentScroller, anchorId);
        if (target == null)
        {
            Logger.LogDebug($"[ScrollAnimator] Anchor '{anchorId}' not found — cannot scroll");
            return;
        }
        AnimatedScrollHelper.ScrollToElement(ContentScroller, target);
    }

    // ── Active-section tracking ───────────────────────────────────────────────

    private void ContentScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_vm == null) return;

        // Skip the active-section recompute while AnimatedScrollHelper is
        // driving the offset — every animation frame fires ScrollChanged, and
        // recomputing would strobe the sidebar highlight across every section we
        // cross during the animation.
        //
        // The single ScrollChanged that fires after the final ScrollToVerticalOffset 
        // IS useful — it syncs the highlight to the destination section. 
        // IsAnimating is already false by then so we let it through naturally.
        if (AnimatedScrollHelper.IsAnimating(ContentScroller)) return;

        var top = ContentScroller.VerticalOffset;
        const double viewportLead = 80;
        string? bestId = null;
        double bestY = double.NegativeInfinity;

        foreach (var (id, fe) in ScrollAnchor.All(ContentScroller))
        {
            try
            {
                var content = ContentScroller.Content as Visual;
                if (content == null) continue;
                var y = fe.TransformToVisual(content).Transform(new Point(0, 0)).Y;
                if (y <= top + viewportLead && y > bestY) { bestY = y; bestId = id; }
            }
            catch { }
        }

        if (!string.IsNullOrEmpty(bestId))
            _vm.SyncActiveFromScroll(bestId);
    }

    // ── Key / mouse passthrough ───────────────────────────────────────────────

    private void SettingsView_KeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.HandleKeyPress(e.Key);
    }

    private void SettingsView_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            if (vm.HandleMousePress(e.ChangedButton)) e.Handled = true;
        }
    }
}
