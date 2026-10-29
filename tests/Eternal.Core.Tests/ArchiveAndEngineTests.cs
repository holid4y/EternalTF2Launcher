using System.IO.Compression;
using Eternal.Core.Engine;
using Eternal.Core.Mods;

namespace Eternal.Core.Tests;

public sealed class ArchiveAndEngineTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("eternal-archive-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string MakeZip(string name, params (string Entry, string Content)[] files)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, content) in files)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
            writer.Write(content);
        }
        return path;
    }

    [Fact]
    public void Archives_unpack_into_a_folder_named_after_them()
    {
        var zip = MakeZip("Cool Hitsound.zip", ("sound/ui/hitsound.wav", "x"));

        var folder = ArchiveIngest.Extract(zip, Path.Combine(_dir, "staging"));

        Assert.Equal("Cool Hitsound", Path.GetFileName(folder));
        Assert.True(File.Exists(Path.Combine(folder, "sound", "ui", "hitsound.wav")));
    }

    [Fact]
    public void Solid_7z_archives_unpack_with_their_folders()
    {
        // Solid RAR and 7z archives take the single-pass path; a mod folder inside the archive is kept.
        var archive = Path.Combine(AppContext.BaseDirectory, "Fixtures", "solid-skin.7z");

        var folder = ArchiveIngest.Extract(archive, Path.Combine(_dir, "staging"));

        Assert.Equal("solid-skin", Path.GetFileName(folder));
        Assert.Equal("mdl", File.ReadAllText(Path.Combine(folder, "Solid Skin", "models", "weapons", "rocket.mdl")));
        Assert.Equal("vmt", File.ReadAllText(Path.Combine(folder, "Solid Skin", "materials", "models", "weapons", "rocket.vmt")));
    }

    [Fact]
    public void Entries_escaping_the_folder_abort_before_anything_is_written()
    {
        var zip = MakeZip("evil.zip", ("ok.txt", "fine"), ("../../escape.txt", "x"));

        Assert.Throws<ArchiveException>(() => ArchiveIngest.Extract(zip, Path.Combine(_dir, "staging")));
        Assert.False(File.Exists(Path.Combine(_dir, "escape.txt")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_dir, "staging"), "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/../../x")]
    [InlineData("C:/Windows/x")]
    public void Unsafe_entry_names_are_rejected(string key)
    {
        Assert.Throws<ArchiveException>(() => ArchiveIngest.ResolveTarget(_dir, key));
    }

    [Fact]
    public void Archive_names_become_safe_folder_names()
    {
        Assert.Equal("a_b_c", ArchiveIngest.SafeFolderName("a/b:c"));
        Assert.Equal("mod", ArchiveIngest.SafeFolderName("..."));
    }

    [Fact]
    public void Engine_progress_and_result_lines_are_parsed_and_noise_ignored()
    {
        var reported = new List<EngineProgress>();
        var progress = new SyncProgress<EngineProgress>(reported.Add);

        Assert.Null(EngineClient.Parse("""{"event":"progress","stage":"Packing","percent":42,"message":"Making custom VPK"}""", progress));
        Assert.Null(EngineClient.Parse("Traceback (most recent call last):", progress));
        var result = EngineClient.Parse("""{"event":"result","command":"install","success":true,"data":{"tf_path":"/tf"},"warnings":["w"],"errors":[],"durationMs":9063}""", progress);

        Assert.Equal(42, Assert.Single(reported).Percent);
        Assert.NotNull(result);
        Assert.True(result!.Success);
        Assert.Equal(["w"], result.Warnings);
        Assert.Equal("/tf", result.Data.GetProperty("tf_path").GetString());
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
