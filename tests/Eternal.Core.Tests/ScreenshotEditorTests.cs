using Eternal.App.Services;
using SkiaSharp;

namespace Eternal.Core.Tests;

public class ScreenshotEditorTests
{
    private const float W = 1000f, H = 500f;

    private static Mark Box(float left, float top, float right, float bottom, float rotation = 0) =>
        new(MarkKind.Box, new SKRect(left, top, right, bottom), SKColors.White, Rotation: rotation);

    [Fact]
    public void Dragging_the_body_moves_the_mark()
    {
        var moved = MarkTransform.Drag(Box(0.2f, 0.2f, 0.4f, 0.4f), MarkHandle.Body, new SKPoint(0.3f, 0.3f), new SKPoint(0.5f, 0.4f), W, H, false);

        Assert.Equal(0.4f, moved.Area.Left, 3);
        Assert.Equal(0.3f, moved.Area.Top, 3);
        Assert.Equal(0.2f, moved.Area.Width, 3);
    }

    [Fact]
    public void A_mark_stays_inside_the_picture_when_dragged_past_the_edge()
    {
        var moved = MarkTransform.Drag(Box(0.2f, 0.2f, 0.4f, 0.4f), MarkHandle.Body, new SKPoint(0.3f, 0.3f), new SKPoint(-2f, 3f), W, H, false);

        Assert.Equal(0f, moved.Area.Left, 3);
        Assert.Equal(1f, moved.Area.Bottom, 3);
    }

    [Fact]
    public void Shift_keeps_a_move_on_one_axis()
    {
        var moved = MarkTransform.Drag(Box(0.2f, 0.2f, 0.4f, 0.4f), MarkHandle.Body, new SKPoint(0.3f, 0.3f), new SKPoint(0.5f, 0.32f), W, H, true);

        Assert.Equal(0.2f, moved.Area.Top, 3);
    }

    [Fact]
    public void Dragging_a_corner_leaves_the_opposite_corner_in_place()
    {
        var resized = MarkTransform.Drag(Box(0.2f, 0.2f, 0.4f, 0.4f), MarkHandle.SouthEast, new SKPoint(0.4f, 0.4f), new SKPoint(0.6f, 0.7f), W, H, false);

        Assert.Equal(0.2f, resized.Area.Left, 3);
        Assert.Equal(0.2f, resized.Area.Top, 3);
        Assert.Equal(0.6f, resized.Area.Right, 3);
        Assert.Equal(0.7f, resized.Area.Bottom, 3);
    }

    [Fact]
    public void Dragging_an_edge_changes_only_that_side()
    {
        var resized = MarkTransform.Drag(Box(0.2f, 0.2f, 0.4f, 0.4f), MarkHandle.West, new SKPoint(0.2f, 0.3f), new SKPoint(0.1f, 0.9f), W, H, false);

        Assert.Equal(0.1f, resized.Area.Left, 3);
        Assert.Equal(0.4f, resized.Area.Right, 3);
        Assert.Equal(0.2f, resized.Area.Top, 3);
        Assert.Equal(0.4f, resized.Area.Bottom, 3);
    }

    [Fact]
    public void A_mark_cannot_be_turned_inside_out()
    {
        var resized = MarkTransform.Drag(Box(0.2f, 0.2f, 0.4f, 0.4f), MarkHandle.SouthEast, new SKPoint(0.4f, 0.4f), new SKPoint(0f, 0f), W, H, false);

        Assert.True(resized.Area.Width > 0);
        Assert.True(resized.Area.Height > 0);
    }

    [Fact]
    public void A_picture_keeps_its_shape_unless_shift_is_held()
    {
        var picture = new Mark(MarkKind.Image, new SKRect(0.2f, 0.2f, 0.4f, 0.6f), SKColors.White);
        var aspect = picture.Area.Width * W / (picture.Area.Height * H);

        var kept = MarkTransform.Drag(picture, MarkHandle.SouthEast, new SKPoint(0.4f, 0.6f), new SKPoint(0.8f, 0.7f), W, H, false);
        var free = MarkTransform.Drag(picture, MarkHandle.SouthEast, new SKPoint(0.4f, 0.6f), new SKPoint(0.8f, 0.7f), W, H, true);

        Assert.Equal(aspect, kept.Area.Width * W / (kept.Area.Height * H), 2);
        Assert.NotEqual(aspect, free.Area.Width * W / (free.Area.Height * H), 2);
    }

    [Fact]
    public void Resizing_a_rotated_mark_keeps_the_far_corner_where_it_was_on_screen()
    {
        var box = Box(0.4f, 0.4f, 0.6f, 0.6f, 40);
        var before = MarkTransform.Handles(box, W, H, 0).First(x => x.Handle == MarkHandle.NorthWest).At;
        var grabbed = MarkTransform.Handles(box, W, H, 0).First(x => x.Handle == MarkHandle.SouthEast).At;

        var resized = MarkTransform.Drag(box, MarkHandle.SouthEast, new SKPoint(grabbed.X / W, grabbed.Y / H), new SKPoint(grabbed.X / W + 0.1f, grabbed.Y / H + 0.05f), W, H, false);

        var after = MarkTransform.Handles(resized, W, H, 0).First(x => x.Handle == MarkHandle.NorthWest).At;
        Assert.Equal(before.X, after.X, 1);
        Assert.Equal(before.Y, after.Y, 1);
    }

    [Fact]
    public void The_rotate_handle_turns_around_the_centre()
    {
        var box = Box(0.4f, 0.4f, 0.6f, 0.6f);
        var center = new SKPoint(0.5f, 0.5f);

        var turned = MarkTransform.Drag(box, MarkHandle.Rotate, new SKPoint(center.X, 0.2f), new SKPoint(0.5f + 300f / W, 0.5f), W, H, false);

        Assert.Equal(90f, turned.Rotation, 0);
    }

    [Fact]
    public void Shift_snaps_a_turn_to_steps()
    {
        var box = Box(0.4f, 0.4f, 0.6f, 0.6f);

        var turned = MarkTransform.Drag(box, MarkHandle.Rotate, new SKPoint(0.5f, 0.2f), new SKPoint(0.51f, 0.2f), W, H, true);

        Assert.Equal(0f, turned.Rotation % 15f, 2);
    }

    [Fact]
    public void An_arrow_end_moves_without_flipping_the_other_end()
    {
        var arrow = new Mark(MarkKind.Arrow, new SKRect(0.8f, 0.5f, 0.2f, 0.5f), SKColors.White);

        var moved = MarkTransform.Drag(arrow, MarkHandle.End, new SKPoint(0.2f, 0.5f), new SKPoint(0.1f, 0.7f), W, H, false);

        Assert.Equal(0.8f, moved.Area.Left, 3);
        Assert.Equal(0.5f, moved.Area.Top, 3);
        Assert.Equal(0.1f, moved.Area.Right, 3);
        Assert.Equal(0.7f, moved.Area.Bottom, 3);
    }

    [Fact]
    public void Moving_an_arrow_keeps_both_ends_inside_the_picture()
    {
        var arrow = new Mark(MarkKind.Arrow, new SKRect(0.8f, 0.5f, 0.2f, 0.5f), SKColors.White);

        var moved = MarkTransform.Drag(arrow, MarkHandle.Body, new SKPoint(0.5f, 0.5f), new SKPoint(-1f, 0.5f), W, H, false);

        Assert.InRange(moved.Area.Left, 0f, 1f);
        Assert.InRange(moved.Area.Right, 0f, 1f);
    }

    [Fact]
    public void A_rotated_mark_is_hit_where_it_is_drawn()
    {
        var marks = new List<Mark> { Box(0.45f, 0.1f, 0.55f, 0.9f, 90) };

        Assert.Equal(0, MarkTransform.HitTest(marks, new SKPoint(0.65f, 0.5f), W, H, 1f));
        Assert.Equal(-1, MarkTransform.HitTest(marks, new SKPoint(0.5f, 0.15f), W, H, 1f));
    }

    [Fact]
    public void The_topmost_mark_wins_a_click()
    {
        var marks = new List<Mark> { Box(0.1f, 0.1f, 0.5f, 0.5f), Box(0.2f, 0.2f, 0.4f, 0.4f) };

        Assert.Equal(1, MarkTransform.HitTest(marks, new SKPoint(0.3f, 0.3f), W, H, 1f));
    }

    [Fact]
    public void Handles_are_found_before_the_body()
    {
        var box = Box(0.2f, 0.2f, 0.4f, 0.4f);

        Assert.Equal(MarkHandle.SouthEast, MarkTransform.HandleAt(box, new SKPoint(0.4f, 0.4f), W, H, 8f, 20f));
        Assert.Equal(MarkHandle.Body, MarkTransform.HandleAt(box, new SKPoint(0.3f, 0.3f), W, H, 8f, 20f));
        Assert.Equal(MarkHandle.None, MarkTransform.HandleAt(box, new SKPoint(0.8f, 0.8f), W, H, 8f, 20f));
    }

    [Fact]
    public void Text_only_has_corner_handles_and_a_measured_box()
    {
        var area = ScreenshotRenderer.TextArea("hello", new SKPoint(0.2f, 0.3f), 1600f, 900f);
        var mark = new Mark(MarkKind.Text, area, SKColors.White, "hello");

        Assert.True(area.Width > 0);
        Assert.True(area.Height > 0);
        Assert.DoesNotContain(MarkTransform.Handles(mark, 1600f, 900f, 20f), x => x.Handle is MarkHandle.North or MarkHandle.East);
    }

    [Fact]
    public void A_wider_text_takes_a_wider_box()
    {
        var narrow = ScreenshotRenderer.TextArea("iii", new SKPoint(0.1f, 0.1f), 1600f, 900f);
        var wide = ScreenshotRenderer.TextArea("WWW", new SKPoint(0.1f, 0.1f), 1600f, 900f);

        Assert.True(wide.Width > narrow.Width);
    }

    [Fact]
    public void Turning_the_picture_turns_a_mark_once_and_keeps_its_size()
    {
        var edit = new ImageEdit { Marks = [Box(0.1f, 0.1f, 0.3f, 0.2f, 10)] };

        var turned = edit.Turn(clockwise: true, W, H).Marks[0];

        Assert.Equal(100f, turned.Rotation);
        Assert.Equal(200f, turned.Area.Width * H, 1);
        Assert.Equal(50f, turned.Area.Height * W, 1);
    }

    [Fact]
    public void Turning_the_picture_keeps_a_mark_over_the_same_spot()
    {
        var edit = new ImageEdit { Marks = [Box(0.1f, 0.1f, 0.3f, 0.2f)] };

        var turned = edit.Turn(clockwise: true, W, H).Marks[0];

        Assert.Equal(1f - 0.15f, turned.Area.MidX, 3);
        Assert.Equal(0.2f, turned.Area.MidY, 3);
    }

    [Fact]
    public void A_blur_follows_the_picture_without_a_rotation_of_its_own()
    {
        var edit = new ImageEdit { Marks = [new Mark(MarkKind.Blur, new SKRect(0.1f, 0.1f, 0.3f, 0.2f), SKColors.White)] };

        var turned = edit.Turn(clockwise: true, W, H).Marks[0];

        Assert.Equal(0f, turned.Rotation);
        Assert.Equal(0.1f, turned.Area.Width, 3);
        Assert.Equal(0.2f, turned.Area.Height, 3);
    }

    [Fact]
    public void A_mark_past_the_edge_can_still_be_moved_without_jumping()
    {
        var box = Box(0.02f, 0.4f, 0.12f, 0.6f, 45);

        var left = MarkTransform.Drag(box, MarkHandle.Body, new SKPoint(0.1f, 0.5f), new SKPoint(0.099f, 0.5f), W, H, false);
        var right = MarkTransform.Drag(box, MarkHandle.Body, new SKPoint(0.1f, 0.5f), new SKPoint(0.11f, 0.5f), W, H, false);

        Assert.Equal(box.Area.Left, left.Area.Left, 3);
        Assert.Equal(box.Area.Left + 0.01f, right.Area.Left, 3);
    }

    [Fact]
    public async Task A_local_picture_is_read_with_its_size()
    {
        var dir = Directory.CreateTempSubdirectory("eternal-img-");
        try
        {
            var path = Path.Combine(dir.FullName, "p.png");
            using (var bitmap = new SKBitmap(64, 32))
            using (var image = SKImage.FromBitmap(bitmap))
            using (var encoded = image.Encode(SKEncodedImageFormat.Png, 100))
                await File.WriteAllBytesAsync(path, encoded.ToArray());

            using var picture = ScreenshotRenderer.LoadImageFile(path);

            Assert.Equal((64, 32), (picture.Width, picture.Height));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Everything_renders_without_touching_the_source()
    {
        using var source = new SKBitmap(200, 100);
        source.Erase(SKColors.Gray);
        using var stamp = SKImage.FromBitmap(source);
        var edit = new ImageEdit
        {
            Marks =
            [
                new Mark(MarkKind.Box, new SKRect(0.1f, 0.1f, 0.3f, 0.3f), SKColors.Red, Rotation: 30, Opacity: 0.5f),
                new Mark(MarkKind.Arrow, new SKRect(0.9f, 0.1f, 0.5f, 0.5f), SKColors.Red),
                new Mark(MarkKind.Blur, new SKRect(0.1f, 0.5f, 0.3f, 0.8f), SKColors.Red),
                new Mark(MarkKind.Pixelate, new SKRect(0.4f, 0.5f, 0.6f, 0.8f), SKColors.Red),
                new Mark(MarkKind.Text, ScreenshotRenderer.TextArea("hi", new SKPoint(0.2f, 0.2f), 200, 100), SKColors.Yellow, "hi", Rotation: -20),
                new Mark(MarkKind.Image, new SKRect(0.6f, 0.2f, 0.9f, 0.6f), SKColors.White, Picture: stamp, Rotation: 45),
            ],
        };

        using var result = ScreenshotRenderer.Render(source, edit);

        Assert.Equal((200, 100), (result.Width, result.Height));
    }
}
