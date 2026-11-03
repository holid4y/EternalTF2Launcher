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

public enum EditTool { None, Crop, Blur, Pixelate, Box, Arrow, Text }

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

/// <summary>
/// The editor works on a copy scaled to fit the screen and keeps every change
/// as a list of steps, so undo is free and nothing touches the file until Save
/// renders the steps onto the full-size picture.
/// </summary>
public sealed partial class ScreenshotEditorViewModel : ObservableObject, IDisposable
{
    private const int PreviewWidth = 1600;
    private readonly SKBitmap _source;
    private readonly IDialogs _dialogs;
    private readonly Func<SteamScreenshot, ImageEdit, Task<bool>> _save;
    private readonly Action _close;
    private readonly Stack<ImageEdit> _undo = new();
    private CancellationTokenSource? _render;
    private ImageEdit? _mergeBase;

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
        var source = await Task.Run(() => ThumbnailMaker.Decode(shot.FullPath, PreviewWidth / 2));
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
    public bool Marking => Tool is EditTool.Box or EditTool.Arrow or EditTool.Text;
    public string Hint => Tool switch
    {
        EditTool.Crop => "Drag over the picture to pick what stays.",
        EditTool.Blur => "Drag a box over names, chat or anything else to blur it.",
        EditTool.Pixelate => "Drag a box over what should turn into blocks.",
        EditTool.Box => "Drag to draw a box.",
        EditTool.Arrow => "Drag from where the arrow starts to where it points.",
        EditTool.Text => "Click where the text should go.",
        _ => "Pick a tool on the right, or turn and adjust the picture.",
    };

    /// <summary>The picture's width over its height after turning, for keeping crops in proportion.</summary>
    public double Aspect => Edit.Rotation % 180 == 0 ? (double)_source.Width / _source.Height : (double)_source.Height / _source.Width;

    partial void OnEditChanged(ImageEdit value)
    {
        OnPropertyChanged(nameof(Brightness));
        OnPropertyChanged(nameof(Contrast));
        OnPropertyChanged(nameof(Saturation));
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(Aspect));
        _ = RenderAsync();
    }

    partial void OnToolChanged(EditTool value)
    {
        foreach (var name in new[] { nameof(Cropping), nameof(UsingBlur), nameof(UsingPixelate), nameof(UsingBox), nameof(UsingArrow), nameof(UsingText), nameof(Marking), nameof(Hint) })
            OnPropertyChanged(name);
    }

    partial void OnColorChanged(Swatch? oldValue, Swatch newValue)
    {
        if (oldValue is not null)
            oldValue.Selected = false;
        newValue.Selected = true;
    }

    /// <summary>Slider moves merge into one undo step; anything else is its own step.</summary>
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
    private void TurnLeft() => Change(Edit.Turn(clockwise: false));

    [RelayCommand]
    private void TurnRight() => Change(Edit.Turn(clockwise: true));

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

    /// <summary>Adds a box-shaped mark (blur, pixelate, box) or an arrow from one corner to the other.</summary>
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
        Change(Edit with { Marks = Edit.Marks.Add(new Mark(k, shape, Color.Color)) });
    }

    public async Task AddTextAsync(SKPoint at)
    {
        var text = await _dialogs.PromptAsync("Add text", "What should it say?", confirm: "Add");
        if (string.IsNullOrWhiteSpace(text))
            return;
        text = new string(text.Where(c => !char.IsControl(c)).Take(ScreenshotRenderer.MaxTextLength).ToArray());
        Change(Edit with { Marks = Edit.Marks.Add(new Mark(MarkKind.Text, new SKRect(Clamp(at.X), Clamp(at.Y), Clamp(at.X), Clamp(at.Y)), Color.Color, text)) });
    }

    private static float Clamp(float v) => Math.Clamp(v, 0f, 1f);

    private static SKRect Normalize(SKRect r) =>
        new(Clamp(Math.Min(r.Left, r.Right)), Clamp(Math.Min(r.Top, r.Bottom)), Clamp(Math.Max(r.Left, r.Right)), Clamp(Math.Max(r.Top, r.Bottom)));

    [RelayCommand]
    private async Task CancelAsync()
    {
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

    /// <summary>Renders the preview off the UI thread; a newer edit drops an older render.</summary>
    private async Task RenderAsync()
    {
        _render?.Cancel();
        var cts = _render = new CancellationTokenSource();
        var edit = Edit;
        try
        {
            await Task.Delay(30, cts.Token);
            var bitmap = await Task.Run(() =>
            {
                using var rendered = ScreenshotRenderer.Render(_source, edit, showCrop: false);
                cts.Token.ThrowIfCancellationRequested();
                return new Bitmap(PixelFormat.Rgba8888, AlphaFormat.Premul, rendered.GetPixels(),
                    new PixelSize(rendered.Width, rendered.Height), new Vector(96, 96), rendered.RowBytes);
            }, cts.Token);
            if (_render == cts)
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
        _render?.Cancel();
        _source.Dispose();
    }
}
