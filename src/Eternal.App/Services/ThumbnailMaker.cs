using SkiaSharp;

namespace Eternal.App.Services;

public static class ThumbnailMaker
{
    private const int Width = 640, Height = 360;
    private const long MaxPixels = 64L * 1024 * 1024;

    public static byte[]? FromFile(string path)
    {
        using var source = Decode(path, Width);
        if (source is null)
            return null;

        const float targetRatio = (float)Width / Height;
        var sourceRatio = (float)source.Width / source.Height;
        var crop = sourceRatio > targetRatio
            ? SKRectI.Create((int)((source.Width - source.Height * targetRatio) / 2), 0, (int)(source.Height * targetRatio), source.Height)
            : SKRectI.Create(0, (int)((source.Height - source.Width / targetRatio) / 2), source.Width, (int)(source.Width / targetRatio));

        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High };
        surface.Canvas.DrawBitmap(source, crop, new SKRect(0, 0, Width, Height), paint);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    public static SKBitmap? Decode(string path, int? minWidth = null)
    {
        using var codec = SKCodec.Create(path);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > MaxPixels)
            return null;

        var size = codec.Info.Size;
        if (minWidth is { } wanted && codec.Info.Width > wanted * 2)
            size = codec.GetScaledDimensions((float)wanted * 2 / codec.Info.Width);
        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput)
            return bitmap;
        bitmap.Dispose();
        return size == codec.Info.Size ? null : SKBitmap.Decode(path);
    }
}
