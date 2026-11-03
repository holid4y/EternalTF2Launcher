using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Eternal.App.ViewModels;
using SkiaSharp;

namespace Eternal.App.Views;

/// <summary>
/// The gallery loads pictures as cards scroll into view; the editor turns
/// pointer drags on the picture into crops and marks. Shapes being dragged are
/// drawn on top as plain outlines and only rendered into the picture once let go.
/// </summary>
public partial class ScreenshotsView : UserControl
{
    private static readonly IBrush Dim = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
    private static readonly IBrush Outline = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255));

    private ScreenshotsViewModel? _vm;
    private ScreenshotEditorViewModel? _editor;
    private Point? _dragStart;
    private Point _dragEnd;

    public ScreenshotsView()
    {
        InitializeComponent();
        foreach (var repeater in new[] { ShotGrid, ShotList })
        {
            repeater.ElementPrepared += (_, e) => (e.Element.DataContext as ShotItem)?.LoadThumbnailAsync();
            repeater.AddHandler(PointerReleasedEvent, OnShotReleased, RoutingStrategies.Bubble);
            repeater.AddHandler(KeyDownEvent, OnShotKey);
            repeater.DoubleTapped += (_, e) =>
            {
                if (ShotAt(e.Source) is { } item && _vm is not null)
                {
                    _vm.Selected = item;
                    _vm.EditCommand.Execute(null);
                }
            };
        }

        Stage.PointerPressed += OnStagePressed;
        Stage.PointerMoved += OnStageMoved;
        Stage.PointerReleased += OnStageReleased;
        Stage.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
                DrawOverlay();
        };
        foreach (var slider in new[] { BrightnessSlider, ContrastSlider, SaturationSlider })
            slider.AddHandler(PointerCaptureLostEvent, (_, _) => _editor?.EndSliderChange(), RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
            _vm.PropertyChanged -= OnPageChanged;
        _vm = DataContext as ScreenshotsViewModel;
        if (_vm is not null)
            _vm.PropertyChanged += OnPageChanged;
        AttachEditor(_vm?.Editor);
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScreenshotsViewModel.Editor))
            AttachEditor(_vm?.Editor);
    }

    private void AttachEditor(ScreenshotEditorViewModel? editor)
    {
        if (_editor is not null)
            _editor.PropertyChanged -= OnEditorChanged;
        _editor = editor;
        _dragStart = null;
        if (_editor is not null)
            _editor.PropertyChanged += OnEditorChanged;
        DrawOverlay();
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ScreenshotEditorViewModel.Edit) or nameof(ScreenshotEditorViewModel.Preview) or nameof(ScreenshotEditorViewModel.Tool))
            DrawOverlay();
    }

    private static ShotItem? ShotAt(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("shot"))?.DataContext as ShotItem;

    private void OnShotReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left && ShotAt(e.Source) is { } item && _vm is not null)
            _vm.Selected = item;
    }

    private void OnShotKey(object? sender, KeyEventArgs e)
    {
        if (ShotAt(e.Source) is not { } item || _vm is null)
            return;
        if (e.Key is Key.Enter or Key.Space)
        {
            _vm.Selected = item;
            e.Handled = true;
        }
    }

    /// <summary>Where the picture actually sits inside the stage, since it is scaled to fit and centred.</summary>
    private Rect PictureRect()
    {
        if (_editor?.Preview is not { } preview || Stage.Bounds.Width <= 0 || Stage.Bounds.Height <= 0)
            return default;
        var size = preview.Size;
        var scale = Math.Min(Stage.Bounds.Width / size.Width, Stage.Bounds.Height / size.Height);
        var w = size.Width * scale;
        var h = size.Height * scale;
        return new Rect((Stage.Bounds.Width - w) / 2, (Stage.Bounds.Height - h) / 2, w, h);
    }

    private SKPoint ToImage(Point p)
    {
        var r = PictureRect();
        return r.Width <= 0 ? default : new SKPoint((float)((p.X - r.X) / r.Width), (float)((p.Y - r.Y) / r.Height));
    }

    private void OnStagePressed(object? sender, PointerPressedEventArgs e)
    {
        if (_editor is null || _editor.Tool == EditTool.None || !e.GetCurrentPoint(Stage).Properties.IsLeftButtonPressed)
            return;
        var r = PictureRect();
        var p = e.GetPosition(Stage);
        if (!r.Contains(p))
            return;
        _dragStart = _dragEnd = p;
        e.Pointer.Capture(Stage);
        e.Handled = true;
    }

    private void OnStageMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is null)
            return;
        var r = PictureRect();
        var p = e.GetPosition(Stage);
        _dragEnd = new Point(Math.Clamp(p.X, r.Left, r.Right), Math.Clamp(p.Y, r.Top, r.Bottom));
        if (_editor is { Tool: EditTool.Crop, Ratio.Ratio: { } ratio })
            _dragEnd = KeepRatio(_dragStart.Value, _dragEnd, ratio, r);
        DrawOverlay();
    }

    private async void OnStageReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragStart is not { } start || _editor is not { } editor)
            return;
        _dragStart = null;
        e.Pointer.Capture(null);
        var a = ToImage(start);
        var b = ToImage(_dragEnd);
        var area = new SKRect(a.X, a.Y, b.X, b.Y);
        switch (editor.Tool)
        {
            case EditTool.Crop:
                editor.SetCrop(area);
                break;
            case EditTool.Text:
                await editor.AddTextAsync(b);
                break;
            default:
                editor.AddMark(area);
                break;
        }
        DrawOverlay();
    }

    private static Point KeepRatio(Point start, Point end, double ratio, Rect bounds)
    {
        var w = end.X - start.X;
        var h = end.Y - start.Y;
        if (Math.Abs(w) / ratio > Math.Abs(h))
            h = Math.Sign(h == 0 ? 1 : h) * Math.Abs(w) / ratio;
        else
            w = Math.Sign(w == 0 ? 1 : w) * Math.Abs(h) * ratio;
        var x = Math.Clamp(start.X + w, bounds.Left, bounds.Right);
        var y = Math.Clamp(start.Y + h, bounds.Top, bounds.Bottom);
        // Hitting an edge shortens one side; shorten the other to match.
        var fitW = Math.Min(Math.Abs(x - start.X), Math.Abs(y - start.Y) * ratio);
        return new Point(start.X + Math.Sign(w) * fitW, start.Y + Math.Sign(h) * fitW / ratio);
    }

    private void DrawOverlay()
    {
        Overlay.Children.Clear();
        if (_editor is null)
            return;
        var r = PictureRect();
        if (r.Width <= 0)
            return;

        if (_editor.Edit.Crop is { } crop && !(_dragStart is not null && _editor.Tool == EditTool.Crop))
        {
            var c = new Rect(r.X + crop.Left * r.Width, r.Y + crop.Top * r.Height, crop.Width * r.Width, crop.Height * r.Height);
            Shade(r, c);
        }

        if (_dragStart is not { } start)
            return;
        if (_editor.Tool == EditTool.Arrow)
        {
            Overlay.Children.Add(new Line { StartPoint = start, EndPoint = _dragEnd, Stroke = Outline, StrokeThickness = 2 });
            return;
        }
        var box = new Rect(start, _dragEnd).Normalize();
        if (_editor.Tool == EditTool.Crop)
            Shade(r, box);
        else
            Frame(box);
    }

    private void Shade(Rect picture, Rect keep)
    {
        void Fill(double x, double y, double w, double h)
        {
            if (w <= 0 || h <= 0)
                return;
            var shade = new Rectangle { Width = w, Height = h, Fill = Dim };
            Canvas.SetLeft(shade, x);
            Canvas.SetTop(shade, y);
            Overlay.Children.Add(shade);
        }
        Fill(picture.X, picture.Y, picture.Width, keep.Top - picture.Y);
        Fill(picture.X, keep.Bottom, picture.Width, picture.Bottom - keep.Bottom);
        Fill(picture.X, keep.Top, keep.Left - picture.X, keep.Height);
        Fill(keep.Right, keep.Top, picture.Right - keep.Right, keep.Height);
        Frame(keep);
    }

    private void Frame(Rect area)
    {
        var frame = new Rectangle { Width = area.Width, Height = area.Height, Stroke = Outline, StrokeThickness = 1.5, StrokeDashArray = [4, 3] };
        Canvas.SetLeft(frame, area.X);
        Canvas.SetTop(frame, area.Y);
        Overlay.Children.Add(frame);
    }
}
