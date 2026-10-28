using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Eternal.Core.Logging;

namespace Eternal.Core.Feed;

public sealed record NewsItem(string Title, string Summary, string Url, string? ImageUrl, DateTimeOffset Date, string Source);

public sealed record GameBananaMod(long Id, string Name, string Author, string? ImageUrl, string Url, DateTimeOffset Date, string Category);

public sealed record GameBananaFile(long Id, string FileName, long Size, string DownloadUrl, string? Md5, string? Description, bool Clean);

public sealed record UpdateInfo(string Version, string Url);

public static partial class FeedService
{
    private const int GameBananaTf2 = 297;
    private const long MaxDownloadBytes = 2L * 1024 * 1024 * 1024;

    [GeneratedRegex(@"\[/?[a-zA-Z0-9*]+(?:=[^\]]*)?\]|<[^>]+>")]
    private static partial Regex Markup();

    [GeneratedRegex(@"\{STEAM_CLAN_IMAGE\}(?<path>/[^\s\[\]""<>]+\.(?:png|jpe?g|gif|webp))", RegexOptions.IgnoreCase)]
    private static partial Regex ClanImage();

    [GeneratedRegex(@"https?://[^\s\[\]""<>]+\.(?:png|jpe?g|gif|webp)", RegexOptions.IgnoreCase)]
    private static partial Regex AnyImage();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public static async Task<IReadOnlyList<NewsItem>> SteamNewsAsync(int count = 10, CancellationToken cancellation = default)
    {
        var url = $"https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid=440&count={count}&maxlength=0&format=json";
        using var doc = JsonDocument.Parse(await Http.Client.GetStringAsync(url, cancellation).ConfigureAwait(false));
        var items = new List<NewsItem>();
        foreach (var item in doc.RootElement.GetProperty("appnews").GetProperty("newsitems").EnumerateArray())
        {
            var contents = item.TryGetProperty("contents", out var c) ? c.GetString() ?? "" : "";
            var image = ClanImage().Match(contents) is { Success: true } clan
                ? "https://clan.cloudflare.steamstatic.com/images" + clan.Groups["path"].Value
                : AnyImage().Match(contents) is { Success: true } any ? any.Value : null;
            var text = Whitespace().Replace(WebUtility.HtmlDecode(Markup().Replace(ClanImage().Replace(contents, ""), " ")), " ").Trim();
            items.Add(new NewsItem(
                item.GetProperty("title").GetString() ?? "",
                text.Length > 220 ? text[..220].TrimEnd() + "…" : text,
                item.GetProperty("url").GetString() ?? "",
                image,
                DateTimeOffset.FromUnixTimeSeconds(item.GetProperty("date").GetInt64()),
                item.TryGetProperty("feedlabel", out var label) ? label.GetString() ?? "Steam" : "Steam"));
        }
        return items;
    }

    public static async Task<IReadOnlyList<GameBananaMod>> GameBananaLatestAsync(int count = 12, CancellationToken cancellation = default)
    {
        var url = "https://gamebanana.com/apiv11/Mod/Index?_nPage=1" +
                  $"&_nPerpage={count}&_csvProperties=_idRow,_sName,_aPreviewMedia,_aSubmitter,_tsDateAdded,_sProfileUrl,_bHasContentRatings,_aRootCategory" +
                  $"&_aFilters%5BGeneric_Game%5D={GameBananaTf2}&_sSort=Generic_LatestModified";
        using var doc = JsonDocument.Parse(await Http.Client.GetStringAsync(url, cancellation).ConfigureAwait(false));
        var mods = new List<GameBananaMod>();
        foreach (var record in doc.RootElement.GetProperty("_aRecords").EnumerateArray())
        {
            if (record.TryGetProperty("_bHasContentRatings", out var rated) && rated.ValueKind == JsonValueKind.True)
                continue;
            string? image = null;
            if (record.TryGetProperty("_aPreviewMedia", out var media) && media.TryGetProperty("_aImages", out var images)
                && images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0)
            {
                var first = images[0];
                var file = Str(first, "_sFile530") ?? Str(first, "_sFile220") ?? Str(first, "_sFile");
                if (Str(first, "_sBaseUrl") is { } baseUrl && file is not null)
                    image = $"{baseUrl}/{file}";
            }
            mods.Add(new GameBananaMod(
                record.GetProperty("_idRow").GetInt64(),
                Str(record, "_sName") ?? "",
                record.TryGetProperty("_aSubmitter", out var sub) ? Str(sub, "_sName") ?? "" : "",
                image,
                Str(record, "_sProfileUrl") ?? $"https://gamebanana.com/mods/{record.GetProperty("_idRow").GetInt64()}",
                record.TryGetProperty("_tsDateAdded", out var ts) && ts.TryGetInt64(out var t) ? DateTimeOffset.FromUnixTimeSeconds(t) : DateTimeOffset.MinValue,
                record.TryGetProperty("_aRootCategory", out var cat) ? Str(cat, "_sName") ?? "" : ""));
        }
        return mods;
    }

    public static async Task<IReadOnlyList<GameBananaFile>> GameBananaFilesAsync(long modId, CancellationToken cancellation = default)
    {
        using var doc = JsonDocument.Parse(await Http.Client.GetStringAsync(
            $"https://gamebanana.com/apiv11/Mod/{modId}?_csvProperties=_aFiles", cancellation).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("_aFiles", out var files) || files.ValueKind != JsonValueKind.Array)
            return [];
        return files.EnumerateArray().Select(f => new GameBananaFile(
            f.GetProperty("_idRow").GetInt64(),
            Str(f, "_sFile") ?? "download",
            f.TryGetProperty("_nFilesize", out var size) && size.TryGetInt64(out var s) ? s : 0,
            Str(f, "_sDownloadUrl") ?? "",
            Str(f, "_sMd5Checksum"),
            Str(f, "_sDescription"),
            Str(f, "_sAvResult") is null or "clean")).ToArray();
    }

    /// <summary>Downloads a GameBanana file into <paramref name="folder"/> and verifies its checksum.</summary>
    public static async Task<string> DownloadAsync(GameBananaFile file, string folder, IProgress<double>? progress, CancellationToken cancellation = default)
    {
        if (!file.Clean)
            throw new InvalidOperationException($"GameBanana's virus scan flagged {file.FileName}; it won't be downloaded.");
        if (!Uri.TryCreate(file.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !(uri.Host == "gamebanana.com" || uri.Host.EndsWith(".gamebanana.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Unexpected download location.");
        if (file.Size > MaxDownloadBytes)
            throw new InvalidOperationException("That file is too large.");

        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, Path.GetFileName(file.FileName));
        using var response = await Http.Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? file.Size;

        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
            await using var output = File.Create(target);
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
            {
                read += n;
                if (read > MaxDownloadBytes)
                    throw new InvalidOperationException("Download exceeded the size limit.");
                await output.WriteAsync(buffer.AsMemory(0, n), cancellation).ConfigureAwait(false);
                if (total > 0)
                    progress?.Report((double)read / total);
            }
        }
        catch
        {
            File.Delete(target);
            throw;
        }

        if (!string.IsNullOrWhiteSpace(file.Md5))
        {
            await using var check = File.OpenRead(target);
            var hash = Convert.ToHexString(await MD5.HashDataAsync(check, cancellation).ConfigureAwait(false));
            if (!hash.Equals(file.Md5, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(target);
                throw new InvalidOperationException("The download was corrupted (checksum mismatch). Try again.");
            }
        }
        Log.Info($"Downloaded {file.FileName} from GameBanana");
        return target;
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
