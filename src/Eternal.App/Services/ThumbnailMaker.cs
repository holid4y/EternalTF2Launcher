using SkiaSharp;

namespace Eternal.App.Services;

/// <summary>Turns any picture into a 640×360 card image (centre crop to 16:9).</summary>
public static class ThumbnailMaker
{
    public static byte[]? FromFile(string path)
    {
        using var source = SKBitmap.Decode(path);
        if (source is null)
            return null;

        const int width = 640, height = 360;
        var targetRatio = (float)width / height;
        var sourceRatio = (float)source.Width / source.Height;
        var crop = sourceRatio > targetRatio
            ? SKRectI.Create((int)((source.Width - source.Height * targetRatio) / 2), 0, (int)(source.Height * targetRatio), source.Height)
            : SKRectI.Create(0, (int)((source.Height - source.Width / targetRatio) / 2), source.Width, (int)(source.Width / targetRatio));

        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High };
        surface.Canvas.DrawBitmap(source, crop, new SKRect(0, 0, width, height), paint);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }
}
