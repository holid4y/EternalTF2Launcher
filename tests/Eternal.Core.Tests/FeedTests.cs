using Eternal.Core.Feed;

namespace Eternal.Core.Tests;

public sealed class FeedTests
{
    private static string Item(string title, string feed, long date, string contents) =>
        $$"""{"title": "{{title}}", "url": "https://example.com/{{feed}}/{{date}}", "feedlabel": "{{feed}}", "date": {{date}}, "contents": "{{contents}}"}""";

    private static string News(params string[] items) => $$$"""{"appnews": {"newsitems": [{{{string.Join(",", items)}}}]}}""";

    [Fact]
    public void A_post_published_in_several_feeds_is_listed_once()
    {
        var json = News(
            Item("Team Fortress 2 Update Released", "Community Announcements", 1793402368, "Fixed smoke."),
            Item("Team Fortress 2 Update Released", "TF2 Blog", 1793402280, "Fixed smoke."),
            Item("Team Fortress 2 Update Released", "Product Update", 1793402280, "Fixed smoke."),
            Item("Team Fortress 2 Update Released", "Community Announcements", 1793305779, "Fixed a crash."));

        var news = FeedService.ParseNews(json, 10);

        Assert.Equal(["Fixed smoke.", "Fixed a crash."], news.Select(n => n.Summary));
    }

    [Fact]
    public void The_copy_with_a_picture_wins()
    {
        var json = News(
            Item("Operation: Europa", "Community Announcements", 1793292975, "Legend speaks."),
            Item("Operation: Europa", "TF2 Blog", 1793292900, "[img]{STEAM_CLAN_IMAGE}/1/europa.png[/img] Legend speaks."));

        var item = Assert.Single(FeedService.ParseNews(json, 10));

        Assert.Equal("https://clan.cloudflare.steamstatic.com/images/1/europa.png", item.ImageUrl);
    }

    [Fact]
    public void Localised_clan_images_become_the_picture_not_text()
    {
        var json = News(Item("Operation: Europa", "Community Announcements", 1793292975,
            "[img]{STEAM_CLAN_LOC_IMAGE}/554111/d18660ef.png[/img](Image Credit: Entropy) Legend speaks."));

        var item = Assert.Single(FeedService.ParseNews(json, 10));

        Assert.Equal("https://clan.cloudflare.steamstatic.com/images/554111/d18660ef.png", item.ImageUrl);
        Assert.DoesNotContain("STEAM_CLAN", item.Summary);
        Assert.StartsWith("(Image Credit: Entropy) Legend", item.Summary);
    }

    [Fact]
    public void Only_the_requested_number_of_posts_is_returned()
    {
        var json = News(Enumerable.Range(0, 6).Select(i => Item($"Post {i}", "TF2 Blog", 1793000000 - i * 86400, "x")).ToArray());

        Assert.Equal(4, FeedService.ParseNews(json, 4).Count);
    }
}
