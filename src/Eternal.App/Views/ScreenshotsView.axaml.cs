using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Eternal.App.Services;
using Eternal.App.ViewModels;
using SkiaSharp;

namespace Eternal.App.Views;

public partial class ScreenshotsView : UserControl
{
    private static readonly IBrush Dim = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
    private static readonly IBrush Outline = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255));

    private ScreenshotsViewModel? _vm;
    private ScreenshotEditorViewModel? _editor;
    private Point? _dragStart;
    private Point _dragEnd;
    private int _moving = -1;
    private enum DragMode { None, Move, Resize }
    private DragMode _mode;
    private DragMode _armed;

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
        _moving = -1;
        _mode = DragMode.None;
        _armed = DragMode.None;
        if (_editor is not null)
            _editor.PropertyChanged += OnEditorChanged;
        DrawOverlay();
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ScreenshotEditorViewModel.Edit) or nameof(ScreenshotEditorViewModel.Preview) or nameof(ScreenshotEditorViewModel.Tool)
            or nameof(ScreenshotEditorViewModel.SelectedMark))
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
        if (_editor is null)
            return;
        var r = PictureRect();
        var p = e.GetPosition(Stage);
        var props = e.GetCurrentPoint(Stage).Properties;
        if (props.IsRightButtonPressed)
        {
            if (r.Contains(p) && _editor.HitMark(ToImage(p)) is var hit && hit >= 0)
            {
                _editor.SelectMark(hit);
                ShowMarkMenu(hit);
            }
            e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed || !r.Contains(p))
            return;
        if (_editor.Tool == EditTool.None)
        {
            var hit = _editor.HitMark(ToImage(p));
            if (hit < 0)
            {
                _editor.SelectMark(-1);
                return;
            }
            _editor.SelectMark(hit);
            _moving = hit;
            _mode = _armed != DragMode.None ? _armed
                : NearHandle(p, _editor.Edit.Marks[hit]) ? DragMode.Resize : DragMode.Move;
            _armed = DragMode.None;
        }
        else
        {
            _editor.SelectMark(-1);
            _armed = DragMode.None;
        }
        _dragStart = _dragEnd = p;
        e.Pointer.Capture(Stage);
        e.Handled = true;
    }

    private bool NearHandle(Point p, Mark m)
    {
        var r = PictureRect();
        var area = MarkBounds(m);
        var hx = r.X + area.Right * r.Width;
        var hy = r.Y + area.Bottom * r.Height;
        return Math.Abs(p.X - hx) <= 12 && Math.Abs(p.Y - hy) <= 12;
    }

    private SKRect MarkBounds(Mark m)
    {
        if (m.Kind == MarkKind.Text && m.Text is not null && _editor is { } editor)
        {
            var (w, h) = editor.ImageSize;
            return ScreenshotRenderer.TextBounds(m, w, h);
        }
        return m.Area;
    }

    private void ShowMarkMenu(int index)
    {
        if (_editor is null)
            return;
        var menu = new ContextMenu();
        void Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Item("Move", () => { _armed = DragMode.Move; _editor.SelectMark(index); });
        Item("Resize", () => { _armed = DragMode.Resize; _editor.SelectMark(index); });
        Item("Delete", () => _editor.DeleteMark(index));
        menu.PlacementTarget = Stage;
        menu.Open(Stage);
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
        var moving = _moving;
        var mode = _mode;
        _moving = -1;
        _mode = DragMode.None;
        _dragStart = null;
        e.Pointer.Capture(null);
        var a = ToImage(start);
        var b = ToImage(_dragEnd);
        if (moving >= 0)
        {
            var delta = new SKPoint(b.X - a.X, b.Y - a.Y);
            if (mode == DragMode.Resize)
                editor.ResizeMark(moving, delta);
            else
                editor.MoveMark(moving, delta);
        }
        else
        {
            var area = new SKRect(a.X, a.Y, b.X, b.Y);
            switch (editor.Tool)
            {
                case EditTool.Crop:
                    editor.SetCrop(area);
                    break;
                case EditTool.Text:
                    await editor.AddTextAsync(b);
                    break;
                case EditTool.Image:
                    await editor.AddImageAsync(b);
                    break;
                default:
                    editor.AddMark(area);
                    break;
            }
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
        {
            if (_editor is { HasSelectedMark: true } selected)
                DrawMarkFrame(r, selected.Edit.Marks[selected.SelectedMark], handle: true);
            return;
        }
        if (_moving >= 0 && _moving < _editor.Edit.Marks.Count)
        {
            var m = _editor.Edit.Marks[_moving];
            var delta = new SKPoint(ToImage(_dragEnd).X - ToImage(start).X, ToImage(_dragEnd).Y - ToImage(start).Y);
            if (_mode == DragMode.Resize && m.Kind == MarkKind.Text)
            {
                var scale = Math.Clamp(m.Scale + (delta.X + delta.Y) * 1.5f, 0.2f, 8f);
                DrawMarkFrame(r, m with { Scale = scale }, handle: false);
            }
            else
            {
                var area = _mode == DragMode.Resize
                    ? ScreenshotRenderer.Resize(m.Area, delta, m.Kind == MarkKind.Arrow)
                    : ScreenshotRenderer.Translate(m.Area, delta);
                DrawMarkFrame(r, m with { Area = area }, handle: false);
            }
            return;
        }
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

    private void DrawMarkFrame(Rect r, Mark m, bool handle)
    {
        if (m.Kind == MarkKind.Arrow)
        {
            Overlay.Children.Add(new Line
            {
                StartPoint = new Point(r.X + m.Area.Left * r.Width, r.Y + m.Area.Top * r.Height),
                EndPoint = new Point(r.X + m.Area.Right * r.Width, r.Y + m.Area.Bottom * r.Height),
                Stroke = Outline,
                StrokeThickness = 2,
            });
            return;
        }
        var area = MarkBounds(m);
        var box = new Rect(r.X + area.Left * r.Width, r.Y + area.Top * r.Height,
            Math.Abs(area.Width) * r.Width, Math.Abs(area.Height) * r.Height);
        Frame(box);
        if (handle)
        {
            const double hs = 9;
            var grip = new Rectangle { Width = hs, Height = hs, Fill = Outline, Stroke = new SolidColorBrush(Colors.Black), StrokeThickness = 1 };
            Canvas.SetLeft(grip, box.Right - hs / 2);
            Canvas.SetTop(grip, box.Bottom - hs / 2);
            Overlay.Children.Add(grip);
        }
    }
}
