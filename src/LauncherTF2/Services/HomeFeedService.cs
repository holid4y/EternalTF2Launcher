using LauncherTF2.Core;
using LauncherTF2.Models;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LauncherTF2.Services;

/// <summary>
/// Fetches live TF2 content for the Home tab from Steam and GameBanana APIs.
/// Results are cached in-memory for 15 minutes.
/// </summary>
public class HomeFeedService
{
    private const int TF2AppId = 440;
    private const int TF2GameBananaId = 297;
    private const int CacheMinutes = 15;

    private static readonly HttpClient _http = AppHttpClient.Instance;

    // Cheap HTML <img src="..."> matcher — works on every BBCode/HTML mix Valve serves.
    private static readonly Regex _imgSrcRegex = new(
        """<img[^>]+src\s*=\s*["']([^"']+)["']""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Markdown-style ![alt](url) image fallback.
    private static readonly Regex _markdownImgRegex = new(
        @"!\[[^\]]*\]\((https?://[^\s)]+\.(?:png|jpe?g|gif|webp))\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Steam Community Announcements use {STEAM_CLAN_IMAGE}/{STEAM_CLAN_LOC_IMAGE}
    // macros instead of real URLs; capture the path so we can resolve the CDN URL.
    private static readonly Regex _clanImageRegex = new(
        @"\{STEAM_CLAN(?:_LOC)?_IMAGE\}/?(?<path>[^\s\[\]""'<>]+\.(?:png|jpe?g|gif|webp))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Any leftover clan-image macro reference (with or without an extension) —
    // removed from the displayed text so it never leaks as raw "{STEAM_..." noise.
    private static readonly Regex _clanImageStripRegex = new(
        @"\{STEAM_CLAN(?:_LOC)?_IMAGE\}\S*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // BBCode tags ([img], [url=...], [b], [list], ...) that Community
    // Announcements mix into the contents.
    private static readonly Regex _bbcodeRegex = new(
        @"\[/?[a-zA-Z*][^\]]*\]",
        RegexOptions.Compiled);

    private List<NewsItem>? _cachedNews;
    private List<NewModItem>? _cachedMods;
    private DateTime _newsCachedAt = DateTime.MinValue;
    private DateTime _modsCachedAt = DateTime.MinValue;

    public bool LastSteamNewsLoadSucceeded { get; private set; }
    public bool LastGameBananaLoadSucceeded { get; private set; }

    public async Task<List<NewsItem>> GetSteamNewsAsync(int count = 5)
    {
        if (_cachedNews != null && (DateTime.UtcNow - _newsCachedAt).TotalMinutes < CacheMinutes)
            return _cachedNews;

        try
        {
            var url = $"https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid={TF2AppId}&count={count}&maxlength=600&format=json";
            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            var items  = new List<NewsItem>();
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var newsItems = doc.RootElement.GetProperty("appnews").GetProperty("newsitems");

            foreach (var item in newsItems.EnumerateArray())
            {
                var articleUrl = item.GetProperty("url").GetString() ?? "";
                var title      = item.GetProperty("title").GetString() ?? "";

                // The Steam News API mirrors many TF2 Blog posts into the
                // Community Announcements feed with a different URL — dedupe by
                // both URL and normalised title so the Home feed shows each
                // article once regardless of which feed it came from.
                if (!seenUrls.Add(articleUrl)) continue;
                if (!string.IsNullOrWhiteSpace(title) && !seenTitles.Add(title.Trim())) continue;

                var contents = item.GetProperty("contents").GetString() ?? "";
                items.Add(new NewsItem
                {
                    Title    = title,
                    Contents = StripHtml(contents),
                    Date     = DateTimeOffset.FromUnixTimeSeconds(item.GetProperty("date").GetInt64()).DateTime,
                    Url      = articleUrl,
                    FeedLabel = item.TryGetProperty("feedlabel", out var fl) ? fl.GetString() ?? "" : "",
                    ImageUrl = ExtractFirstImageUrl(contents),
                });
            }

            _cachedNews = items;
            _newsCachedAt = DateTime.UtcNow;
            LastSteamNewsLoadSucceeded = true;
            Logger.LogInfo($"[HomeFeed] Loaded {items.Count} Steam news items");
            return items;
        }
        catch (Exception ex)
        {
            LastSteamNewsLoadSucceeded = false;
            Logger.LogWarning($"[HomeFeed] Steam news fetch failed: {ex.Message}");
            return [];
        }
    }

    public async Task<List<NewModItem>> GetNewModsAsync(int count = 8)
    {
        if (_cachedMods != null && (DateTime.UtcNow - _modsCachedAt).TotalMinutes < CacheMinutes)
            return _cachedMods;

        try
        {
            // Fetch extra so NSFW filtering still leaves a full row of results.
            var fetchCount = Math.Min(count * 3, 50);
            var url = $"https://gamebanana.com/apiv11/Mod/Index?_nPage=1&_nPerpage={fetchCount}" +
                      $"&_csvProperties=_idRow,_sName,_aPreviewMedia,_aSubmitter,_tsDateAdded,_bIsNsfw,_sProfileUrl" +
                      $"&_aFilters[Generic_Game]={TF2GameBananaId}" +
                      $"&_sSort=Generic_LatestModified";

            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            var items = new List<NewModItem>();
            var records = doc.RootElement.GetProperty("_aRecords");

            foreach (var rec in records.EnumerateArray())
            {
                if (items.Count >= count)
                    break;

                if (rec.TryGetProperty("_bIsNsfw", out var nsfw) &&
                    nsfw.ValueKind == JsonValueKind.True)
                    continue;

                string? thumbUrl = null;
                if (rec.TryGetProperty("_aPreviewMedia", out var media) &&
                    media.TryGetProperty("_aImages", out var images) &&
                    images.GetArrayLength() > 0)
                {
                    var img = images[0];
                    if (img.TryGetProperty("_sBaseUrl", out var baseUrl))
                    {
                        var file = TryGetStr(img, "_sFile530") ?? TryGetStr(img, "_sFile220") ?? TryGetStr(img, "_sFile");
                        if (file != null)
                            thumbUrl = $"{baseUrl.GetString()}/{file}";
                    }
                }

                var author = "Unknown";
                if (rec.TryGetProperty("_aSubmitter", out var sub) &&
                    sub.TryGetProperty("_sName", out var sName))
                    author = sName.GetString() ?? "Unknown";

                items.Add(new NewModItem
                {
                    Name = rec.GetProperty("_sName").GetString() ?? "",
                    Author = author,
                    ThumbnailUrl = thumbUrl,
                    ProfileUrl = rec.TryGetProperty("_sProfileUrl", out var pUrl) ? pUrl.GetString() ?? "" : "",
                    DateAdded = rec.TryGetProperty("_tsDateAdded", out var ts)
                        ? DateTimeOffset.FromUnixTimeSeconds(ts.GetInt64()).DateTime
                        : DateTime.UtcNow
                });
            }

            _cachedMods = items;
            _modsCachedAt = DateTime.UtcNow;
            LastGameBananaLoadSucceeded = true;
            Logger.LogInfo($"[HomeFeed] Loaded {items.Count} GameBanana mods (NSFW filtered)");
            return items;
        }
        catch (Exception ex)
        {
            LastGameBananaLoadSucceeded = false;
            Logger.LogWarning($"[HomeFeed] GameBanana fetch failed: {ex.Message}");
            return [];
        }
    }

    public void InvalidateCache()
    {
        _newsCachedAt = DateTime.MinValue;
        _modsCachedAt = DateTime.MinValue;
    }

    private static string? ExtractFirstImageUrl(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var img = _imgSrcRegex.Match(html);
        if (img.Success) return img.Groups[1].Value;

        // Community Announcements: resolve the clan-image macro to the real CDN URL.
        var clan = _clanImageRegex.Match(html);
        if (clan.Success)
            return "https://clan.cloudflare.steamstatic.com/images/" + clan.Groups["path"].Value;

        var md = _markdownImgRegex.Match(html);
        if (md.Success) return md.Groups[1].Value;

        return null;
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var noTags = Regex.Replace(html, "<[^>]+>", " ");
        noTags = _clanImageStripRegex.Replace(noTags, " "); // "{STEAM_CLAN_LOC_IMAGE}/..." noise
        noTags = _bbcodeRegex.Replace(noTags, " ");          // [img], [url=...], [b], ...
        var collapsed = Regex.Replace(noTags, @"\s+", " ").Trim();
        return collapsed
            .Replace("&nbsp;", " ")
            .Replace("&amp;", "&")
            .Replace("&quot;", "\"")
            .Replace("&#39;", "'")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">");
    }

    private static string? TryGetStr(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v))
        {
            var s = v.GetString();
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }
        return null;
    }
}
