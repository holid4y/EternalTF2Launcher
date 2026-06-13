using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LauncherTF2.Core;

// Frame-locked smooth scroll driven by CompositionTarget.Rendering.
// Progress is computed from wall-clock time (Stopwatch) so missed frames don't slow the animation.
public static class AnimatedScrollHelper
{
    private sealed class State
    {
        public double From;
        public double To;
        public double DurationMs;
        public Stopwatch Watch = Stopwatch.StartNew();
        public Action? OnCompleted;
    }

    private static readonly Dictionary<ScrollViewer, State> _running = new();
    private static bool _hooked;

    public static bool IsAnimating(ScrollViewer sv) => _running.ContainsKey(sv);

    // Cancels only the animation on this scroller; leaves others running.
    public static void StopAnimation(ScrollViewer sv)
    {
        if (_running.Remove(sv))
            Logger.LogDebug($"[ScrollAnimator] Animation cancelled on user input");
        UnhookIfIdle();
    }

    public static void StopAnimation()
    {
        _running.Clear();
        UnhookIfIdle();
    }

    // Latest call wins — a new call while animating replaces the in-flight state.
    public static void ScrollToElement(
        ScrollViewer scroller,
        FrameworkElement target,
        double durationMs = 380,
        Action? onCompleted = null)
    {
        if (scroller == null || target == null)
        {
            onCompleted?.Invoke();
            return;
        }

        try
        {
            if (scroller.Content is not Visual content)
            {
                onCompleted?.Invoke();
                return;
            }

            // Force layout before sampling position — freshly-inflated DataTemplates can report stale Y.
            scroller.UpdateLayout();

            var pos  = target.TransformToVisual(content).Transform(new Point(0, 0));
            double from = scroller.VerticalOffset;
            double to   = Math.Max(0, Math.Min(pos.Y, scroller.ScrollableHeight));

            Logger.LogDebug($"[ScrollAnimator] ScrollToElement from={from:F0} to={to:F0} (anchor Y={pos.Y:F0})");

            if (Math.Abs(from - to) < 1.0)
            {
                Logger.LogDebug("[ScrollAnimator] Skipped — already at target");
                onCompleted?.Invoke();
                return;
            }

            // Take ownership of the offset: kill the wheel-smoothing loop and
            // re-baseline its target, or it lerps the view back mid-animation.
            SmoothScrollBehavior.SyncToCurrent(scroller);

            _running[scroller] = new State
            {
                From        = from,
                To          = to,
                DurationMs  = durationMs,
                OnCompleted = onCompleted,
            };

            Hook();
        }
        catch (Exception ex)
        {
            Logger.LogDebug($"[ScrollAnimator] ScrollToElement failed: {ex.Message}");
            onCompleted?.Invoke();
        }
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private static void Hook()
    {
        if (_hooked) return;
        CompositionTarget.Rendering += OnRender;
        _hooked = true;
    }

    private static void UnhookIfIdle()
    {
        if (_hooked && _running.Count == 0)
        {
            CompositionTarget.Rendering -= OnRender;
            _hooked = false;
        }
    }

    private static void OnRender(object? sender, EventArgs e)
    {
        if (_running.Count == 0)
        {
            UnhookIfIdle();
            return;
        }

        // Snapshot so a re-entrant ScrollToElement inside OnCompleted can mutate _running safely.
        foreach (var (sv, state) in _running.ToArray())
        {
            try
            {
                double t      = Math.Clamp(state.Watch.Elapsed.TotalMilliseconds / state.DurationMs, 0.0, 1.0);
                double eased  = 1.0 - Math.Pow(1.0 - t, 3.0); // ease-out cubic
                double offset = state.From + (state.To - state.From) * eased;

                sv.ScrollToVerticalOffset(offset);

                if (t >= 1.0)
                {
                    // Guard against a re-entrant call having replaced our state entry.
                    if (_running.TryGetValue(sv, out var current) && ReferenceEquals(current, state))
                    {
                        sv.ScrollToVerticalOffset(state.To);
                        _running.Remove(sv);

                        // Hand the offset back to the wheel-smoothing behavior at
                        // the final position so its next tick starts from here.
                        SmoothScrollBehavior.SyncToCurrent(sv);

                        Logger.LogDebug($"[ScrollAnimator] Animation complete (target={state.To:F0})");
                        state.OnCompleted?.Invoke();
                    }
                }
            }
            catch (Exception ex)
            {
                // Never let a bad scroller pin the offset forever — drop it.
                _running.Remove(sv);
                Logger.LogDebug($"[ScrollAnimator] Frame failed, dropping animation: {ex.Message}");
            }
        }

        UnhookIfIdle();
    }
}
