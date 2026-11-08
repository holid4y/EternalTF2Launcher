using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Eternal.Core.Logging;

namespace Eternal.Core.Feed;

public sealed record NewsPost(DateTimeOffset Date, string Url);

/// <summary>One entry in the feed. Several posts in a row with the same title share an entry, newest first.</summary>
public sealed record NewsItem(string Title, string Summary, string Url, string? ImageUrl, DateTimeOffset Date, string Source)
{
    public IReadOnlyList<NewsPost> Posts { get; init; } = [new(Date, Url)];
}

public static partial class FeedService
{
    [GeneratedRegex(@"\[/?[a-zA-Z0-9*]+(?:=[^\]]*)?\]|<[^>]+>")]
    private static partial Regex Markup();

    [GeneratedRegex(@"\{STEAM_CLAN(?:_LOC)?_IMAGE\}(?<path>/[^\s\[\]""<>]+\.(?:png|jpe?g|gif|webp))", RegexOptions.IgnoreCase)]
    private static partial Regex ClanImage();

    [GeneratedRegex(@"https?://[^\s\[\]""<>]+\.(?:png|jpe?g|gif|webp)", RegexOptions.IgnoreCase)]
    private static partial Regex AnyImage();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^An update to Team Fortress 2 has been released\..*?The major changes include:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex UpdatePreamble();

    public static async Task<IReadOnlyList<NewsItem>> SteamNewsAsync(int count = 10, CancellationToken cancellation = default)
    {
        // Each post shows up in up to three feeds and updates in a row share an entry, so ask for more than will be shown.
        var url = $"https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid=440&count={count * 8}&maxlength=0&format=json";
        return ParseNews(await Http.Client.GetStringAsync(url, cancellation).ConfigureAwait(false), count);
    }

    internal static IReadOnlyList<NewsItem> ParseNews(string json, int count)
    {
        using var doc = JsonDocument.Parse(json);
        var items = new List<NewsItem>();
        foreach (var item in doc.RootElement.GetProperty("appnews").GetProperty("newsitems").EnumerateArray())
        {
            var contents = item.TryGetProperty("contents", out var c) ? c.GetString() ?? "" : "";
            var image = ClanImage().Match(contents) is { Success: true } clan
                ? "https://clan.cloudflare.steamstatic.com/images" + clan.Groups["path"].Value
                : AnyImage().Match(contents) is { Success: true } any ? any.Value : null;
            var text = Whitespace().Replace(WebUtility.HtmlDecode(Markup().Replace(ClanImage().Replace(contents, ""), " ")), " ").Trim();
            text = UpdatePreamble().Replace(text, "");
            var post = new NewsItem(
                item.GetProperty("title").GetString() ?? "",
                text.Length > 220 ? text[..220].TrimEnd() + "…" : text,
                item.GetProperty("url").GetString() ?? "",
                image,
                DateTimeOffset.FromUnixTimeSeconds(item.GetProperty("date").GetInt64()),
                item.TryGetProperty("feedlabel", out var label) ? label.GetString() ?? "Steam" : "Steam");

            var copy = items.FindIndex(p => p.Title == post.Title && (p.Date - post.Date).Duration() < TimeSpan.FromHours(6));
            if (copy < 0)
                items.Add(post);
            else if (items[copy].ImageUrl is null && post.ImageUrl is not null)
                items[copy] = post;
        }
        return Grouped(items).Take(count).ToArray();
    }

    private static IEnumerable<NewsItem> Grouped(List<NewsItem> items)
    {
        NewsItem? entry = null;
        foreach (var item in items)
        {
            if (entry is not null && entry.Title == item.Title)
            {
                entry = entry with { Posts = [.. entry.Posts, new NewsPost(item.Date, item.Url)] };
                continue;
            }
            if (entry is not null)
                yield return entry;
            entry = item;
        }
        if (entry is not null)
            yield return entry;
    }
}
