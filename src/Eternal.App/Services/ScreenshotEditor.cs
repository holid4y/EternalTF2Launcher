using System.Collections.Immutable;
using SkiaSharp;

namespace Eternal.App.Services;

public enum MarkKind { Blur, Pixelate, Box, Arrow, Text, Image }

/// <summary>
/// One object on the picture. Area is normalized to the picture; an arrow keeps its start
/// in Left/Top and its head in Right/Bottom. Rotation is in degrees around the centre of Area.
/// </summary>
public sealed record Mark(MarkKind Kind, SKRect Area, SKColor Color, string? Text = null, SKImage? Picture = null, float Rotation = 0f, float Opacity = 1f)
{
    public bool CanRotate => Kind is MarkKind.Box or MarkKind.Text or MarkKind.Image;
    public bool UsesColor => Kind is MarkKind.Box or MarkKind.Arrow or MarkKind.Text;
}

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

    /// <summary><paramref name="width"/> and <paramref name="height"/> are the picture's size before the turn.</summary>
    public ImageEdit Turn(bool clockwise, float width, float height)
    {
        SKPoint Move(SKPoint p) => clockwise ? new SKPoint(1 - p.Y, p.X) : new SKPoint(p.Y, 1 - p.X);
        var turned = Transform(Move, m => m.Rotation);
        var marks = Marks.Select((old, i) =>
        {
            if (!old.CanRotate)
                return turned.Marks[i];
            // A mark that can rotate keeps its own size and turns with the picture; swapping its axes as well would turn it twice.
            var center = Move(new SKPoint(old.Area.MidX, old.Area.MidY));
            var (halfW, halfH) = (old.Area.Width * width / height / 2, old.Area.Height * height / width / 2);
            var area = new SKRect(center.X - halfW, center.Y - halfH, center.X + halfW, center.Y + halfH);
            return old with { Area = area, Rotation = MarkTransform.Normalize(old.Rotation + (clockwise ? 90f : -90f)) };
        }).ToImmutableList();
        return turned with { Rotation = (Rotation + (clockwise ? 90 : 270)) % 360, Marks = marks };
    }

    public ImageEdit Flip(bool horizontal)
    {
        SKPoint Move(SKPoint p) => horizontal ? new SKPoint(1 - p.X, p.Y) : new SKPoint(p.X, 1 - p.Y);
        var turned = Rotation % 180 != 0;
        var edit = Transform(Move, m => -m.Rotation);
        return horizontal != turned ? edit with { FlipH = !FlipH } : edit with { FlipV = !FlipV };
    }

    private ImageEdit Transform(Func<SKPoint, SKPoint> move, Func<Mark, float> rotate)
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
            Marks = Marks.Select(m => m with { Area = m.Kind == MarkKind.Arrow ? Line(m.Area) : Rect(m.Area), Rotation = rotate(m) }).ToImmutableList(),
        };
    }
}

public static class ScreenshotRenderer
{
    public const int MaxTextLength = 120;

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
                Draw(canvas, result, mark, w, h, unit);
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

    private static void Draw(SKCanvas canvas, SKBitmap image, Mark mark, int w, int h, int unit)
    {
        var r = new SKRect(mark.Area.Left * w, mark.Area.Top * h, mark.Area.Right * w, mark.Area.Bottom * h);
        var alpha = (byte)(Math.Clamp(mark.Opacity, 0f, 1f) * 255);
        canvas.Save();
        if (mark.CanRotate)
            canvas.RotateDegrees(mark.Rotation, r.MidX, r.MidY);
        switch (mark.Kind)
        {
            case MarkKind.Blur:
                Blur(canvas, image, Clamp(r, w, h), unit);
                break;
            case MarkKind.Pixelate:
                Pixelate(canvas, image, Clamp(r, w, h), unit);
                break;
            case MarkKind.Box:
                Box(canvas, r, mark.Color.WithAlpha(alpha), unit);
                break;
            case MarkKind.Arrow:
                Arrow(canvas, new SKPoint(r.Left, r.Top), new SKPoint(r.Right, r.Bottom), mark.Color.WithAlpha(alpha), unit);
                break;
            case MarkKind.Text when !string.IsNullOrWhiteSpace(mark.Text):
                Text(canvas, mark.Text, r, mark.Color.WithAlpha(alpha));
                break;
            case MarkKind.Image when mark.Picture is { } picture:
                using (var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium, IsAntialias = true, Color = SKColors.White.WithAlpha(alpha) })
                    canvas.DrawImage(picture, r, paint);
                break;
        }
        canvas.Restore();
    }

    public static byte[] Encode(SKBitmap bitmap, bool png)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = png ? image.Encode(SKEncodedImageFormat.Png, 100) : image.Encode(SKEncodedImageFormat.Jpeg, 95);
        return data.ToArray();
    }

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

    private const float TextBoxHeight = 1.3f;

    private static float Width(SKFont font, string text)
    {
        using var paint = new SKPaint(font);
        return paint.MeasureText(text);
    }

    public static float TextSize(float height) => Math.Max(14f, height * 0.045f);

    /// <summary>The area a new text takes up at <paramref name="at"/>: measured with the font it will be drawn in.</summary>
    public static SKRect TextArea(string text, SKPoint at, float width, float height)
    {
        using var typeface = SKTypeface.FromFamilyName(null, SKFontStyle.Bold);
        using var font = new SKFont(typeface, TextSize(height));
        var w = Width(font, text) / width;
        var h = font.Size * TextBoxHeight / height;
        var left = Math.Clamp(at.X, 0f, Math.Max(0f, 1f - w));
        var top = Math.Clamp(at.Y, 0f, Math.Max(0f, 1f - h));
        return new SKRect(left, top, left + w, top + h);
    }

    public const int MaxImageFileBytes = 16 * 1024 * 1024;
    public const int MaxImageDimension = 4096;
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"];

    public static SKImage LoadImageFile(string path)
    {
        var extension = Path.GetExtension(path);
        if (!ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"{Path.GetFileName(path)} isn't a PNG, JPG, WebP, BMP or GIF picture.");
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new InvalidDataException("That file doesn't exist anymore.");
        if (info.Length > MaxImageFileBytes)
            throw new InvalidDataException($"{Path.GetFileName(path)} is larger than {MaxImageFileBytes / (1024 * 1024)} MB.");
        var data = File.ReadAllBytes(path);
        using var decoded = SKBitmap.Decode(data);
        if (decoded is null)
            throw new InvalidDataException($"{Path.GetFileName(path)} couldn't be read as a picture.");
        if (decoded.Width > MaxImageDimension || decoded.Height > MaxImageDimension)
            throw new InvalidDataException($"Pictures up to {MaxImageDimension}×{MaxImageDimension} pixels only.");
        return SKImage.FromBitmap(decoded);
    }

    private static void Text(SKCanvas canvas, string text, SKRect box, SKColor color)
    {
        if (text.Length > MaxTextLength)
            text = text[..MaxTextLength];
        using var typeface = SKTypeface.FromFamilyName(null, SKFontStyle.Bold);
        using var font = new SKFont(typeface, Math.Max(1f, box.Height / TextBoxHeight));
        using var outline = new SKPaint { Color = SKColors.Black.WithAlpha((byte)(color.Alpha * 200 / 255)), IsStroke = true, StrokeWidth = font.Size * 0.14f, IsAntialias = true, StrokeJoin = SKStrokeJoin.Round };
        using var fill = new SKPaint { Color = color, IsAntialias = true };
        var measured = Width(font, text);
        canvas.Save();
        if (measured > 0 && box.Width > 0)
        {
            canvas.Translate(box.Left, box.Top);
            canvas.Scale(box.Width / measured, 1f);
            canvas.Translate(-box.Left, -box.Top);
        }
        var baseline = box.Top + font.Size;
        canvas.DrawText(text, box.Left, baseline, font, outline);
        canvas.DrawText(text, box.Left, baseline, font, fill);
        canvas.Restore();
    }
}

public enum MarkHandle { None, Body, Rotate, North, South, East, West, NorthWest, NorthEast, SouthWest, SouthEast, Start, End }

/// <summary>Where a mark's handles are and what dragging them does. Works in picture pixels, because a rotation only stays square there.</summary>
public static class MarkTransform
{
    private const float MinSize = 8f;
    private const float RotateSnap = 15f;
    public const float RotateOffset = 26f;

    private static SKPoint Rotated(SKPoint p, float degrees)
    {
        var radians = degrees * MathF.PI / 180f;
        var (sin, cos) = (MathF.Sin(radians), MathF.Cos(radians));
        return new SKPoint(p.X * cos - p.Y * sin, p.X * sin + p.Y * cos);
    }

    private static SKRect Pixels(SKRect area, float w, float h) => new(area.Left * w, area.Top * h, area.Right * w, area.Bottom * h);

    private static SKPoint Pixels(SKPoint p, float w, float h) => new(p.X * w, p.Y * h);

    private static SKPoint Local(Mark m, SKPoint pixel, SKRect box)
    {
        var center = new SKPoint(box.MidX, box.MidY);
        var rotation = m.CanRotate ? m.Rotation : 0f;
        var local = Rotated(new SKPoint(pixel.X - center.X, pixel.Y - center.Y), -rotation);
        return new SKPoint(local.X + center.X, local.Y + center.Y);
    }

    private static bool OnlyCorners(Mark m) => m.Kind == MarkKind.Text;

    /// <summary>Every handle of a mark, in picture pixels. Rotate sits above the top edge by <paramref name="rotateOffset"/>.</summary>
    public static IReadOnlyList<(MarkHandle Handle, SKPoint At)> Handles(Mark m, float w, float h, float rotateOffset)
    {
        if (m.Kind == MarkKind.Arrow)
            return [(MarkHandle.Start, new SKPoint(m.Area.Left * w, m.Area.Top * h)), (MarkHandle.End, new SKPoint(m.Area.Right * w, m.Area.Bottom * h))];

        var box = Pixels(m.Area, w, h);
        var center = new SKPoint(box.MidX, box.MidY);
        SKPoint Place(float x, float y)
        {
            var p = Rotated(new SKPoint(x - center.X, y - center.Y), m.CanRotate ? m.Rotation : 0f);
            return new SKPoint(p.X + center.X, p.Y + center.Y);
        }

        var list = new List<(MarkHandle, SKPoint)>
        {
            (MarkHandle.NorthWest, Place(box.Left, box.Top)), (MarkHandle.NorthEast, Place(box.Right, box.Top)),
            (MarkHandle.SouthWest, Place(box.Left, box.Bottom)), (MarkHandle.SouthEast, Place(box.Right, box.Bottom)),
        };
        if (!OnlyCorners(m))
        {
            list.Add((MarkHandle.North, Place(box.MidX, box.Top)));
            list.Add((MarkHandle.South, Place(box.MidX, box.Bottom)));
            list.Add((MarkHandle.West, Place(box.Left, box.MidY)));
            list.Add((MarkHandle.East, Place(box.Right, box.MidY)));
        }
        if (m.CanRotate)
            list.Add((MarkHandle.Rotate, Place(box.MidX, box.Top - rotateOffset)));
        return list;
    }

    public static MarkHandle HandleAt(Mark m, SKPoint normalized, float w, float h, float tolerance, float rotateOffset)
    {
        var pixel = Pixels(normalized, w, h);
        foreach (var (handle, at) in Handles(m, w, h, rotateOffset))
        {
            if (SKPoint.Distance(at, pixel) <= tolerance)
                return handle;
        }
        return Contains(m, pixel, w, h, tolerance / 2) ? MarkHandle.Body : MarkHandle.None;
    }

    public static int HitTest(IReadOnlyList<Mark> marks, SKPoint normalized, float w, float h, float tolerance)
    {
        var pixel = Pixels(normalized, w, h);
        for (var i = marks.Count - 1; i >= 0; i--)
        {
            if (Contains(marks[i], pixel, w, h, tolerance))
                return i;
        }
        return -1;
    }

    private static bool Contains(Mark m, SKPoint pixel, float w, float h, float tolerance)
    {
        if (m.Kind == MarkKind.Arrow)
            return DistanceToSegment(pixel, new SKPoint(m.Area.Left * w, m.Area.Top * h), new SKPoint(m.Area.Right * w, m.Area.Bottom * h)) <= tolerance;
        var box = Pixels(m.Area, w, h);
        var local = Local(m, pixel, box);
        return local.X >= box.Left - tolerance && local.X <= box.Right + tolerance && local.Y >= box.Top - tolerance && local.Y <= box.Bottom + tolerance;
    }

    /// <summary>The mark after dragging <paramref name="handle"/> from <paramref name="from"/> to <paramref name="to"/>, both normalized, starting from how it was when the drag began.</summary>
    public static Mark Drag(Mark start, MarkHandle handle, SKPoint from, SKPoint to, float w, float h, bool constrain)
    {
        var fromPx = Pixels(from, w, h);
        var toPx = Pixels(to, w, h);
        return handle switch
        {
            MarkHandle.Body => start with { Area = Moved(start, new SKPoint(toPx.X - fromPx.X, toPx.Y - fromPx.Y), w, h, constrain) },
            MarkHandle.Rotate => start with { Rotation = Rotation(start, fromPx, toPx, w, h, constrain) },
            MarkHandle.Start or MarkHandle.End => start with { Area = ArrowEnd(start.Area, handle, to, w, h, constrain) },
            MarkHandle.None => start,
            _ => start with { Area = Resized(start, handle, toPx, w, h, constrain) },
        };
    }

    private static SKRect Moved(Mark m, SKPoint delta, float w, float h, bool constrain)
    {
        if (constrain)
            delta = Math.Abs(delta.X) >= Math.Abs(delta.Y) ? new SKPoint(delta.X, 0) : new SKPoint(0, delta.Y);
        var (minX, minY, maxX, maxY) = Extents(m, w, h);
        var dx = Math.Clamp(delta.X, Math.Min(0f, -minX), Math.Max(0f, w - maxX));
        var dy = Math.Clamp(delta.Y, Math.Min(0f, -minY), Math.Max(0f, h - maxY));
        return new SKRect(m.Area.Left + dx / w, m.Area.Top + dy / h, m.Area.Right + dx / w, m.Area.Bottom + dy / h);
    }

    private static (float MinX, float MinY, float MaxX, float MaxY) Extents(Mark m, float w, float h)
    {
        if (m.Kind == MarkKind.Arrow)
        {
            return (Math.Min(m.Area.Left, m.Area.Right) * w, Math.Min(m.Area.Top, m.Area.Bottom) * h,
                Math.Max(m.Area.Left, m.Area.Right) * w, Math.Max(m.Area.Top, m.Area.Bottom) * h);
        }
        var corners = Handles(m with { Kind = m.Kind }, w, h, 0).Where(x => x.Handle is MarkHandle.NorthWest or MarkHandle.NorthEast or MarkHandle.SouthWest or MarkHandle.SouthEast).Select(x => x.At).ToList();
        return (corners.Min(c => c.X), corners.Min(c => c.Y), corners.Max(c => c.X), corners.Max(c => c.Y));
    }

    private static float Rotation(Mark m, SKPoint from, SKPoint to, float w, float h, bool snap)
    {
        var box = Pixels(m.Area, w, h);
        var angleFrom = MathF.Atan2(from.Y - box.MidY, from.X - box.MidX);
        var angleTo = MathF.Atan2(to.Y - box.MidY, to.X - box.MidX);
        var degrees = m.Rotation + (angleTo - angleFrom) * 180f / MathF.PI;
        if (snap)
            degrees = MathF.Round(degrees / RotateSnap) * RotateSnap;
        return Normalize(degrees);
    }

    public static float Normalize(float degrees)
    {
        degrees %= 360f;
        return degrees switch { > 180f => degrees - 360f, <= -180f => degrees + 360f, _ => degrees };
    }

    private static SKRect ArrowEnd(SKRect line, MarkHandle handle, SKPoint to, float w, float h, bool snap)
    {
        var point = new SKPoint(Math.Clamp(to.X, 0f, 1f), Math.Clamp(to.Y, 0f, 1f));
        if (snap)
        {
            var anchor = handle == MarkHandle.Start ? new SKPoint(line.Right * w, line.Bottom * h) : new SKPoint(line.Left * w, line.Top * h);
            var delta = new SKPoint(point.X * w - anchor.X, point.Y * h - anchor.Y);
            var length = delta.Length;
            var angle = MathF.Round(MathF.Atan2(delta.Y, delta.X) / (MathF.PI / 4)) * (MathF.PI / 4);
            point = new SKPoint(Math.Clamp((anchor.X + MathF.Cos(angle) * length) / w, 0f, 1f), Math.Clamp((anchor.Y + MathF.Sin(angle) * length) / h, 0f, 1f));
        }
        return handle == MarkHandle.Start ? new SKRect(point.X, point.Y, line.Right, line.Bottom) : new SKRect(line.Left, line.Top, point.X, point.Y);
    }

    /// <summary>Moves one edge or corner while the opposite one stays where it is on screen, even when the mark is rotated.</summary>
    private static SKRect Resized(Mark m, MarkHandle handle, SKPoint pointer, float w, float h, bool constrain)
    {
        var box = Pixels(m.Area, w, h);
        var center = new SKPoint(box.MidX, box.MidY);
        var rotation = m.CanRotate ? m.Rotation : 0f;
        var local = Rotated(new SKPoint(pointer.X - center.X, pointer.Y - center.Y), -rotation);

        var movesLeft = handle is MarkHandle.West or MarkHandle.NorthWest or MarkHandle.SouthWest;
        var movesRight = handle is MarkHandle.East or MarkHandle.NorthEast or MarkHandle.SouthEast;
        var movesTop = handle is MarkHandle.North or MarkHandle.NorthWest or MarkHandle.NorthEast;
        var movesBottom = handle is MarkHandle.South or MarkHandle.SouthWest or MarkHandle.SouthEast;

        var (halfW, halfH) = (box.Width / 2, box.Height / 2);
        var left = movesLeft ? Math.Min(local.X, halfW - MinSize) : -halfW;
        var right = movesRight ? Math.Max(local.X, -halfW + MinSize) : halfW;
        var top = movesTop ? Math.Min(local.Y, halfH - MinSize) : -halfH;
        var bottom = movesBottom ? Math.Max(local.Y, -halfH + MinSize) : halfH;

        var isCorner = (movesLeft || movesRight) && (movesTop || movesBottom);
        var keepShape = isCorner && (OnlyCorners(m) || (m.Kind == MarkKind.Image) != constrain);
        if (keepShape && box.Width > 0 && box.Height > 0)
        {
            var scale = Math.Max((right - left) / box.Width, (bottom - top) / box.Height);
            var (newW, newH) = (box.Width * scale, box.Height * scale);
            if (movesLeft) left = right - newW; else right = left + newW;
            if (movesTop) top = bottom - newH; else bottom = top + newH;
        }

        var anchorLocal = new SKPoint(movesLeft ? halfW : movesRight ? -halfW : 0, movesTop ? halfH : movesBottom ? -halfH : 0);
        var anchor = Rotated(anchorLocal, rotation);
        var (width, height) = (right - left, bottom - top);
        var newAnchorLocal = new SKPoint(movesLeft ? width / 2 : movesRight ? -width / 2 : 0, movesTop ? height / 2 : movesBottom ? -height / 2 : 0);
        var newAnchor = Rotated(newAnchorLocal, rotation);
        var newCenter = new SKPoint(center.X + anchor.X - newAnchor.X, center.Y + anchor.Y - newAnchor.Y);
        return new SKRect((newCenter.X - width / 2) / w, (newCenter.Y - height / 2) / h, (newCenter.X + width / 2) / w, (newCenter.Y + height / 2) / h);
    }

    private static float DistanceToSegment(SKPoint p, SKPoint a, SKPoint b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len2 = dx * dx + dy * dy;
        var t = len2 == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0f, 1f);
        var x = a.X + t * dx - p.X;
        var y = a.Y + t * dy - p.Y;
        return MathF.Sqrt(x * x + y * y);
    }
}
