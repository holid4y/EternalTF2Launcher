using System.Collections.Immutable;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Eternal.App.Services;
using Eternal.Core.Steam;
using SkiaSharp;

namespace Eternal.App.ViewModels;

public enum EditTool { None, Crop, Blur, Pixelate, Box, Arrow, Text, Image }

public sealed record CropRatio(string Label, float? Ratio)
{
    public override string ToString() => Label;
}

public sealed partial class Swatch(SKColor color, string name) : ObservableObject
{
    public SKColor Color { get; } = color;
    public string Name { get; } = name;
    public IBrush Brush { get; } = new SolidColorBrush(Avalonia.Media.Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue));

    [ObservableProperty] private bool _selected;
}

public sealed partial class ScreenshotEditorViewModel : ObservableObject, IDisposable
{
    private const int PreviewWidth = 1280;
    private const float PictureFraction = 0.3f;
    private readonly SKBitmap _source;
    private readonly IDialogs _dialogs;
    private readonly Func<SteamScreenshot, ImageEdit, Task<bool>> _save;
    private readonly Action _close;
    private readonly Stack<ImageEdit> _undo = new();
    private readonly object _sourceGate = new();
    private readonly List<SKImage> _pictures = [];
    private CancellationTokenSource? _render;
    private ImageEdit? _mergeBase;
    private ImageEdit? _transformBase;
    private bool _disposed;

    private ScreenshotEditorViewModel(SteamScreenshot shot, SKBitmap source, IDialogs dialogs, Func<SteamScreenshot, ImageEdit, Task<bool>> save, Action close)
    {
        Shot = shot;
        _source = source;
        _dialogs = dialogs;
        _save = save;
        _close = close;
        _ratio = Ratios[0];
        _color = Swatches[0];
        _color.Selected = true;
        _ = RenderAsync();
    }

    public static async Task<ScreenshotEditorViewModel?> OpenAsync(SteamScreenshot shot, IDialogs dialogs, Func<SteamScreenshot, ImageEdit, Task<bool>> save, Action close)
    {
        var source = await Task.Run(() => ThumbnailMaker.Decode(shot.FullPath, PreviewWidth));
        return source is null ? null : new ScreenshotEditorViewModel(shot, source, dialogs, save, close);
    }

    public SteamScreenshot Shot { get; }

    public CropRatio[] Ratios { get; } = [new("Free", null), new("16:9", 16f / 9), new("4:3", 4f / 3), new("1:1", 1)];

    public Swatch[] Swatches { get; } =
    [
        new(new SKColor(0xFF, 0xF6, 0xE8), "White"), new(new SKColor(0xE5, 0x48, 0x4D), "Red"), new(new SKColor(0xF5, 0xC5, 0x18), "Yellow"),
        new(new SKColor(0x8D, 0xB2, 0x55), "Green"), new(new SKColor(0x5B, 0x8D, 0xB8), "Blue"), new(new SKColor(0x12, 0x0F, 0x16), "Black"),
    ];

    [ObservableProperty] private ImageEdit _edit = new();
    [ObservableProperty] private Bitmap? _preview;
    [ObservableProperty] private EditTool _tool;
    [ObservableProperty] private CropRatio _ratio;
    [ObservableProperty] private Swatch _color;
    [ObservableProperty] private bool _saving;

    public double Brightness
    {
        get => Edit.Brightness * 200;
        set => Change(Edit with { Brightness = (float)Math.Clamp(value / 200, -0.5, 0.5) }, merge: true);
    }

    public double Contrast
    {
        get => Edit.Contrast * 100;
        set => Change(Edit with { Contrast = (float)Math.Clamp(value / 100, -0.9, 1) }, merge: true);
    }

    public double Saturation
    {
        get => Edit.Saturation * 100;
        set => Change(Edit with { Saturation = (float)Math.Clamp(value / 100, -1, 1) }, merge: true);
    }

    public bool CanUndo => _undo.Count > 0;
    public bool HasChanges => !Edit.IsEmpty;
    public bool Cropping => Tool == EditTool.Crop;
    public bool UsingBlur => Tool == EditTool.Blur;
    public bool UsingPixelate => Tool == EditTool.Pixelate;
    public bool UsingBox => Tool == EditTool.Box;
    public bool UsingArrow => Tool == EditTool.Arrow;
    public bool UsingText => Tool == EditTool.Text;
    public bool UsingImage => Tool == EditTool.Image;
    public bool Marking => Tool is EditTool.Box or EditTool.Arrow or EditTool.Text or EditTool.Image;
    public bool Selecting => Tool == EditTool.None;
    public string Hint => Tool switch
    {
        EditTool.Crop => "Drag over the picture to pick what stays.",
        EditTool.Blur => "Drag a box over names, chat or anything else to blur it.",
        EditTool.Pixelate => "Drag a box over what should turn into blocks.",
        EditTool.Box => "Drag to draw a box.",
        EditTool.Arrow => "Drag from where the arrow starts to where it points.",
        EditTool.Text => "Click where the text should go.",
        EditTool.Image => "Click where the picture should go, then pick an image file.",
        _ => "Click something you placed and drag it. Shift keeps it straight.",
    };

    public double Aspect => Edit.Rotation % 180 == 0 ? (double)_source.Width / _source.Height : (double)_source.Height / _source.Width;

    partial void OnEditChanged(ImageEdit value)
    {
        OnPropertyChanged(nameof(Brightness));
        OnPropertyChanged(nameof(Contrast));
        OnPropertyChanged(nameof(Saturation));
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(Aspect));
        if (SelectedMark >= value.Marks.Count)
            SelectMark(-1);
        else
            RaiseSelection();
        _ = RenderAsync();
    }

    partial void OnToolChanged(EditTool value)
    {
        foreach (var name in new[] { nameof(Cropping), nameof(UsingBlur), nameof(UsingPixelate), nameof(UsingBox), nameof(UsingArrow), nameof(UsingText), nameof(UsingImage), nameof(Marking), nameof(Selecting), nameof(Hint) })
            OnPropertyChanged(name);
    }

    partial void OnColorChanged(Swatch? oldValue, Swatch newValue)
    {
        if (oldValue is not null)
            oldValue.Selected = false;
        newValue.Selected = true;
    }

    private void Change(ImageEdit next, bool merge = false)
    {
        if (next == Edit)
            return;
        if (!merge || _mergeBase is null)
        {
            _undo.Push(Edit);
            OnPropertyChanged(nameof(CanUndo));
        }
        _mergeBase = merge ? Edit : null;
        Edit = next;
    }

    public void EndSliderChange() => _mergeBase = null;

    [RelayCommand]
    private void UseTool(EditTool tool) => Tool = Tool == tool ? EditTool.None : tool;

    [RelayCommand]
    private void PickColor(Swatch swatch) => Color = swatch;

    [RelayCommand]
    private void TurnLeft() => Change(Edit.Turn(clockwise: false, ImageSize.Width, ImageSize.Height));

    [RelayCommand]
    private void TurnRight() => Change(Edit.Turn(clockwise: true, ImageSize.Width, ImageSize.Height));

    [RelayCommand]
    private void FlipHorizontal() => Change(Edit.Flip(horizontal: true));

    [RelayCommand]
    private void FlipVertical() => Change(Edit.Flip(horizontal: false));

    [RelayCommand]
    private void ClearCrop() => Change(Edit with { Crop = null });

    [RelayCommand]
    private void ClearMarks() => Change(Edit with { Marks = [] });

    [RelayCommand]
    private void Undo()
    {
        if (_undo.Count == 0)
            return;
        _mergeBase = null;
        Edit = _undo.Pop();
        OnPropertyChanged(nameof(CanUndo));
    }

    [RelayCommand]
    private void Reset() => Change(new ImageEdit());

    public void SetCrop(SKRect area)
    {
        area = Normalize(area);
        if (area.Width > 0.01f && area.Height > 0.01f)
            Change(Edit with { Crop = area });
    }

    public void AddMark(SKRect area)
    {
        var kind = Tool switch
        {
            EditTool.Blur => MarkKind.Blur,
            EditTool.Pixelate => MarkKind.Pixelate,
            EditTool.Box => MarkKind.Box,
            EditTool.Arrow => MarkKind.Arrow,
            _ => (MarkKind?)null,
        };
        if (kind is not { } k)
            return;
        var clamped = new SKRect(Clamp(area.Left), Clamp(area.Top), Clamp(area.Right), Clamp(area.Bottom));
        var shape = k == MarkKind.Arrow ? clamped : Normalize(clamped);
        if (Math.Abs(shape.Width) < 0.005f && Math.Abs(shape.Height) < 0.005f)
            return;
        Place(new Mark(k, shape, Color.Color));
    }

    /// <summary>Adds the mark, selects it and goes back to the pointer, so it can be dragged straight away.</summary>
    private void Place(Mark mark)
    {
        Change(Edit with { Marks = Edit.Marks.Add(mark) });
        SelectMark(Edit.Marks.Count - 1);
        Tool = EditTool.None;
    }

    public (float Width, float Height) ImageSize => Edit.Rotation % 180 == 0
        ? ((float)_source.Width, (float)_source.Height)
        : ((float)_source.Height, (float)_source.Width);

    [ObservableProperty] private int _selectedMark = -1;

    public Mark? Selected => SelectedMark >= 0 && SelectedMark < Edit.Marks.Count ? Edit.Marks[SelectedMark] : null;
    public bool HasSelectedMark => Selected is not null;
    public bool SelectedCanRotate => Selected?.CanRotate == true;
    public bool SelectedUsesColor => Selected?.UsesColor == true;
    public bool SelectedIsText => Selected?.Kind == MarkKind.Text;
    public bool SelectedHasOpacity => Selected?.Kind is not (null or MarkKind.Blur or MarkKind.Pixelate);
    public bool SelectedHasBox => Selected is not null && Selected.Kind != MarkKind.Arrow;

    public string SelectedTitle => Selected?.Kind switch
    {
        MarkKind.Blur => "Blur",
        MarkKind.Pixelate => "Pixelate",
        MarkKind.Box => "Box",
        MarkKind.Arrow => "Arrow",
        MarkKind.Text => "Text",
        MarkKind.Image => "Picture",
        _ => "",
    };

    public decimal? SelectedLeft
    {
        get => Selected is { } m ? Percent(m.Area.Left) : null;
        set => EditBox(value, (a, v) => new SKRect(v, a.Top, v + a.Width, a.Bottom));
    }

    public decimal? SelectedTop
    {
        get => Selected is { } m ? Percent(m.Area.Top) : null;
        set => EditBox(value, (a, v) => new SKRect(a.Left, v, a.Right, v + a.Height));
    }

    public decimal? SelectedWidth
    {
        get => Selected is { } m ? Percent(m.Area.Width) : null;
        set => EditBox(value, (a, v) => new SKRect(a.Left, a.Top, a.Left + Math.Max(0.005f, v), a.Bottom));
    }

    public decimal? SelectedHeight
    {
        get => Selected is { } m ? Percent(m.Area.Height) : null;
        set => EditBox(value, (a, v) => new SKRect(a.Left, a.Top, a.Right, a.Top + Math.Max(0.005f, v)));
    }

    public double SelectedRotation
    {
        get => Selected?.Rotation ?? 0;
        set => EditSelected(m => m with { Rotation = (float)value });
    }

    public double SelectedOpacity
    {
        get => (Selected?.Opacity ?? 1) * 100;
        set => EditSelected(m => m with { Opacity = (float)Math.Clamp(value / 100, 0, 1) });
    }

    public string SelectedText
    {
        get => Selected?.Text ?? "";
        set
        {
            var text = new string((value ?? "").Where(c => !char.IsControl(c)).Take(ScreenshotRenderer.MaxTextLength).ToArray());
            if (text.Length == 0 || Selected is not { Kind: MarkKind.Text } mark)
                return;
            var (w, h) = ImageSize;
            var measured = ScreenshotRenderer.TextArea(text, new SKPoint(mark.Area.Left, mark.Area.Top), w, h);
            var scale = mark.Area.Height / measured.Height;
            EditSelected(m => m with { Text = text, Area = new SKRect(m.Area.Left, m.Area.Top, m.Area.Left + measured.Width * scale, m.Area.Bottom) });
        }
    }

    private static decimal Percent(float v) => Math.Round((decimal)(v * 100), 1);

    private void EditBox(decimal? percent, Func<SKRect, float, SKRect> apply)
    {
        if (percent is { } value)
            EditSelected(m => m with { Area = apply(m.Area, (float)value / 100f) });
    }

    private void EditSelected(Func<Mark, Mark> change)
    {
        if (Selected is { } mark && change(mark) is var next && next != mark)
            Change(Edit with { Marks = Edit.Marks.SetItem(SelectedMark, next) }, merge: true);
    }

    private void RaiseSelection()
    {
        foreach (var name in new[] { nameof(Selected), nameof(HasSelectedMark), nameof(SelectedCanRotate), nameof(SelectedUsesColor), nameof(SelectedIsText),
                     nameof(SelectedHasOpacity), nameof(SelectedHasBox), nameof(SelectedTitle), nameof(SelectedLeft), nameof(SelectedTop), nameof(SelectedWidth),
                     nameof(SelectedHeight), nameof(SelectedRotation), nameof(SelectedOpacity), nameof(SelectedText) })
            OnPropertyChanged(name);
    }

    partial void OnSelectedMarkChanged(int value)
    {
        _mergeBase = null;
        RaiseSelection();
    }

    public void SelectMark(int index) => SelectedMark = index >= 0 && index < Edit.Marks.Count ? index : -1;

    /// <summary>A drag changes the picture live and counts as one undo step when it ends.</summary>
    public void BeginTransform() => _transformBase = Edit;

    public void UpdateTransform(int index, Mark mark)
    {
        if (index >= 0 && index < Edit.Marks.Count && Edit.Marks[index] != mark)
            Edit = Edit with { Marks = Edit.Marks.SetItem(index, mark) };
    }

    public void EndTransform()
    {
        if (_transformBase is { } start && start != Edit)
        {
            _undo.Push(start);
            OnPropertyChanged(nameof(CanUndo));
        }
        _transformBase = null;
    }

    public void Nudge(int dx, int dy)
    {
        if (Selected is not { } mark)
            return;
        var (w, h) = ImageSize;
        var moved = MarkTransform.Drag(mark, MarkHandle.Body, default, new SKPoint(dx / w, dy / h), w, h, false);
        Change(Edit with { Marks = Edit.Marks.SetItem(SelectedMark, moved) });
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (Selected is null)
            return;
        var index = SelectedMark;
        SelectMark(-1);
        Change(Edit with { Marks = Edit.Marks.RemoveAt(index) });
    }

    [RelayCommand]
    private void DuplicateSelected()
    {
        if (Selected is not { } mark)
            return;
        const float offset = 0.02f;
        var area = new SKRect(mark.Area.Left + offset, mark.Area.Top + offset, mark.Area.Right + offset, mark.Area.Bottom + offset);
        Change(Edit with { Marks = Edit.Marks.Add(mark with { Area = area }) });
        SelectMark(Edit.Marks.Count - 1);
    }

    [RelayCommand]
    private void BringToFront() => Reorder(Edit.Marks.Count - 1);

    [RelayCommand]
    private void SendToBack() => Reorder(0);

    private void Reorder(int index)
    {
        if (Selected is not { } mark || index == SelectedMark)
            return;
        Change(Edit with { Marks = Edit.Marks.RemoveAt(SelectedMark).Insert(index, mark) });
        SelectMark(index);
    }

    [RelayCommand]
    private void RecolorSelected(Swatch swatch)
    {
        Color = swatch;
        EditSelected(m => m.UsesColor ? m with { Color = swatch.Color } : m);
        EndSliderChange();
    }

    public async Task AddImageAsync(SKPoint at)
    {
        var files = await Pickers.OpenFilesAsync("Add a picture", false, "Images",
            "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.gif");
        if (files.Count == 0)
            return;
        SKImage picture;
        try
        {
            picture = await Task.Run(() => ScreenshotRenderer.LoadImageFile(files[0]));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            await _dialogs.AlertAsync("That picture can't be used", ex.Message);
            return;
        }
        _pictures.Add(picture);

        var fracW = PictureFraction;
        var fracH = (float)(fracW * Aspect * picture.Height / Math.Max(1, picture.Width));
        if (fracH > PictureFraction)
        {
            fracH = PictureFraction;
            fracW = (float)(fracH * picture.Width / (Math.Max(1, picture.Height) * Aspect));
        }
        var left = Math.Clamp(at.X - fracW / 2, 0f, 1f - fracW);
        var top = Math.Clamp(at.Y - fracH / 2, 0f, 1f - fracH);
        Place(new Mark(MarkKind.Image, new SKRect(left, top, left + fracW, top + fracH), Color.Color, Picture: picture));
    }

    public async Task AddTextAsync(SKPoint at)
    {
        var text = await _dialogs.PromptAsync("Add text", "What should it say?", confirm: "Add");
        if (string.IsNullOrWhiteSpace(text))
            return;
        text = new string(text.Where(c => !char.IsControl(c)).Take(ScreenshotRenderer.MaxTextLength).ToArray());
        var (w, h) = ImageSize;
        Place(new Mark(MarkKind.Text, ScreenshotRenderer.TextArea(text, new SKPoint(Clamp(at.X), Clamp(at.Y)), w, h), Color.Color, text));
    }

    private static float Clamp(float v) => Math.Clamp(v, 0f, 1f);

    private static SKRect Normalize(SKRect r) =>
        new(Clamp(Math.Min(r.Left, r.Right)), Clamp(Math.Min(r.Top, r.Bottom)), Clamp(Math.Max(r.Left, r.Right)), Clamp(Math.Max(r.Top, r.Bottom)));

    [RelayCommand]
    private async Task CancelAsync()
    {
        if (Saving)
            return;
        if (HasChanges && !await _dialogs.ConfirmAsync("Leave the editor", "Throw away these edits?", "Throw away", "Keep editing"))
            return;
        _close();
        Dispose();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!HasChanges || Saving)
            return;
        Saving = true;
        try
        {
            if (await _save(Shot, Edit))
                Dispose();
        }
        finally
        {
            Saving = false;
        }
    }

    private async Task RenderAsync()
    {
        if (_disposed)
            return;
        _render?.Cancel();
        var cts = _render = new CancellationTokenSource();
        var token = cts.Token;
        var edit = Edit;
        try
        {
            await Task.Delay(30, token);
            var bitmap = await Task.Run(() =>
            {
                lock (_sourceGate)
                {
                    token.ThrowIfCancellationRequested();
                    using var rendered = ScreenshotRenderer.Render(_source, edit, showCrop: false);
                    return new Bitmap(PixelFormat.Rgba8888, AlphaFormat.Premul, rendered.GetPixels(),
                        new PixelSize(rendered.Width, rendered.Height), new Vector(96, 96), rendered.RowBytes);
                }
            }, token);
            if (_render == cts && !_disposed)
            {
                var old = Preview;
                Preview = bitmap;
                old?.Dispose();
            }
            else
            {
                bitmap.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _render?.Cancel();
        lock (_sourceGate)
            _source.Dispose();
        var preview = Preview;
        Preview = null;
        preview?.Dispose();
        foreach (var picture in _pictures)
            picture.Dispose();
    }
}
