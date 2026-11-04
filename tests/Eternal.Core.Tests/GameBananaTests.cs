using System.Net;
using System.Security.Cryptography;
using System.Text;
using Eternal.Core.GameBanana;

namespace Eternal.Core.Tests;

public sealed class GameBananaTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("eternal-gb-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "GameBanana", name));

    [Fact]
    public void Categories_come_with_their_counts()
    {
        var categories = GameBananaClient.ParseCategories(Fixture("mod-categories.json"));

        var skins = Assert.Single(categories, c => c.Name == "Skins");
        Assert.Equal(7951, skins.Id);
        Assert.True(skins.ItemCount > 1000);
        Assert.Equal(26, skins.ChildCount);
        Assert.Equal(10, categories.Count);
    }

    [Fact]
    public void A_page_of_mods_has_names_authors_pictures_and_a_page_count()
    {
        var page = GameBananaClient.ParsePage(Fixture("mod-index.json"), Section.Mods, page: 3);

        Assert.Equal(4, page.Items.Count);
        Assert.Equal(3, page.Page);
        Assert.Equal((page.Total + GameBananaClient.PageSize - 1) / GameBananaClient.PageSize, page.PageCount);
        var first = page.Items[0];
        Assert.Equal("The Female Pyro", first.Name);
        Assert.False(string.IsNullOrEmpty(first.Author));
        Assert.StartsWith("https://images.gamebanana.com/", first.Thumbnail);
        Assert.Equal($"https://gamebanana.com/mods/{first.Id}", first.Url);
    }

    [Fact]
    public void Mods_marked_for_casual_by_their_author_say_so()
    {
        const string listed = """
            {"_aMetadata": {"_nRecordCount": 2}, "_aRecords": [
              {"_idRow": 1, "_sName": "Tagged", "_aTags": ["Casual Compatibility: Cueki's Casual Preloader", "Software Used: GIMP"]},
              {"_idRow": 2, "_sName": "Untagged", "_aTags": ["Software Used: GIMP"]}
            ]}
            """;

        var page = GameBananaClient.ParsePage(listed, Section.Mods, 1);
        var profile = GameBananaClient.ParseDetails(Fixture("mod-profile.json"), Section.Mods);

        Assert.Equal([true, false], page.Items.Select(i => i.CasualReady));
        Assert.True(profile.Summary.CasualReady);
    }

    [Fact]
    public void Rated_and_hidden_submissions_are_kept_and_marked_adult_while_malformed_are_left_out()
    {
        // GameBanana flags adult content either with _bHasContentRatings or with
        // _sInitialVisibility "hide" (both show on the site only with the adult filter on).
        const string json = """
            {"_aMetadata": {"_nRecordCount": 5}, "_aRecords": [
              {"_idRow": 1, "_sName": "Fine", "_aSubmitter": {"_sName": "a"}, "_sInitialVisibility": "show"},
              {"_idRow": 2, "_sName": "Rated", "_bHasContentRatings": true, "_sInitialVisibility": "show"},
              {"_idRow": 3, "_sName": "Hidden adult", "_sInitialVisibility": "hide"},
              {"_idRow": 4, "_sName": "Removed", "_sInitialVisibility": "private"},
              {"_idRow": "5; drop", "_sName": "Bad id"},
              {"_sName": "No id"}
            ]}
            """;

        var page = GameBananaClient.ParsePage(json, Section.Mods, 1);

        Assert.Equal(["Fine", "Rated", "Hidden adult"], page.Items.Select(i => i.Name));
        Assert.Equal([false, true, true], page.Items.Select(i => i.Adult));
    }

    [Fact]
    public void Sound_cards_carry_their_preview_audio_and_descriptions_carry_video_links()
    {
        const string index = """
            {"_aMetadata": {"_nRecordCount": 1}, "_aRecords": [
              {"_idRow": 7, "_sName": "Hitsound", "_sInitialVisibility": "show",
               "_aPreviewMedia": {"_aMetadata": {"_sAudioUrl": "https://files.gamebanana.com/preview/sounds/hit.mp3"}}}
            ]}
            """;
        var card = GameBananaClient.ParsePage(index, Section.Sounds, 1).Items.Single();
        Assert.Equal("https://files.gamebanana.com/preview/sounds/hit.mp3", card.AudioUrl);

        const string profile = """
            {"_idRow": 9, "_sName": "Mod", "_sInitialVisibility": "show", "_nDownloadCount": 3,
             "_sText": "<p>Watch <a href=\"https://youtu.be/dQw4w9WgXcQ\">this</a> and <iframe src=\"https://www.youtube.com/embed/abcDEF12345\"></iframe> or <a href=\"https://evil.example/x.mp4\">no</a></p>"}
            """;
        var details = GameBananaClient.ParseDetails(profile, Section.Mods);
        Assert.Equal(["https://www.youtube.com/watch?v=dQw4w9WgXcQ", "https://www.youtube.com/watch?v=abcDEF12345"], details.Videos);
    }

    [Fact]
    public void Pictures_from_anywhere_but_gamebanana_are_dropped()
    {
        const string json = """
            {"_aMetadata": {"_nRecordCount": 2}, "_aRecords": [
              {"_idRow": 1, "_sName": "Elsewhere", "_aPreviewMedia": {"_aImages": [{"_sBaseUrl": "https://evil.example/img", "_sFile530": "a.jpg"}]}},
              {"_idRow": 2, "_sName": "Plain http", "_aPreviewMedia": {"_aImages": [{"_sBaseUrl": "http://images.gamebanana.com/img/ss/mods", "_sFile530": "a.jpg"}]}}
            ]}
            """;

        var page = GameBananaClient.ParsePage(json, Section.Mods, 1);

        Assert.All(page.Items, i => Assert.Null(i.Thumbnail));
    }

    [Fact]
    public void A_profile_lists_its_files_with_size_checksum_and_scan()
    {
        var details = GameBananaClient.ParseDetails(Fixture("mod-profile.json"), Section.Mods);

        Assert.Equal("The Female Scout (FemScout)", details.Summary.Name);
        var file = details.Files[0];
        Assert.EndsWith(".rar", file.Name);
        Assert.True(file.Size > 0);
        Assert.Equal(32, file.Md5!.Length);
        Assert.Equal(FileScan.Clean, file.Scan);
        Assert.True(file.CanInstall);
        Assert.Equal($"https://gamebanana.com/dl/{file.Id}", file.DownloadUrl);
        Assert.NotEmpty(details.Screenshots);
        Assert.True(details.Downloads > 0);
    }

    [Fact]
    public void Descriptions_become_plain_text()
    {
        var text = GameBananaClient.PlainText(
            """<b>Replaces</b> the Scout&nbsp;&amp; more<br><br><script>alert(1)</script><ul><li>Model</li><li>Skins</li></ul><img src="x.gif"><p>Done</p>""");

        Assert.Equal("Replaces the Scout & more\n\n• Model\n• Skins\nDone", text);
    }

    [Theory]
    [InlineData("""{"_sAvResult": "clean", "_sAnalysisResult": "ok"}""", FileScan.Clean)]
    [InlineData("""{"_sAvResult": null, "_sAnalysisState": "queued"}""", FileScan.Pending)]
    [InlineData("""{"_sAvResult": "infected", "_sAnalysisResult": "ok"}""", FileScan.Flagged)]
    [InlineData("""{"_sAvResult": "clean", "_sAnalysisResult": "contains_exe"}""", FileScan.Flagged)]
    public void Only_files_the_scan_cleared_can_be_installed(string scan, FileScan expected)
    {
        var file = scan.TrimEnd('}') + """, "_idRow": 5, "_sFile": "mod.zip", "_nFilesize": 10}""";
        var json = """{"_idRow": 9, "_sName": "x", "_aFiles": [""" + file + "]}";

        var parsed = Assert.Single(GameBananaClient.ParseDetails(json, Section.Mods).Files);

        Assert.Equal(expected, parsed.Scan);
        Assert.Equal(expected == FileScan.Clean, parsed.CanInstall);
    }

    [Theory]
    [InlineData("../../evil.zip", "evil.zip")]
    [InlineData("C:\\Windows\\mod.rar", "mod.rar")]
    [InlineData("con.7z", "_con.7z")]
    [InlineData("", "download")]
    public void File_names_never_leave_the_download_folder(string given, string expected)
    {
        Assert.Equal(expected, GameBananaClient.SafeFileName(given));
    }

    [Theory]
    [InlineData("mod.zip", true)]
    [InlineData("mod.RAR", true)]
    [InlineData("mod.7z", true)]
    [InlineData("mod.vpk", true)]
    [InlineData("setup.exe", false)]
    [InlineData("mod.zip.exe", false)]
    [InlineData("readme", false)]
    public void Only_archives_and_vpks_are_installable(string name, bool expected)
    {
        Assert.Equal(expected, GameBananaClient.IsInstallable(name));
    }

    [Fact]
    public void Queries_are_escaped_and_bounded()
    {
        var url = GameBananaClient.IndexUrl(new BrowseQuery(Section.Sounds, 381, " hit,sound & co ", BrowseSort.MostLiked, Page: -4));

        Assert.StartsWith("https://gamebanana.com/apiv11/Sound/Index?", url);
        Assert.Contains("_nPage=1&", url);
        Assert.Contains($"_nPerpage={GameBananaClient.PageSize}", url);
        Assert.Contains("_aFilters%5BGeneric_Category%5D=381", url);
        Assert.Contains("_aFilters%5BGeneric_Name%5D=contains%2Chit%20sound%20%26%20co", url);
        Assert.Contains("_sSort=Generic_MostLiked", url);
    }

    [Fact]
    public void One_letter_searches_are_ignored()
    {
        Assert.DoesNotContain("Generic_Name", GameBananaClient.IndexUrl(new BrowseQuery(Section.Mods, Search: "é")));
    }

    [Fact]
    public void Gamebanana_hosts_are_recognised_and_lookalikes_are_not()
    {
        Assert.True(GameBananaClient.IsGameBananaUrl("https://files.gamebanana.com/mods/a.zip"));
        Assert.True(GameBananaClient.IsGameBananaUrl("https://gamebanana.com/mods/1"));
        Assert.False(GameBananaClient.IsGameBananaUrl("http://gamebanana.com/mods/1"));
        Assert.False(GameBananaClient.IsGameBananaUrl("https://gamebanana.com.evil.example/a.zip"));
        Assert.False(GameBananaClient.IsGameBananaUrl("https://evilgamebanana.com/a.zip"));
        Assert.False(GameBananaClient.IsGameBananaUrl("javascript:alert(1)"));
    }

    private static SubmissionFile Offered(byte[] content, string? md5 = null, long? size = null) =>
        new(77, "mod.zip", size ?? content.Length, null, DateTimeOffset.UnixEpoch, 0, md5 ?? Convert.ToHexString(MD5.HashData(content)), FileScan.Clean);

    private static GameBananaClient Serving(byte[] body, string finalUrl = "https://files.gamebanana.com/mods/mod.zip", HttpStatusCode status = HttpStatusCode.OK) =>
        new(new HttpClient(new Fake(body, finalUrl, status)));

    [Fact]
    public async Task A_download_is_checked_against_its_checksum()
    {
        var body = Encoding.UTF8.GetBytes("archive bytes");

        var path = await Serving(body).DownloadAsync(Offered(body), _dir, null);

        Assert.Equal(body, await System.IO.File.ReadAllBytesAsync(path));
        Assert.Equal("mod.zip", Path.GetFileName(path));
    }

    [Fact]
    public async Task A_corrupted_download_is_thrown_away()
    {
        var body = Encoding.UTF8.GetBytes("archive bytes");

        await Assert.ThrowsAsync<GameBananaException>(() => Serving(body).DownloadAsync(Offered(body, md5: new string('0', 32)), _dir, null));

        Assert.Empty(Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_download_larger_than_announced_is_cut_off()
    {
        var body = new byte[4096];

        await Assert.ThrowsAsync<GameBananaException>(() => Serving(body).DownloadAsync(Offered(body, size: 100), _dir, null));

        Assert.Empty(Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_download_redirected_off_gamebanana_is_refused()
    {
        var body = Encoding.UTF8.GetBytes("archive bytes");

        await Assert.ThrowsAsync<GameBananaException>(() => Serving(body, "https://evil.example/mod.zip").DownloadAsync(Offered(body), _dir, null));
    }

    [Fact]
    public async Task A_download_follows_redirects_within_gamebanana()
    {
        var body = Encoding.UTF8.GetBytes("archive bytes");
        var hops = new Hops(body, "https://files.gamebanana.com/mods/mod.zip");

        var path = await new GameBananaClient(new HttpClient(hops)).DownloadAsync(Offered(body), _dir, null);

        Assert.Equal(body, await System.IO.File.ReadAllBytesAsync(path));
        Assert.Equal(["https://gamebanana.com/dl/77", "https://files.gamebanana.com/mods/mod.zip"], hops.Requested);
    }

    [Fact]
    public async Task A_redirect_off_gamebanana_is_never_followed()
    {
        var hops = new Hops([], "http://127.0.0.1:8080/admin");

        await Assert.ThrowsAsync<GameBananaException>(() => new GameBananaClient(new HttpClient(hops)).DownloadAsync(Offered([1]), _dir, null));

        Assert.Equal(["https://gamebanana.com/dl/77"], hops.Requested);
    }

    [Fact]
    public async Task Endless_redirects_give_up()
    {
        var hops = new Hops([], "https://gamebanana.com/dl/77");

        await Assert.ThrowsAsync<GameBananaException>(() => new GameBananaClient(new HttpClient(hops)).DownloadAsync(Offered([1]), _dir, null));

        Assert.InRange(hops.Requested.Count, 2, 10);
    }

    [Fact]
    public async Task An_answer_of_unknown_length_is_still_capped()
    {
        var client = new GameBananaClient(new HttpClient(new Unsized(new byte[9 * 1024 * 1024])));

        var ex = await Assert.ThrowsAsync<GameBananaException>(() => client.BrowseAsync(new BrowseQuery(Section.Mods)));

        Assert.Contains("more than expected", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Being_rate_limited_gets_a_plain_answer()
    {
        var ex = await Assert.ThrowsAsync<GameBananaException>(() =>
            Serving([], status: HttpStatusCode.TooManyRequests).BrowseAsync(new BrowseQuery(Section.Mods)));

        Assert.Contains("try again", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Files_the_scan_flagged_are_never_downloaded()
    {
        var body = Encoding.UTF8.GetBytes("archive bytes");
        var flagged = Offered(body) with { Scan = FileScan.Flagged };

        await Assert.ThrowsAsync<GameBananaException>(() => Serving(body).DownloadAsync(flagged, _dir, null));
    }

    /// <summary>Sends gamebanana.com/dl/ on to <paramref name="next"/>, the way GameBanana's download links do.</summary>
    private sealed class Hops(byte[] body, string next) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requested.Add(url);
            if (Requested.Count > 50)
                throw new InvalidOperationException("Followed too many redirects");
            var response = url.Contains("/dl/", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(next) } }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    /// <summary>Answers without a Content-Length, as a chunked response does.</summary>
    private sealed class Unsized(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new Chunked(body), RequestMessage = request });

        private sealed class Chunked(byte[] body) : HttpContent
        {
            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(body).AsTask();

            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }
        }
    }

    private sealed class Fake(byte[] body, string finalUrl, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = new Uri(finalUrl);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body), RequestMessage = request });
        }
    }
}
