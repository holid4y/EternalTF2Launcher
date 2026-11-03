using System.Collections.Immutable;
using SkiaSharp;

namespace Eternal.App.Services;

public enum MarkKind { Blur, Pixelate, Box, Arrow, Text }

/// <summary>
/// Something drawn on the picture. Positions are fractions of the turned image
/// (0 to 1), so they survive any size. An arrow runs from (Left, Top) to (Right, Bottom).
/// </summary>
public sealed record Mark(MarkKind Kind, SKRect Area, SKColor Color, string? Text = null);

/// <summary>
/// Every change made in the editor, kept apart from the picture until it is
/// saved: turn and flip first, then light, then marks, then the crop.
/// </summary>
public sealed record ImageEdit
{
    public int Rotation { get; init; }
    public bool FlipH { get; init; }
    public bool FlipV { get; init; }
    public SKRect? Crop { get; init; }
    public float Brightness { get; init; }
    public float Contrast { get; init; }
    public float Saturation { get; init; }
    public ImmutableList<Mark> Marks { get; init; } = [];

    public bool IsEmpty => Rotation == 0 && !FlipH && !FlipV && Crop is null && Brightness == 0 && Contrast == 0 && Saturation == 0 && Marks.IsEmpty;

    /// <summary>A quarter turn clockwise (or back), carrying the crop and marks along.</summary>
    public ImageEdit Turn(bool clockwise)
    {
        SKPoint Move(SKPoint p) => clockwise ? new SKPoint(1 - p.Y, p.X) : new SKPoint(p.Y, 1 - p.X);
        return Transform(Move) with { Rotation = (Rotation + (clockwise ? 90 : 270)) % 360 };
    }

    public ImageEdit Flip(bool horizontal)
    {
        SKPoint Move(SKPoint p) => horizontal ? new SKPoint(1 - p.X, p.Y) : new SKPoint(p.X, 1 - p.Y);
        var turned = Rotation % 180 != 0;
        var edit = Transform(Move);
        return horizontal != turned ? edit with { FlipH = !FlipH } : edit with { FlipV = !FlipV };
    }

    private ImageEdit Transform(Func<SKPoint, SKPoint> move)
    {
        SKRect Rect(SKRect r)
        {
            var a = move(new SKPoint(r.Left, r.Top));
            var b = move(new SKPoint(r.Right, r.Bottom));
            return new SKRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }
        SKRect Line(SKRect r)
        {
            var a = move(new SKPoint(r.Left, r.Top));
            var b = move(new SKPoint(r.Right, r.Bottom));
            return new SKRect(a.X, a.Y, b.X, b.Y);
        }
        return this with
        {
            Crop = Crop is { } c ? Rect(c) : null,
            Marks = Marks.Select(m => m with { Area = m.Kind == MarkKind.Arrow ? Line(m.Area) : Rect(m.Area) }).ToImmutableList(),
        };
    }
}

public static class ScreenshotRenderer
{
    public const int MaxTextLength = 120;

    /// <summary>The finished picture: the source with every edit applied.</summary>
    public static SKBitmap Render(SKBitmap source, ImageEdit edit, bool showCrop = true)
    {
        using var oriented = Orient(source, edit);
        var w = oriented.Width;
        var h = oriented.Height;
        var result = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(result))
        {
            using (var paint = new SKPaint { ColorFilter = Adjust(edit) })
                canvas.DrawBitmap(oriented, 0, 0, paint);
            var unit = Math.Max(w, h);
            foreach (var mark in edit.Marks)
            {
                var r = new SKRect(mark.Area.Left * w, mark.Area.Top * h, mark.Area.Right * w, mark.Area.Bottom * h);
                switch (mark.Kind)
                {
                    case MarkKind.Blur:
                        Blur(canvas, result, Clamp(r, w, h), unit);
                        break;
                    case MarkKind.Pixelate:
                        Pixelate(canvas, result, Clamp(r, w, h), unit);
                        break;
                    case MarkKind.Box:
                        Box(canvas, r, mark.Color, unit);
                        break;
                    case MarkKind.Arrow:
                        Arrow(canvas, new SKPoint(r.Left, r.Top), new SKPoint(r.Right, r.Bottom), mark.Color, unit);
                        break;
                    case MarkKind.Text when !string.IsNullOrWhiteSpace(mark.Text):
                        Text(canvas, mark.Text, new SKPoint(r.Left, r.Top), mark.Color, h);
                        break;
                }
            }
        }
        if (!showCrop || edit.Crop is not { } crop)
            return result;

        var area = Clamp(new SKRect(crop.Left * w, crop.Top * h, crop.Right * w, crop.Bottom * h), w, h);
        var bounds = SKRectI.Round(area);
        if (bounds.Width < 2 || bounds.Height < 2)
            return result;
        var cropped = new SKBitmap(bounds.Width, bounds.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(cropped))
            canvas.DrawBitmap(result, bounds, new SKRect(0, 0, bounds.Width, bounds.Height));
        result.Dispose();
        return cropped;
    }

    public static byte[] Encode(SKBitmap bitmap, bool png)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = png ? image.Encode(SKEncodedImageFormat.Png, 100) : image.Encode(SKEncodedImageFormat.Jpeg, 95);
        return data.ToArray();
    }

    /// <summary>Steam's own thumbnails are JPEGs 200 pixels wide.</summary>
    public static byte[] Thumbnail(SKBitmap bitmap, int width)
    {
        var height = Math.Max(1, (int)Math.Round((double)bitmap.Height * width / bitmap.Width));
        using var small = bitmap.Resize(new SKImageInfo(width, height), SKFilterQuality.High);
        using var image = SKImage.FromBitmap(small);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private static SKBitmap Orient(SKBitmap source, ImageEdit edit)
    {
        var turned = edit.Rotation % 180 != 0;
        var w = turned ? source.Height : source.Width;
        var h = turned ? source.Width : source.Height;
        var oriented = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(oriented);
        canvas.Translate(w / 2f, h / 2f);
        canvas.RotateDegrees(edit.Rotation);
        canvas.Scale(edit.FlipH ? -1 : 1, edit.FlipV ? -1 : 1);
        canvas.Translate(-source.Width / 2f, -source.Height / 2f);
        canvas.DrawBitmap(source, 0, 0);
        return oriented;
    }

    private static SKColorFilter? Adjust(ImageEdit edit)
    {
        if (edit.Brightness == 0 && edit.Contrast == 0 && edit.Saturation == 0)
            return null;
        var s = 1 + Math.Clamp(edit.Saturation, -1, 1);
        const float lr = 0.2126f, lg = 0.7152f, lb = 0.0722f;
        float[] saturation =
        [
            lr * (1 - s) + s, lg * (1 - s), lb * (1 - s), 0, 0,
            lr * (1 - s), lg * (1 - s) + s, lb * (1 - s), 0, 0,
            lr * (1 - s), lg * (1 - s), lb * (1 - s) + s, 0, 0,
            0, 0, 0, 1, 0,
        ];
        var c = 1 + Math.Clamp(edit.Contrast, -0.9f, 1);
        var offset = 0.5f * (1 - c) + Math.Clamp(edit.Brightness, -0.5f, 0.5f);
        float[] light =
        [
            c, 0, 0, 0, offset,
            0, c, 0, 0, offset,
            0, 0, c, 0, offset,
            0, 0, 0, 1, 0,
        ];
        using var inner = SKColorFilter.CreateColorMatrix(saturation);
        using var outer = SKColorFilter.CreateColorMatrix(light);
        return SKColorFilter.CreateCompose(outer, inner);
    }

    private static SKRect Clamp(SKRect r, int w, int h) =>
        new(Math.Clamp(r.Left, 0, w), Math.Clamp(r.Top, 0, h), Math.Clamp(r.Right, 0, w), Math.Clamp(r.Bottom, 0, h));

    private static void Blur(SKCanvas canvas, SKBitmap image, SKRect area, int unit)
    {
        if (area.Width < 1 || area.Height < 1)
            return;
        var sigma = Math.Max(6f, unit * 0.012f);
        using var snapshot = SKImage.FromBitmap(image);
        using var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp) };
        canvas.Save();
        canvas.ClipRect(area);
        canvas.DrawImage(snapshot, 0, 0, paint);
        canvas.Restore();
    }

    private static void Pixelate(SKCanvas canvas, SKBitmap image, SKRect area, int unit)
    {
        var bounds = SKRectI.Round(area);
        if (bounds.Width < 2 || bounds.Height < 2)
            return;
        var block = Math.Max(6, unit / 90);
        var info = new SKImageInfo(Math.Max(1, bounds.Width / block), Math.Max(1, bounds.Height / block));
        using var small = new SKBitmap(info);
        using (var shrink = new SKCanvas(small))
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium })
            shrink.DrawBitmap(image, bounds, new SKRect(0, 0, info.Width, info.Height), paint);
        using var blocky = new SKPaint { FilterQuality = SKFilterQuality.None };
        canvas.DrawBitmap(small, new SKRect(0, 0, info.Width, info.Height), area, blocky);
    }

    private static SKPaint Stroke(SKColor color, int unit) => new()
    {
        Color = color,
        IsStroke = true,
        StrokeWidth = Math.Max(2f, unit * 0.004f),
        IsAntialias = true,
        StrokeCap = SKStrokeCap.Round,
        StrokeJoin = SKStrokeJoin.Round,
    };

    private static void Box(SKCanvas canvas, SKRect r, SKColor color, int unit)
    {
        using var paint = Stroke(color, unit);
        canvas.DrawRect(r, paint);
    }

    private static void Arrow(SKCanvas canvas, SKPoint from, SKPoint to, SKColor color, int unit)
    {
        using var paint = Stroke(color, unit);
        canvas.DrawLine(from, to, paint);
        var angle = Math.Atan2(to.Y - from.Y, to.X - from.X);
        var head = paint.StrokeWidth * 5;
        using var path = new SKPath();
        path.MoveTo(to);
        path.LineTo(to.X - head * (float)Math.Cos(angle - 0.45), to.Y - head * (float)Math.Sin(angle - 0.45));
        path.MoveTo(to);
        path.LineTo(to.X - head * (float)Math.Cos(angle + 0.45), to.Y - head * (float)Math.Sin(angle + 0.45));
        canvas.DrawPath(path, paint);
    }

    private static void Text(SKCanvas canvas, string text, SKPoint at, SKColor color, int height)
    {
        if (text.Length > MaxTextLength)
            text = text[..MaxTextLength];
        using var typeface = SKTypeface.FromFamilyName(null, SKFontStyle.Bold);
        using var font = new SKFont(typeface, Math.Max(14f, height * 0.045f));
        using var outline = new SKPaint { Color = SKColors.Black.WithAlpha(200), IsStroke = true, StrokeWidth = font.Size * 0.14f, IsAntialias = true, StrokeJoin = SKStrokeJoin.Round };
        using var fill = new SKPaint { Color = color, IsAntialias = true };
        var baseline = new SKPoint(at.X, at.Y + font.Size);
        canvas.DrawText(text, baseline.X, baseline.Y, font, outline);
        canvas.DrawText(text, baseline.X, baseline.Y, font, fill);
    }
}
