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

        var entry = Assert.Single(news);
        Assert.Equal("Fixed smoke.", entry.Summary);
        Assert.Equal(2, entry.Posts.Count);
    }

    [Fact]
    public void A_post_with_no_picture_uses_the_game_art()
    {
        var entry = Assert.Single(FeedService.ParseNews(News(Item("Team Fortress 2 Update Released", "Product Update", 1793402368, "Fixed smoke.")), 10));
        Assert.Null(entry.ImageUrl);
        Assert.EndsWith("/apps/440/header.jpg", entry.Picture);
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

    [Fact]
    public void Updates_in_a_row_become_one_entry_listing_each_date()
    {
        var json = News(
            Item("Team Fortress 2 Update Released", "Community Announcements", 1793402368, "Newest changes."),
            Item("Team Fortress 2 Update Released", "Community Announcements", 1793305779, "Older changes."),
            Item("Operation: Europa", "Community Announcements", 1793292975, "Legend speaks."),
            Item("Team Fortress 2 Update Released", "Community Announcements", 1792000000, "Oldest changes."));

        var news = FeedService.ParseNews(json, 10);

        Assert.Equal(3, news.Count);
        Assert.Equal("Newest changes.", news[0].Summary);
        Assert.Equal(2, news[0].Posts.Count);
        Assert.Equal("Operation: Europa", news[1].Title);
        Assert.Single(news[2].Posts);
    }

    [Fact]
    public void An_entry_keeps_the_link_of_each_post_it_stands_for()
    {
        var json = News(
            Item("Team Fortress 2 Update Released", "Community Announcements", 1793402368, "a"),
            Item("Team Fortress 2 Update Released", "Community Announcements", 1793305779, "b"));

        var entry = Assert.Single(FeedService.ParseNews(json, 10));

        Assert.Equal(["https://example.com/Community Announcements/1793402368", "https://example.com/Community Announcements/1793305779"],
            entry.Posts.Select(p => p.Url));
    }

    [Fact]
    public void The_stock_opening_of_an_update_post_is_left_out_of_the_summary()
    {
        var json = News(Item("Team Fortress 2 Update Released", "Community Announcements", 1793402368,
            "An update to Team Fortress 2 has been released. The update will be applied automatically when you restart Team Fortress 2. The major changes include: Fixed smoke."));

        Assert.Equal("Fixed smoke.", Assert.Single(FeedService.ParseNews(json, 10)).Summary);
    }

    [Fact]
    public void The_requested_count_applies_to_entries_after_grouping()
    {
        var json = News(Enumerable.Range(0, 6).Select(i => Item("Update", "TF2 Blog", 1793000000 - i * 86400, "x"))
            .Append(Item("Other", "TF2 Blog", 1790000000, "y")).ToArray());

        var news = FeedService.ParseNews(json, 2);

        Assert.Equal(["Update", "Other"], news.Select(n => n.Title));
    }
}
