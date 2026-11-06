using Eternal.App.Services;
using SkiaSharp;

namespace Eternal.Core.Tests;

public class ScreenshotEditorTests
{
    [Fact]
    public void Resizing_a_leftward_arrow_keeps_it_pointing_left()
    {
        var arrow = new SKRect(0.8f, 0.5f, 0.2f, 0.5f);
        var resized = ScreenshotRenderer.Resize(arrow, new SKPoint(-0.1f, 0f), arrow: true);
        Assert.True(resized.Right < resized.Left);
        Assert.Equal(0.8f, resized.Left);
        Assert.True(resized.Right is >= 0f and <= 1f);
    }

    [Fact]
    public void Resizing_an_upward_arrow_keeps_it_pointing_up()
    {
        var arrow = new SKRect(0.5f, 0.8f, 0.5f, 0.2f);
        var resized = ScreenshotRenderer.Resize(arrow, new SKPoint(0f, -0.1f), arrow: true);
        Assert.True(resized.Bottom < resized.Top);
        Assert.Equal(0.8f, resized.Top);
    }

    [Fact]
    public void A_text_mark_has_a_real_bounding_box_for_hit_and_handle_tests()
    {
        var mark = new Mark(MarkKind.Text, new SKRect(0.2f, 0.3f, 0.2f, 0.3f), SKColors.White, "hello");
        var bounds = ScreenshotRenderer.TextBounds(mark, 1600f, 900f);
        Assert.True(bounds.Width > 0f);
        Assert.True(bounds.Height > 0f);
        Assert.Equal(0.2f, bounds.Left);
        Assert.Equal(0.3f, bounds.Top);

        var marks = new List<Mark> { mark };
        Assert.Equal(0, ScreenshotRenderer.HitTest(marks, new SKPoint(bounds.Right - 0.001f, bounds.Bottom - 0.001f), 1600f, 900f));
        Assert.Equal(-1, ScreenshotRenderer.HitTest(marks, new SKPoint(0.9f, 0.9f), 1600f, 900f));
    }

    [Fact]
    public async Task A_local_picture_is_read_once_with_its_size()
    {
        var dir = Directory.CreateTempSubdirectory("eternal-img-");
        try
        {
            var path = Path.Combine(dir.FullName, "p.png");
            using (var bitmap = new SKBitmap(64, 32))
            using (var image = SKImage.FromBitmap(bitmap))
            using (var encoded = image.Encode(SKEncodedImageFormat.Png, 100))
                File.WriteAllBytes(path, encoded.ToArray());

            var (data, width, height) = ScreenshotRenderer.LoadImageFile(path);

            Assert.Equal((64, 32), (width, height));
            Assert.Equal(await File.ReadAllBytesAsync(path), data);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Resizing_a_box_keeps_a_minimum_size()
    {
        var box = new SKRect(0.2f, 0.2f, 0.5f, 0.5f);
        var resized = ScreenshotRenderer.Resize(box, new SKPoint(-0.9f, -0.9f));
        Assert.InRange(resized.Right, 0.205f, 0.215f);
        Assert.InRange(resized.Bottom, 0.205f, 0.215f);
        Assert.Equal(0.2f, resized.Left);
    }

    [Fact]
    public void Moving_an_arrow_keeps_both_ends_inside_the_picture()
    {
        var arrow = new SKRect(0.8f, 0.5f, 0.2f, 0.5f);
        var moved = ScreenshotRenderer.Translate(arrow, new SKPoint(-0.5f, 0f));
        Assert.InRange(moved.Left, 0f, 1f);
        Assert.InRange(moved.Right, 0f, 1f);
    }

    [Fact]
    public void Moving_a_box_stays_clamped_to_the_picture()
    {
        var box = new SKRect(0.2f, 0.2f, 0.5f, 0.5f);
        var moved = ScreenshotRenderer.Translate(box, new SKPoint(-0.5f, 0.5f));
        Assert.InRange(moved.Left, -0.001f, 0.001f);
        Assert.True(moved.Right is > 0.29f and < 0.31f);
        Assert.Equal(0.7f, moved.Top, 3);
        Assert.Equal(1f, moved.Bottom, 3);
    }
}
