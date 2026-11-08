using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using System.Windows.Input;
using Eternal.App.Services;
using Eternal.App.ViewModels;
using SkiaSharp;

namespace Eternal.App.Views;

public partial class ScreenshotsView : UserControl
{
    private static readonly IBrush Dim = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
    private static readonly IBrush Outline = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255));
    private static readonly IBrush Grip = new SolidColorBrush(Colors.Black);
    private const double KnobSize = 10;
    private const double HandleGrab = 10;
    private const double HitTolerance = 6;

    private ScreenshotsViewModel? _vm;
    private ScreenshotEditorViewModel? _editor;
    private Point? _dragStart;
    private Point _dragEnd;
    private MarkHandle _handle;
    private Mark? _dragMark;
    private int _dragIndex = -1;
    private StandardCursorType _cursorType = StandardCursorType.Cross;

    public ScreenshotsView()
    {
        InitializeComponent();
        foreach (var repeater in new[] { ShotGrid, ShotList })
        {
            repeater.ElementPrepared += (_, e) => (e.Element.DataContext as ShotItem)?.LoadThumbnailAsync();
            repeater.ElementClearing += (_, e) => (e.Element.DataContext as ShotItem)?.ReleaseThumbnail();
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
        Stage.KeyDown += OnStageKey;
        Stage.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
                DrawOverlay();
        };
        foreach (var slider in new[] { BrightnessSlider, ContrastSlider, SaturationSlider, RotationSlider, OpacitySlider })
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
        _handle = MarkHandle.None;
        _dragMark = null;
        _dragIndex = -1;
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

    private float PicturePixels(double viewPixels) => _editor is null || PictureRect().Width <= 0 ? 0 : (float)(viewPixels * _editor.ImageSize.Width / PictureRect().Width);

    private MarkHandle HandleUnder(Point p)
    {
        if (_editor?.Selected is not { } mark)
            return MarkHandle.None;
        var (w, h) = _editor.ImageSize;
        return MarkTransform.HandleAt(mark, ToImage(p), w, h, PicturePixels(HandleGrab), PicturePixels(MarkTransform.RotateOffset));
    }

    private void OnStagePressed(object? sender, PointerPressedEventArgs e)
    {
        if (_editor is not { } editor)
            return;
        Stage.Focus();
        var r = PictureRect();
        var p = e.GetPosition(Stage);
        var props = e.GetCurrentPoint(Stage).Properties;
        var (w, h) = editor.ImageSize;

        if (props.IsRightButtonPressed)
        {
            if (r.Contains(p) && MarkTransform.HitTest(editor.Edit.Marks, ToImage(p), w, h, PicturePixels(HitTolerance)) is >= 0 and var hit)
            {
                editor.SelectMark(hit);
                ShowMarkMenu();
            }
            e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed)
            return;

        var handle = HandleUnder(p);
        if (handle == MarkHandle.None && !r.Contains(p))
            return;
        if (handle == MarkHandle.None && editor.Tool == EditTool.None)
        {
            var hit = MarkTransform.HitTest(editor.Edit.Marks, ToImage(p), w, h, PicturePixels(HitTolerance));
            editor.SelectMark(hit);
            if (hit < 0)
                return;
            handle = MarkHandle.Body;
        }

        if (handle != MarkHandle.None && editor.Selected is { } mark)
        {
            _handle = handle;
            _dragMark = mark;
            _dragIndex = editor.SelectedMark;
            editor.BeginTransform();
        }
        else
        {
            editor.SelectMark(-1);
        }
        _dragStart = _dragEnd = p;
        e.Pointer.Capture(Stage);
        e.Handled = true;
    }

    private void ShowMarkMenu()
    {
        if (_editor is not { } editor)
            return;
        var menu = new ContextMenu();
        void Item(string header, ICommand command)
        {
            var item = new MenuItem { Header = header, Command = command };
            menu.Items.Add(item);
        }
        Item("Duplicate", editor.DuplicateSelectedCommand);
        Item("Bring to front", editor.BringToFrontCommand);
        Item("Send to back", editor.SendToBackCommand);
        Item("Delete", editor.DeleteSelectedCommand);
        menu.PlacementTarget = Stage;
        menu.Open(Stage);
    }

    private void OnStageMoved(object? sender, PointerEventArgs e)
    {
        var p = e.GetPosition(Stage);
        if (_dragStart is not { } start)
        {
            UpdateCursor(p);
            return;
        }
        var r = PictureRect();
        _dragEnd = new Point(Math.Clamp(p.X, r.Left, r.Right), Math.Clamp(p.Y, r.Top, r.Bottom));
        if (_editor is not { } editor)
            return;
        if (_handle != MarkHandle.None && _dragMark is { } mark)
        {
            var (w, h) = editor.ImageSize;
            editor.UpdateTransform(_dragIndex, MarkTransform.Drag(mark, _handle, ToImage(start), ToImage(_dragEnd), w, h, e.KeyModifiers.HasFlag(KeyModifiers.Shift)));
            return;
        }
        if (editor is { Tool: EditTool.Crop, Ratio.Ratio: { } ratio })
            _dragEnd = KeepRatio(start, _dragEnd, ratio, r);
        DrawOverlay();
    }

    private void UpdateCursor(Point p)
    {
        if (_editor is not { } editor)
            return;
        var handle = HandleUnder(p);
        var (w, h) = editor.ImageSize;
        if (handle == MarkHandle.None && editor.Tool == EditTool.None && PictureRect().Contains(p)
            && MarkTransform.HitTest(editor.Edit.Marks, ToImage(p), w, h, PicturePixels(HitTolerance)) >= 0)
            handle = MarkHandle.Body;
        var type = handle switch
        {
            MarkHandle.Body => StandardCursorType.SizeAll,
            MarkHandle.Rotate => StandardCursorType.Hand,
            MarkHandle.North or MarkHandle.South => StandardCursorType.SizeNorthSouth,
            MarkHandle.East or MarkHandle.West => StandardCursorType.SizeWestEast,
            MarkHandle.NorthWest => StandardCursorType.TopLeftCorner,
            MarkHandle.NorthEast => StandardCursorType.TopRightCorner,
            MarkHandle.SouthWest => StandardCursorType.BottomLeftCorner,
            MarkHandle.SouthEast => StandardCursorType.BottomRightCorner,
            MarkHandle.Start or MarkHandle.End => StandardCursorType.Hand,
            _ => editor.Tool == EditTool.None ? StandardCursorType.Arrow : StandardCursorType.Cross,
        };
        if (type != _cursorType)
        {
            _cursorType = type;
            Stage.Cursor = new Cursor(type);
        }
    }

    private async void OnStageReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragStart is not { } start || _editor is not { } editor)
            return;
        var transforming = _handle != MarkHandle.None;
        _handle = MarkHandle.None;
        _dragMark = null;
        _dragIndex = -1;
        _dragStart = null;
        e.Pointer.Capture(null);
        if (transforming)
        {
            editor.EndTransform();
            DrawOverlay();
            return;
        }

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
            case EditTool.Image:
                await editor.AddImageAsync(b);
                break;
            case EditTool.None:
                break;
            default:
                editor.AddMark(area);
                break;
        }
        DrawOverlay();
    }

    private void OnStageKey(object? sender, KeyEventArgs e)
    {
        if (_editor is not { HasSelectedMark: true } editor)
            return;
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        switch (e.Key)
        {
            case Key.Delete or Key.Back:
                editor.DeleteSelectedCommand.Execute(null);
                break;
            case Key.Escape:
                editor.SelectMark(-1);
                break;
            case Key.Left:
                editor.Nudge(-step, 0);
                break;
            case Key.Right:
                editor.Nudge(step, 0);
                break;
            case Key.Up:
                editor.Nudge(0, -step);
                break;
            case Key.Down:
                editor.Nudge(0, step);
                break;
            case Key.D when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                editor.DuplicateSelectedCommand.Execute(null);
                break;
            default:
                return;
        }
        e.Handled = true;
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

        if (_editor.Edit.Crop is { } crop && !(_dragStart is not null && _handle == MarkHandle.None && _editor.Tool == EditTool.Crop))
        {
            var c = new Rect(r.X + crop.Left * r.Width, r.Y + crop.Top * r.Height, crop.Width * r.Width, crop.Height * r.Height);
            Shade(r, c);
        }

        if (_editor.Selected is { } selected)
            DrawSelection(r, selected);

        if (_dragStart is not { } start || _handle != MarkHandle.None)
            return;
        if (_editor.Tool == EditTool.Arrow)
        {
            Overlay.Children.Add(new Line { StartPoint = start, EndPoint = _dragEnd, Stroke = Outline, StrokeThickness = 2 });
            return;
        }
        if (_editor.Tool == EditTool.None)
            return;
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

    private void DrawSelection(Rect r, Mark m)
    {
        var (w, h) = _editor!.ImageSize;
        var scale = r.Width / w;
        Point View(SKPoint picture) => new(r.X + picture.X * scale, r.Y + picture.Y * scale);
        var handles = MarkTransform.Handles(m, w, h, PicturePixels(MarkTransform.RotateOffset)).ToDictionary(x => x.Handle, x => View(x.At));

        if (m.Kind == MarkKind.Arrow)
        {
            Overlay.Children.Add(new Line { StartPoint = handles[MarkHandle.Start], EndPoint = handles[MarkHandle.End], Stroke = Outline, StrokeThickness = 1.5, StrokeDashArray = [4, 3] });
        }
        else
        {
            Overlay.Children.Add(new Polygon
            {
                Points = [handles[MarkHandle.NorthWest], handles[MarkHandle.NorthEast], handles[MarkHandle.SouthEast], handles[MarkHandle.SouthWest]],
                Stroke = Outline,
                StrokeThickness = 1.5,
                StrokeDashArray = [4, 3],
            });
            if (handles.TryGetValue(MarkHandle.Rotate, out var knob))
            {
                var top = new Point((handles[MarkHandle.NorthWest].X + handles[MarkHandle.NorthEast].X) / 2, (handles[MarkHandle.NorthWest].Y + handles[MarkHandle.NorthEast].Y) / 2);
                Overlay.Children.Add(new Line { StartPoint = top, EndPoint = knob, Stroke = Outline, StrokeThickness = 1.5 });
            }
        }

        foreach (var (handle, at) in handles)
        {
            Shape grip = handle == MarkHandle.Rotate
                ? new Ellipse { Width = KnobSize, Height = KnobSize, Fill = Outline, Stroke = Grip, StrokeThickness = 1 }
                : new Rectangle { Width = KnobSize, Height = KnobSize, Fill = Outline, Stroke = Grip, StrokeThickness = 1 };
            Canvas.SetLeft(grip, at.X - KnobSize / 2);
            Canvas.SetTop(grip, at.Y - KnobSize / 2);
            Overlay.Children.Add(grip);
        }
    }
}
