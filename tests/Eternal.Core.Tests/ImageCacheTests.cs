using Eternal.App.Services;
using Xunit;

namespace Eternal.Core.Tests;

public sealed class ImageCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eternal-images-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Picture(string name, long size, DateTime written)
    {
        var file = Path.Combine(_root, name);
        using (var stream = File.Create(file))
            stream.SetLength(size);
        File.SetLastWriteTimeUtc(file, written);
        return file;
    }

    [Fact]
    public void Trim_deletes_the_oldest_pictures_until_the_folder_fits()
    {
        Directory.CreateDirectory(_root);
        var now = DateTime.UtcNow;
        var oldest = Picture("a.img", ImageCache.DiskBudget / 2, now.AddDays(-3));
        var older = Picture("b.img", ImageCache.DiskBudget / 2, now.AddDays(-2));
        var newest = Picture("c.img", ImageCache.DiskBudget / 2, now.AddDays(-1));
        var other = Picture("d.img.1234.part", ImageCache.DiskBudget, now.AddDays(-9));

        new ImageCache(_root).Trim();

        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(older));
        Assert.True(File.Exists(newest));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void Trim_leaves_a_folder_within_budget_alone()
    {
        Directory.CreateDirectory(_root);
        var picture = Picture("a.img", 1024, DateTime.UtcNow.AddYears(-1));

        new ImageCache(_root).Trim();

        Assert.True(File.Exists(picture));
    }

    [Fact]
    public void Trim_does_nothing_before_the_first_download()
    {
        new ImageCache(_root).Trim();

        Assert.False(Directory.Exists(_root));
    }
}
