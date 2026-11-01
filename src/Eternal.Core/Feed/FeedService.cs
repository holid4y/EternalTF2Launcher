using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Eternal.Core.Logging;

namespace Eternal.Core.Feed;

public sealed record NewsItem(string Title, string Summary, string Url, string? ImageUrl, DateTimeOffset Date, string Source);

public sealed record UpdateInfo(string Version, string Url);

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

    public static async Task<IReadOnlyList<NewsItem>> SteamNewsAsync(int count = 10, CancellationToken cancellation = default)
    {
        // Each post usually shows up in two or three feeds, so ask for more than will be shown.
        var url = $"https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid=440&count={count * 3}&maxlength=0&format=json";
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
        return items.Take(count).ToArray();
    }

    public static async Task<UpdateInfo?> CheckForUpdateAsync(Version current, CancellationToken cancellation = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.Client.GetStringAsync(
                "https://api.github.com/repos/holid4y/EternalTF2Launcher/releases/latest", cancellation).ConfigureAwait(false));
            var tag = Str(doc.RootElement, "tag_name")?.TrimStart('v', 'V') ?? "";
            var url = Str(doc.RootElement, "html_url") ?? "";
            return Version.TryParse(tag, out var latest) && latest > current ? new UpdateInfo(tag, url) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            Log.Debug($"Update check failed: {ex.Message}");
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
