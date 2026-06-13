using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LauncherTF2.Views;

/// <summary>
/// Modal 16:9 crop dialog for mod thumbnails. All selection math happens in
/// image PIXEL coordinates: the source Image is forced to PixelWidth ×
/// PixelHeight DIPs (ignoring any DPI metadata in the file) and the enclosing
/// Viewbox scales it to fit, so canvas positions map 1:1 onto bitmap pixels.
///
/// The constructor throws if the image can't be loaded — callers surface the
/// error with the launcher's themed dialog.
/// </summary>
public partial class ImageCropDialog : Window
{
    private const double Aspect = 16.0 / 9.0;

    public string? CroppedImagePath { get; private set; }

    private readonly BitmapImage _bitmap;
    private readonly double _minWidth;

    private enum DragMode { None, Move, Resize }
    private DragMode _mode = DragMode.None;
    private string? _activeCorner;          // "TL" | "TR" | "BL" | "BR"
    private Point _lastMousePos;

    // Selection bounds in image pixel coordinates
    private double _selX, _selY, _selW, _selH;

    public ImageCropDialog(string imagePath)
    {
        InitializeComponent();

        _bitmap = new BitmapImage();
        _bitmap.BeginInit();
        _bitmap.CacheOption = BitmapCacheOption.OnLoad;
        _bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        _bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
        _bitmap.EndInit();
        _bitmap.Freeze();

        // 1:1 pixel-to-DIP mapping regardless of the file's DPI metadata
        SourceImage.Source = _bitmap;
        SourceImage.Width  = _bitmap.PixelWidth;
        SourceImage.Height = _bitmap.PixelHeight;
        CanvasGrid.Width   = _bitmap.PixelWidth;
        CanvasGrid.Height  = _bitmap.PixelHeight;

        _minWidth = Math.Min(64, _bitmap.PixelWidth);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; Close(); }
        };

        Loaded += (_, _) => InitializeSelection();
    }

    private void InitializeSelection()
    {
        double imgW = _bitmap.PixelWidth;
        double imgH = _bitmap.PixelHeight;

        // Largest centered 16:9 box that fits
        double w = imgW;
        double h = w / Aspect;
        if (h > imgH)
        {
            h = imgH;
            w = h * Aspect;
        }

        _selW = w;
        _selH = h;
        _selX = (imgW - w) / 2;
        _selY = (imgH - h) / 2;

        UpdateSelectionUI();
    }

    private void UpdateSelectionUI()
    {
        Canvas.SetLeft(SelectionBorder, _selX);
        Canvas.SetTop(SelectionBorder, _selY);
        SelectionBorder.Width = _selW;
        SelectionBorder.Height = _selH;

        // Dark overlay with an even-odd hole over the selection
        var geometry = new GeometryGroup { FillRule = FillRule.EvenOdd };
        geometry.Children.Add(new RectangleGeometry(new Rect(0, 0, _bitmap.PixelWidth, _bitmap.PixelHeight)));
        geometry.Children.Add(new RectangleGeometry(new Rect(_selX, _selY, _selW, _selH)));

        OverlayCanvas.Children.Clear();
        OverlayCanvas.Children.Add(new System.Windows.Shapes.Path
        {
            Fill = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
            Data = geometry,
        });
    }

    // ─────────────────────────── mouse interaction ───────────────────────────

    private void DragCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        var pos = e.GetPosition(DragCanvas);
        _activeCorner = (e.OriginalSource as FrameworkElement)?.Tag as string;

        if (_activeCorner is "TL" or "TR" or "BL" or "BR")
        {
            _mode = DragMode.Resize;
        }
        else if (pos.X >= _selX && pos.X <= _selX + _selW &&
                 pos.Y >= _selY && pos.Y <= _selY + _selH)
        {
            _mode = DragMode.Move;
        }
        else
        {
            _mode = DragMode.None;
            return;
        }

        _lastMousePos = pos;
        DragCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void DragCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_mode == DragMode.None) return;

        var pos = e.GetPosition(DragCanvas);

        if (_mode == DragMode.Move)
        {
            _selX += pos.X - _lastMousePos.X;
            _selY += pos.Y - _lastMousePos.Y;

            _selX = Math.Clamp(_selX, 0, _bitmap.PixelWidth - _selW);
            _selY = Math.Clamp(_selY, 0, _bitmap.PixelHeight - _selH);

            _lastMousePos = pos;
        }
        else
        {
            ResizeTo(pos);
        }

        UpdateSelectionUI();
    }

    /// <summary>
    /// Resizes from the dragged corner with the opposite corner anchored,
    /// keeping the 16:9 aspect and staying inside the image.
    /// </summary>
    private void ResizeTo(Point pos)
    {
        // direction the rect grows from the anchor, per dragged corner
        int sx = _activeCorner is "TR" or "BR" ? 1 : -1;
        int sy = _activeCorner is "BL" or "BR" ? 1 : -1;

        double anchorX = sx > 0 ? _selX : _selX + _selW;
        double anchorY = sy > 0 ? _selY : _selY + _selH;

        double maxW = sx > 0 ? _bitmap.PixelWidth - anchorX : anchorX;
        double maxH = sy > 0 ? _bitmap.PixelHeight - anchorY : anchorY;

        double w = Math.Clamp((pos.X - anchorX) * sx, _minWidth, maxW);
        double h = w / Aspect;
        if (h > maxH)
        {
            h = maxH;
            w = h * Aspect;
        }

        _selW = w;
        _selH = h;
        _selX = sx > 0 ? anchorX : anchorX - w;
        _selY = sy > 0 ? anchorY : anchorY - h;
    }

    private void DragCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _mode = DragMode.None;
        _activeCorner = null;
        DragCanvas.ReleaseMouseCapture();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    // ─────────────────────────── actions ───────────────────────────

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            int pw = _bitmap.PixelWidth, ph = _bitmap.PixelHeight;
            int x = Math.Clamp((int)Math.Round(_selX), 0, pw - 1);
            int y = Math.Clamp((int)Math.Round(_selY), 0, ph - 1);
            int w = Math.Clamp((int)Math.Round(_selW), 1, pw - x);
            int h = Math.Clamp((int)Math.Round(_selH), 1, ph - y);

            var cropped = new CroppedBitmap(_bitmap, new Int32Rect(x, y, w, h));
            var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
            encoder.Frames.Add(BitmapFrame.Create(cropped));

            var tempFile = Path.Combine(Path.GetTempPath(), $"thumb_{Guid.NewGuid():N}.jpg");
            using (var fs = new FileStream(tempFile, FileMode.Create))
                encoder.Save(fs);

            CroppedImagePath = tempFile;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageDialog.ShowError("Crop Failed", $"The thumbnail could not be saved.\n\n{ex.Message}");
        }
    }
}
