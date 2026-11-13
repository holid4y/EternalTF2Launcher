using Eternal.Core.Platform;
using Eternal.Core.Steam;

namespace Eternal.Core.Tests;

public sealed class SteamFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("eternal-steam-").FullName;
    private string UserDir => Path.Combine(_root, "Steam", "userdata", "1242567178");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Vdf_round_trips_what_steam_writes()
    {
        var text = "\"screenshots\"\n{\n\t\"440\"\n\t{\n\t\t\"0\"\n\t\t{\n\t\t\t\"filename\"\t\t\"440/screenshots/a.jpg\"\n\t\t\t\"externalfilename\"\t\t\"C:\\\\Games\\\\\\\"tf\\\"\"\n\t\t}\n\t}\n}\n";
        var root = Vdf.Parse(text);
        Assert.Equal("C:\\Games\\\"tf\"", root.Path("screenshots", "440", "0")!.Value("externalfilename"));
        Assert.Equal(text, Vdf.Write(root));
    }

    [Fact]
    public void Nothing_is_written_while_steam_runs()
    {
        var running = true;
        var queue = Queue(() => running);
        queue.Add(Favorite("91.216.250.10:27015"));

        Assert.True(queue.TryApply().SteamRunning);
        Assert.False(File.Exists(Path.Combine(UserDir, SteamWriteQueue.ServerHistoryFile)));

        running = false;
        var result = queue.TryApply();
        Assert.Equal(1, result.Applied);
        Assert.Equal(0, queue.Count);
        var favorites = ReadServers().Path("Filters", "Favorites")!;
        var entry = Assert.IsType<VdfNode>(Assert.Single(favorites.Children).Value);
        Assert.Equal("91.216.250.10:27015", entry.Value("address"));
        Assert.Equal("0", entry.Value("appid"));
    }

    [Fact]
    public void Server_changes_keep_the_rest_of_the_file_and_back_it_up()
    {
        WriteServers("""
            "Filters"
            {
                "Favorites"
                {
                    "1" { "name" "Old" "address" "45.88.229.19:27018" "LastPlayed" "1687636881" "appid" "0" "accountid" "3428899" }
                }
                "History"
                {
                    "4" { "name" "x" "address" "205.178.177.24:27015" "LastPlayed" "1681653317" "appid" "0" "accountid" "0" }
                }
                "Custom" { "keep" "me" }
            }
            """);
        var queue = Queue(() => false);
        queue.Add(Favorite("91.216.250.10:27015"));
        queue.Add(new SteamWrite { Kind = SteamWriteKind.RemoveFavorite, UserDir = UserDir, Target = "45.88.229.19:27018" });
        queue.Add(new SteamWrite { Kind = SteamWriteKind.AddHistory, UserDir = UserDir, Target = "205.178.177.24:27015", Time = 1800000000 });
        queue.TryApply();

        var filters = ReadServers().Section("Filters")!;
        Assert.Equal(["2"], filters.Section("Favorites")!.Children.Select(c => c.Key));
        Assert.Equal("1800000000", filters.Path("History", "4")!.Value("LastPlayed"));
        Assert.Equal("me", filters.Path("Custom")!.Value("keep"));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "backups")));
    }

    [Fact]
    public void Adding_then_removing_a_favorite_before_steam_closes_leaves_only_the_removal()
    {
        var queue = Queue(() => true);
        queue.Add(Favorite("91.216.250.10:27015"));
        queue.Add(new SteamWrite { Kind = SteamWriteKind.RemoveFavorite, UserDir = UserDir, Target = "91.216.250.10:27015" });
        Assert.Equal(SteamWriteKind.RemoveFavorite, Assert.Single(queue.Pending).Kind);
    }

    [Theory]
    [InlineData(SteamWriteKind.AddFavorite, "not-an-address")]
    [InlineData(SteamWriteKind.RemoveScreenshot, "440/screenshots/../../config.vdf")]
    [InlineData(SteamWriteKind.RemoveScreenshot, "730/screenshots/a.jpg")]
    public void Bad_targets_are_refused(SteamWriteKind kind, string target) =>
        Assert.Throws<ArgumentException>(() => Queue(() => true).Add(new SteamWrite { Kind = kind, UserDir = UserDir, Target = target }));

    [Fact]
    public void A_queue_file_pointing_outside_userdata_is_dropped_on_load()
    {
        var file = Path.Combine(_root, "writes.json");
        File.WriteAllText(file, """{"writes":[{"kind":"AddFavorite","userDir":"/etc","target":"1.2.3.4:27015"}]}""");
        Assert.Equal(0, new SteamWriteQueue(file, Path.Combine(_root, "backups"), () => false).Count);
    }

    [Fact]
    public void Screenshot_library_reads_steam_records_and_ignores_unsafe_names()
    {
        var folder = SteamScreenshots.Folder(UserDir, 440);
        Directory.CreateDirectory(Path.Combine(folder, "thumbnails"));
        File.WriteAllBytes(Path.Combine(folder, "20261102205959_1.jpg"), [1]);
        File.WriteAllBytes(Path.Combine(folder, "20261103101010_1.jpg"), [1]);
        File.WriteAllText(Path.Combine(UserDir, "760", "screenshots.vdf"), """
            "screenshots"
            {
                "440"
                {
                    "0" { "filename" "440/screenshots/20261102205959_1.jpg" "width" "1280" "height" "720" "creation" "1793663999" "publishedfileid" "3361337" }
                    "1" { "filename" "440/screenshots/../../../screenshots.vdf" }
                }
            }
            """);

        var shots = SteamScreenshots.Load(UserDir, 440);
        Assert.Equal(2, shots.Count);
        var recorded = shots.Single(s => s.PublishedFileId is not null);
        Assert.Equal(1280, recorded.Width);
        Assert.Equal("https://steamcommunity.com/sharedfiles/filedetails/?id=3361337", recorded.ShareUrl);
        Assert.EndsWith(Path.Combine("thumbnails", "20261102205959_1.jpg"), recorded.ThumbnailPath);
    }

    [Fact]
    public void Removing_a_screenshot_renumbers_the_rest()
    {
        Directory.CreateDirectory(Path.Combine(UserDir, "760"));
        File.WriteAllText(Path.Combine(UserDir, "760", "screenshots.vdf"), """
            "screenshots" { "440" {
                "0" { "filename" "440/screenshots/a.jpg" }
                "1" { "filename" "440/screenshots/b.jpg" "width" "1280" }
                "2" { "filename" "440/screenshots/c.jpg" }
            } "shortcutnames" { } }
            """);
        var queue = Queue(() => false);
        queue.Add(new SteamWrite { Kind = SteamWriteKind.RemoveScreenshot, UserDir = UserDir, Target = "440/screenshots/a.jpg" });
        queue.Add(new SteamWrite { Kind = SteamWriteKind.ResizeScreenshot, UserDir = UserDir, Target = "440/screenshots/b.jpg", Width = 640, Height = 360 });
        queue.TryApply();

        var app = Vdf.Parse(File.ReadAllText(Path.Combine(UserDir, "760", "screenshots.vdf"))).Path("screenshots", "440")!;
        Assert.Equal(["0", "1"], app.Children.Select(c => c.Key));
        Assert.Equal("640", app.Section("0")!.Value("width"));
        Assert.Equal("440/screenshots/c.jpg", app.Section("1")!.Value("filename"));
    }

    [Fact]
    public void Trash_keeps_the_file_and_records_where_it_came_from()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows uses the Recycle Bin, not the freedesktop trash.");
        var file = Path.Combine(_root, "shot one.jpg");
        File.WriteAllBytes(file, [1, 2, 3]);
        var trash = Path.Combine(_root, "Trash");
        var moved = Trash.MoveToFreedesktopTrash(file, trash);

        Assert.False(File.Exists(file));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(moved));
        var info = File.ReadAllText(Path.Combine(trash, "info", "shot one.jpg.trashinfo"));
        Assert.Contains("Path=" + _root.Replace(" ", "%20") + "/shot%20one.jpg", info);

        File.WriteAllBytes(file, [4]);
        Assert.EndsWith("shot one.2.jpg", Trash.MoveToFreedesktopTrash(file, trash));
    }

    private SteamWriteQueue Queue(Func<bool> running) =>
        new(Path.Combine(_root, "writes.json"), Path.Combine(_root, "backups"), running);

    private SteamWrite Favorite(string address) =>
        new() { Kind = SteamWriteKind.AddFavorite, UserDir = UserDir, Target = address, Time = 1790000000 };

    private void WriteServers(string text)
    {
        var path = Path.Combine(UserDir, SteamWriteQueue.ServerHistoryFile);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private VdfNode ReadServers() => Vdf.Parse(File.ReadAllText(Path.Combine(UserDir, SteamWriteQueue.ServerHistoryFile)));
}

public sealed class ServerBookTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("eternal-book-").FullName;
    private string UserDir => Path.Combine(_root, "userdata", "1127760660");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_game_list_that_cannot_be_read_still_shows_the_launchers_own_favorites()
    {
        var file = Path.Combine(UserDir, SteamWriteQueue.ServerHistoryFile);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "\"Filters\" { }");
        var queue = new SteamWriteQueue(Path.Combine(_root, "writes.json"), Path.Combine(_root, "backups"), () => true);
        var book = new Eternal.Core.Servers.ServerBook(Path.Combine(_root, "servers.json"), queue, () => UserDir);
        Eternal.Core.Servers.ServerAddress.TryParse("1.2.3.4:27015", out var added);
        book.AddFavorite(added, "Added here");

        using var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal("Added here", Assert.Single(book.Favorites()).Name);
    }

    [Fact]
    public void A_saved_list_that_could_not_be_read_is_not_overwritten()
    {
        const string saved = """{"favorites":[{"address":"1.2.3.4:27015","name":"Kept","at":1}]}""";
        var file = Path.Combine(_root, "servers.json");
        File.WriteAllText(file, saved);
        var queue = new SteamWriteQueue(Path.Combine(_root, "writes.json"), Path.Combine(_root, "backups"), () => true);
        Eternal.Core.Servers.ServerBook book;
        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            book = new Eternal.Core.Servers.ServerBook(file, queue, () => null);
        Eternal.Core.Servers.ServerAddress.TryParse("5.6.7.8:27015", out var other);

        book.AddFavorite(other, "Added while the list was unreadable");

        Assert.False(book.LoadOutcome.CanSave);
        Assert.Equal(saved, File.ReadAllText(file));
    }

    [Fact]
    public void Favorites_merge_the_launcher_list_with_the_game_list_and_hide_pending_removals()
    {
        var file = Path.Combine(UserDir, SteamWriteQueue.ServerHistoryFile);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """
            "Filters" { "Favorites" { "1" { "name" "45.88.229.19:27018" "address" "45.88.229.19:27018" "LastPlayed" "1687636881" "appid" "0" } }
                        "History" { "1" { "name" "x" "address" "91.216.250.10:27015" "LastPlayed" "1699650102" "appid" "0" } } }
            """);
        var queue = new SteamWriteQueue(Path.Combine(_root, "writes.json"), Path.Combine(_root, "backups"), () => true);
        var book = new Eternal.Core.Servers.ServerBook(Path.Combine(_root, "servers.json"), queue, () => UserDir);
        Eternal.Core.Servers.ServerAddress.TryParse("1.2.3.4:27015", out var added);
        Eternal.Core.Servers.ServerAddress.TryParse("45.88.229.19:27018", out var fromGame);

        book.AddFavorite(added, "Added here");
        Assert.Equal(2, book.Favorites().Count);

        book.RemoveFavorite(fromGame);
        Assert.Equal("Added here", Assert.Single(book.Favorites()).Name);

        book.Played(added, "Added here");
        Assert.Equal(added, book.Recent()[0].Address);
        Assert.Contains(book.Recent(), r => r.Address.ToString() == "91.216.250.10:27015");
        Assert.Equal(3, queue.Count);
    }
}
