using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace LauncherTF2.Core;

/// <summary>
/// Adds smooth, animated mouse-wheel scrolling to a <see cref="ScrollViewer"/>.
/// Supports vertical scrolling by default and horizontal scrolling when Shift is held.
/// </summary>
public static class SmoothScrollBehavior
{
    private const double ScrollStep = 72.0;
    private const double LerpFactor = 0.28;
    private const double SnapThreshold = 0.5;
    private static readonly object RenderLock = new();
    private static readonly Dictionary<ScrollViewer, EventHandler> RenderHandlers = new();
    private static readonly Dictionary<ScrollViewer, ScrollBar?> CachedVerticalScrollBars = new();

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(SmoothScrollBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty AnimatedVerticalOffsetProperty =
        DependencyProperty.RegisterAttached(
            "AnimatedVerticalOffset",
            typeof(double),
            typeof(SmoothScrollBehavior),
            new PropertyMetadata(0.0));

    private static readonly DependencyProperty AnimatedHorizontalOffsetProperty =
        DependencyProperty.RegisterAttached(
            "AnimatedHorizontalOffset",
            typeof(double),
            typeof(SmoothScrollBehavior),
            new PropertyMetadata(0.0));

    private static readonly DependencyProperty IsRenderingProperty =
        DependencyProperty.RegisterAttached(
            "IsRendering",
            typeof(bool),
            typeof(SmoothScrollBehavior),
            new PropertyMetadata(false));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private static double GetAnimatedVerticalOffset(DependencyObject obj) => (double)obj.GetValue(AnimatedVerticalOffsetProperty);

    private static void SetAnimatedVerticalOffset(DependencyObject obj, double value) => obj.SetValue(AnimatedVerticalOffsetProperty, value);

    private static double GetAnimatedHorizontalOffset(DependencyObject obj) => (double)obj.GetValue(AnimatedHorizontalOffsetProperty);

    private static void SetAnimatedHorizontalOffset(DependencyObject obj, double value) => obj.SetValue(AnimatedHorizontalOffsetProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer scrollViewer)
            return;

        if ((bool)e.NewValue)
        {
            scrollViewer.Loaded += ScrollViewer_Loaded;
            scrollViewer.Unloaded += ScrollViewer_Unloaded;
            scrollViewer.PreviewMouseWheel += ScrollViewer_PreviewMouseWheel;
        }
        else
        {
            scrollViewer.Loaded -= ScrollViewer_Loaded;
            scrollViewer.Unloaded -= ScrollViewer_Unloaded;
            scrollViewer.PreviewMouseWheel -= ScrollViewer_PreviewMouseWheel;
        }
    }

    private static void ScrollViewer_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer scrollViewer)
        {
            scrollViewer.SetCurrentValue(AnimatedVerticalOffsetProperty, scrollViewer.VerticalOffset);
            scrollViewer.SetCurrentValue(AnimatedHorizontalOffsetProperty, scrollViewer.HorizontalOffset);
        }
    }

    private static void ScrollViewer_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer scrollViewer && GetIsEnabled(scrollViewer))
        {
            scrollViewer.Loaded -= ScrollViewer_Loaded;
            scrollViewer.Unloaded -= ScrollViewer_Unloaded;
            scrollViewer.PreviewMouseWheel -= ScrollViewer_PreviewMouseWheel;
            StopRendering(scrollViewer);
        }
    }

    /// <summary>
    /// Re-baselines the wheel-smoothing targets to the scroller's live offsets
    /// and stops the smoothing loop. <see cref="AnimatedScrollHelper"/> calls
    /// this before driving the offset itself — otherwise a stale wheel target
    /// would keep lerping the view back, fighting the category-jump animation
    /// frame by frame (the "settings scrolling gets stuck" bug).
    /// </summary>
    public static void SyncToCurrent(ScrollViewer scrollViewer)
    {
        scrollViewer.SetCurrentValue(AnimatedVerticalOffsetProperty, scrollViewer.VerticalOffset);
        scrollViewer.SetCurrentValue(AnimatedHorizontalOffsetProperty, scrollViewer.HorizontalOffset);
        StopRendering(scrollViewer);
    }

    private static void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer || e.Handled)
            return;

        var delta = e.Delta > 0 ? ScrollStep : -ScrollStep;

        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            var target = Clamp(scrollViewer.HorizontalOffset - delta, 0, scrollViewer.ScrollableWidth);
            scrollViewer.SetCurrentValue(AnimatedHorizontalOffsetProperty, target);
        }
        else
        {
            var target = Clamp(scrollViewer.VerticalOffset - delta, 0, scrollViewer.ScrollableHeight);
            scrollViewer.SetCurrentValue(AnimatedVerticalOffsetProperty, target);
        }

        EnsureRendering(scrollViewer);

        e.Handled = true;
    }

    private static void OnAnimatedVerticalOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer scrollViewer && e.NewValue is double value)
            scrollViewer.ScrollToVerticalOffset(value);
    }

    private static void OnAnimatedHorizontalOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer scrollViewer && e.NewValue is double value)
            scrollViewer.ScrollToHorizontalOffset(value);
    }

    private static void EnsureRendering(ScrollViewer scrollViewer)
    {
        if ((bool)scrollViewer.GetValue(IsRenderingProperty))
            return;

        scrollViewer.SetValue(IsRenderingProperty, true);

        EventHandler handler = null!;
        handler = (_, _) => RenderScroll(scrollViewer, handler);

        lock (RenderLock)
        {
            RenderHandlers[scrollViewer] = handler;
        }

        CompositionTarget.Rendering += handler;
    }

    private static void RenderScroll(ScrollViewer scrollViewer, EventHandler handler)
    {
        if (!scrollViewer.IsLoaded)
        {
            StopRendering(scrollViewer, handler);
            return;
        }

        // If the user is interacting directly with the scrollbar (dragging the thumb)
        // or the ScrollViewer has captured the mouse, stop the smooth animation so
        // we don't fight the user's direct input and cause the scrollbar to snap.
        if (scrollViewer.IsMouseCaptureWithin || IsVerticalScrollBarBeingDragged(scrollViewer))
        {
            StopRendering(scrollViewer, handler);
            return;
        }

        // A category-jump animation owns the offset right now — defer to it.
        if (AnimatedScrollHelper.IsAnimating(scrollViewer))
        {
            StopRendering(scrollViewer, handler);
            return;
        }

        var currentVertical = scrollViewer.VerticalOffset;
        var targetVertical = GetAnimatedVerticalOffset(scrollViewer);
        var nextVertical = Approach(currentVertical, targetVertical);

        if (Math.Abs(nextVertical - currentVertical) >= SnapThreshold)
            scrollViewer.ScrollToVerticalOffset(nextVertical);

        var currentHorizontal = scrollViewer.HorizontalOffset;
        var targetHorizontal = GetAnimatedHorizontalOffset(scrollViewer);
        var nextHorizontal = Approach(currentHorizontal, targetHorizontal);

        if (Math.Abs(nextHorizontal - currentHorizontal) >= SnapThreshold)
            scrollViewer.ScrollToHorizontalOffset(nextHorizontal);

        if (Math.Abs(nextVertical - targetVertical) < SnapThreshold &&
            Math.Abs(nextHorizontal - targetHorizontal) < SnapThreshold)
        {
            scrollViewer.ScrollToVerticalOffset(targetVertical);
            scrollViewer.ScrollToHorizontalOffset(targetHorizontal);
            StopRendering(scrollViewer, handler);
        }
    }

    private static bool IsVerticalScrollBarBeingDragged(ScrollViewer scrollViewer)
    {
        if (!CachedVerticalScrollBars.TryGetValue(scrollViewer, out var cached))
        {
            var found = FindVisualChild<ScrollBar>(scrollViewer, sb => sb.Orientation == Orientation.Vertical);
            CachedVerticalScrollBars[scrollViewer] = found;
            cached = found;
        }

        if (cached is null)
            return false;

        return cached.IsMouseCaptureWithin;
    }

    private static T? FindVisualChild<T>(DependencyObject parent, Func<T, bool>? predicate = null) where T : DependencyObject
    {
        if (parent == null)
            return default;

        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t && (predicate == null || predicate(t)))
                return t;

            var found = FindVisualChild(child, predicate);
            if (found != null)
                return found;
        }

        return default;
    }

    private static void StopRendering(ScrollViewer scrollViewer, EventHandler? handler = null)
    {
        lock (RenderLock)
        {
            if (!RenderHandlers.TryGetValue(scrollViewer, out var existingHandler))
                existingHandler = handler;

            if (existingHandler != null)
                CompositionTarget.Rendering -= existingHandler;

            RenderHandlers.Remove(scrollViewer);
        }

        scrollViewer.SetValue(IsRenderingProperty, false);
    }

    private static double Approach(double current, double target)
        => current + ((target - current) * LerpFactor);

    private static double Clamp(double value, double min, double max)
        => value < min ? min : value > max ? max : value;
}