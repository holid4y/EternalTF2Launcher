using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Eternal.Core.Logging;

namespace Eternal.Core.GameBanana;

public sealed class GameBananaException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Browsing and downloading from GameBanana's TF2 section. What the API sends is
/// untrusted: ids have to be numbers, pictures and downloads have to come from
/// gamebanana.com over https, descriptions are reduced to plain text and every
/// download is held to its announced size and MD5 before it is kept.
/// </summary>
public sealed partial class GameBananaClient(HttpClient http)
{
    public const int Tf2 = 297;
    public const int PageSize = 24;
    public const long MaxDownloadBytes = 4L * 1024 * 1024 * 1024;
    private const string Api = "https://gamebanana.com/apiv11";
    private const int MaxJsonBytes = 8 * 1024 * 1024;
    private const int MaxTextChars = 8000;
    private static readonly string[] Installable = [".zip", ".rar", ".7z", ".vpk"];
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(60);
    private const int MaxRedirects = 5;

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, string Json)> _cache = new();

    public static GameBananaClient Shared { get; } = new(Http.NoRedirects);

    public Task<IReadOnlyList<Category>> CategoriesAsync(Section section, long? parent = null, CancellationToken cancellation = default) =>
        FetchAsync(parent is > 0
            ? $"{Api}/{Model(section)}/Categories?_idCategoryRow={parent}&_sSort=a_to_z"
            : $"{Api}/{Model(section)}/Categories?_idGameRow={Tf2}&_sSort=a_to_z", ParseCategories, cancellation);

    public Task<BrowsePage> BrowseAsync(BrowseQuery query, CancellationToken cancellation = default) =>
        FetchAsync(IndexUrl(query), json => ParsePage(json, query.Section, query.Page), cancellation);

    public Task<SubmissionDetails> DetailsAsync(Section section, long id, CancellationToken cancellation = default) =>
        FetchAsync($"{Api}/{Model(section)}/{id}/ProfilePage", json => ParseDetails(json, section), cancellation);

    /// <summary>Downloads into its own folder under <paramref name="folder"/>; the file only appears once it is complete and verified.</summary>
    public async Task<string> DownloadAsync(SubmissionFile file, string folder, IProgress<double>? progress, CancellationToken cancellation = default)
    {
        if (file.Scan == FileScan.Flagged)
            throw new GameBananaException($"GameBanana's scan flagged {file.Name}, so it won't be downloaded.");
        if (file.Scan == FileScan.Pending)
            throw new GameBananaException($"GameBanana hasn't finished scanning {file.Name}. Try again later.");
        if (!IsInstallable(file.Name))
            throw new GameBananaException($"{file.Name} isn't a ZIP, RAR, 7z or VPK, so it can't be installed.");
        if (file.Size > MaxDownloadBytes)
            throw new GameBananaException($"{file.Name} is larger than {MaxDownloadBytes >> 30} GB.");

        var directory = Path.Combine(folder, file.Id.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, SafeFileName(file.Name));
        var partial = target + ".part";
        var limit = file.Size > 0 ? file.Size : MaxDownloadBytes;
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        try
        {
            stall.CancelAfter(StallLimit);
            using var response = await GetAsync(file.DownloadUrl, stall.Token).ConfigureAwait(false);
            Check(response);
            if (response.RequestMessage?.RequestUri is not { } final || !IsGameBananaUrl(final.AbsoluteUri))
                throw new GameBananaException($"The download of {file.Name} was sent somewhere other than GameBanana, so it was stopped.");
            if (response.Content.Headers.ContentLength > limit)
                throw new GameBananaException($"{file.Name} is bigger than GameBanana says it is, so it wasn't downloaded.");

            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            long read = 0;
            try
            {
                await using var input = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
                await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                int n;
                while ((n = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
                {
                    stall.CancelAfter(StallLimit);
                    read += n;
                    if (read > limit)
                        throw new GameBananaException($"{file.Name} is bigger than GameBanana says it is, so the download was stopped.");
                    md5.AppendData(buffer, 0, n);
                    await output.WriteAsync(buffer.AsMemory(0, n), stall.Token).ConfigureAwait(false);
                    progress?.Report((double)read / limit);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (file.Size > 0 && read != file.Size)
                throw new GameBananaException($"{file.Name} arrived incomplete. Try again.");
            if (file.Md5 is { } expected && !Convert.ToHexString(md5.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new GameBananaException($"{file.Name} arrived damaged: its checksum doesn't match GameBanana's. Try again.");
            File.Move(partial, target, overwrite: true);
            Log.Info($"Downloaded {file.Name} ({read} bytes) from GameBanana");
            return target;
        }
        catch (OperationCanceledException ex) when (!cancellation.IsCancellationRequested)
        {
            throw new GameBananaException($"The download of {file.Name} stalled. Try again.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new GameBananaException($"{file.Name} couldn't be downloaded: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new GameBananaException($"{file.Name} couldn't be saved: {ex.Message}", ex);
        }
        finally
        {
            File.Delete(partial);
        }
    }

    private static async Task<string> ReadJsonAsync(HttpContent content, CancellationToken cancellation)
    {
        if (content.Headers.ContentLength > MaxJsonBytes)
            throw new GameBananaException("GameBanana sent more than expected. Try again later.");
        await using var input = await content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var json = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
        {
            if (json.Length + n > MaxJsonBytes)
                throw new GameBananaException("GameBanana sent more than expected. Try again later.");
            json.Write(buffer, 0, n);
        }
        return Encoding.UTF8.GetString(json.GetBuffer(), 0, (int)json.Length);
    }

    private async Task<HttpResponseMessage> GetAsync(string url, CancellationToken cancellation)
    {
        var uri = new Uri(url);
        for (var hop = 0; ; hop++)
        {
            var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            if ((int)response.StatusCode is < 300 or > 399 || response.Headers.Location is not { } location)
                return response;
            response.Dispose();
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
            if (!IsGameBananaUrl(uri.AbsoluteUri))
                throw new GameBananaException("GameBanana sent the request somewhere else, so it was stopped.");
            if (hop == MaxRedirects)
                throw new GameBananaException("GameBanana kept redirecting. Try again later.");
        }
    }

    private async Task<T> FetchAsync<T>(string url, Func<string, T> parse, CancellationToken cancellation)
    {
        string json;
        if (_cache.TryGetValue(url, out var hit) && DateTimeOffset.UtcNow - hit.At < CacheFor)
        {
            json = hit.Json;
        }
        else
        {
            try
            {
                using var response = await GetAsync(url, cancellation).ConfigureAwait(false);
                Check(response);
                json = await ReadJsonAsync(response.Content, cancellation).ConfigureAwait(false);
            }
            catch (TaskCanceledException ex) when (!cancellation.IsCancellationRequested)
            {
                throw new GameBananaException("GameBanana took too long to answer. Try again.", ex);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                throw new GameBananaException("GameBanana couldn't be reached. Check your connection and try again.", ex);
            }
        }

        try
        {
            var result = parse(json);
            _cache[url] = (DateTimeOffset.UtcNow, json);
            if (_cache.Count > 300)
            {
                foreach (var stale in _cache.Where(e => DateTimeOffset.UtcNow - e.Value.At >= CacheFor).Select(e => e.Key).ToList())
                    _cache.TryRemove(stale, out _);
            }
            return result;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            Log.Warn($"Unexpected answer from {url}: {ex.Message}");
            throw new GameBananaException("GameBanana sent something the launcher doesn't understand. Try again later.", ex);
        }
    }

    private static void Check(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new GameBananaException("GameBanana is getting too many requests. Wait a minute and try again.");
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new GameBananaException("GameBanana doesn't have that anymore.");
        if (!response.IsSuccessStatusCode)
            throw new GameBananaException($"GameBanana answered with an error ({(int)response.StatusCode}). Try again later.");
    }

    internal static string IndexUrl(BrowseQuery query)
    {
        var url = new StringBuilder($"{Api}/{Model(query.Section)}/Index?_nPage={Math.Max(1, query.Page)}&_nPerpage={PageSize}")
            .Append(CultureInfo.InvariantCulture, $"&_aFilters%5BGeneric_Game%5D={Tf2}");
        if (query.CategoryId is > 0)
            url.Append(CultureInfo.InvariantCulture, $"&_aFilters%5BGeneric_Category%5D={query.CategoryId}");
        var search = SearchText(query.Search);
        if (search.Length >= 2)
            url.Append("&_aFilters%5BGeneric_Name%5D=").Append(Uri.EscapeDataString("contains," + search));
        return url.Append("&_sSort=").Append(query.Sort switch
        {
            BrowseSort.MostLiked => "Generic_MostLiked",
            BrowseSort.MostViewed => "Generic_MostViewed",
            BrowseSort.RecentlyUpdated => "Generic_LatestUpdated",
            BrowseSort.Newest => "Generic_Newest",
            BrowseSort.Name => "Generic_Alphabetically",
            _ => "Generic_MostDownloaded",
        }).ToString();
    }

    // The API splits its filter on commas and has no use for control characters.
    public static string SearchText(string? text)
    {
        var cleaned = new string((text ?? "").Select(c => c == ',' || char.IsControl(c) ? ' ' : c).ToArray());
        cleaned = Whitespace().Replace(cleaned, " ").Trim();
        return cleaned.Length > 80 ? cleaned[..80].TrimEnd() : cleaned;
    }

    private static string Model(Section section) => section == Section.Sounds ? "Sound" : "Mod";

    internal static IReadOnlyList<Category> ParseCategories(string json)
    {
        using var doc = JsonDocument.Parse(json);
        ThrowOnError(doc.RootElement);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return [];
        return doc.RootElement.EnumerateArray()
            .Select(c => Id(c) is { } id && Text(c, "_sName") is { Length: > 0 } name
                ? new Category(id, Clip(name, 80), Int(c, "_nItemCount"), Int(c, "_nCategoryCount"))
                : null)
            .OfType<Category>()
            .ToArray();
    }

    internal static BrowsePage ParsePage(string json, Section section, int page)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        ThrowOnError(root);
        var total = root.TryGetProperty("_aMetadata", out var meta) ? Int(meta, "_nRecordCount") : 0;
        var items = root.TryGetProperty("_aRecords", out var records) && records.ValueKind == JsonValueKind.Array
            ? records.EnumerateArray().Select(r => ParseSubmission(r, section)).OfType<Submission>().ToArray()
            : [];
        var pages = Math.Max(1, (total + PageSize - 1) / PageSize);
        return new BrowsePage(items, total, Math.Clamp(page, 1, pages), pages);
    }

    internal static SubmissionDetails ParseDetails(string json, Section section)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        ThrowOnError(root);
        var summary = ParseSubmission(root, section)
            ?? throw new GameBananaException("This submission isn't available in the launcher.");

        var screenshots = Images(root).Select(i => ImageUrl(i, "_sFile") ?? ImageUrl(i, "_sFile530")).OfType<string>().Take(12).ToArray();
        var audio = Object(Object(root, "_aPreviewMedia"), "_aMetadata") is { } media && Text(media, "_sAudioUrl") is { } a && IsGameBananaUrl(a) ? a : null;
        var files = root.TryGetProperty("_aFiles", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(ParseFile).OfType<SubmissionFile>().ToArray()
            : [];
        return new SubmissionDetails(summary, PlainText(Text(root, "_sText")), screenshots, audio,
            Int(root, "_nDownloadCount"), Text(root, "_sVersion") is { Length: > 0 } v ? Clip(v, 40) : null, files);
    }

    private static Submission? ParseSubmission(JsonElement r, Section section)
    {
        if (Id(r) is not { } id || Text(r, "_sName") is not { Length: > 0 } name
            || Text(r, "_sInitialVisibility") is { } shown && shown != "show")
            return null;
        var category = Text(Object(r, "_aSubCategory"), "_sName") ?? Text(Object(r, "_aRootCategory"), "_sName") ?? Text(Object(r, "_aCategory"), "_sName") ?? "";
        return new Submission(
            id, section, Clip(name, 120),
            Clip(Text(Object(r, "_aSubmitter"), "_sName") ?? "", 60),
            Images(r).Select(i => ImageUrl(i, "_sFile530") ?? ImageUrl(i, "_sFile220") ?? ImageUrl(i, "_sFile")).FirstOrDefault(u => u is not null),
            Clip(category, 60),
            Time(r, "_tsDateUpdated") ?? Time(r, "_tsDateModified") ?? Time(r, "_tsDateAdded") ?? DateTimeOffset.UnixEpoch,
            Int(r, "_nLikeCount"), Int(r, "_nViewCount"),
            Tags(r).Any(t => t.StartsWith("Casual Compatibility", StringComparison.OrdinalIgnoreCase)),
            Bool(r, "_bIsObsolete"),
            Bool(r, "_bHasContentRatings"));
    }

    private static SubmissionFile? ParseFile(JsonElement f)
    {
        if (Id(f) is not { } id || Text(f, "_sFile") is not { Length: > 0 } name)
            return null;
        var md5 = Text(f, "_sMd5Checksum");
        return new SubmissionFile(id, SafeFileName(name), Math.Max(0, Long(f, "_nFilesize")),
            Text(f, "_sDescription") is { Length: > 0 } d ? Clip(PlainText(d), 300) : null,
            Time(f, "_tsDateAdded") ?? DateTimeOffset.UnixEpoch, Int(f, "_nDownloadCount"),
            md5 is { Length: 32 } && md5.All(char.IsAsciiHexDigit) ? md5 : null,
            Scan(f));
    }

    private static FileScan Scan(JsonElement f)
    {
        var av = Text(f, "_sAvResult");
        var analysis = Text(f, "_sAnalysisResult");
        if (av is not null && av != "clean" || analysis is not null && analysis != "ok")
            return FileScan.Flagged;
        return av == "clean" ? FileScan.Clean : FileScan.Pending;
    }

    private static void ThrowOnError(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && Text(root, "_sErrorCode") is { } code)
        {
            Log.Warn($"GameBanana refused a request: {code}");
            throw new GameBananaException("GameBanana didn't accept that request.");
        }
    }

    private static IEnumerable<JsonElement> Images(JsonElement r) =>
        Object(r, "_aPreviewMedia") is { } media && media.TryGetProperty("_aImages", out var images) && images.ValueKind == JsonValueKind.Array
            ? images.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object)
            : [];

    private static string? ImageUrl(JsonElement image, string field) =>
        Text(image, "_sBaseUrl") is { } baseUrl && Text(image, field) is { } file && !file.Contains('/') && !file.Contains('\\')
            && IsGameBananaUrl($"{baseUrl}/{file}") ? $"{baseUrl}/{file}" : null;

    private static IEnumerable<string> Tags(JsonElement r) =>
        r.TryGetProperty("_aTags", out var tags) && tags.ValueKind == JsonValueKind.Array
            ? tags.EnumerateArray().Select(t => t.ValueKind switch
            {
                JsonValueKind.String => t.GetString(),
                JsonValueKind.Object => $"{Text(t, "_sTitle")}: {Text(t, "_sValue")}",
                _ => null,
            }).OfType<string>()
            : [];

    public static bool IsGameBananaUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
        && (uri.Host.Equals("gamebanana.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".gamebanana.com", StringComparison.OrdinalIgnoreCase));

    public static bool IsInstallable(string name) => Installable.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    /// <summary>A name that is safe to create on Windows and Linux and can't point outside its folder.</summary>
    public static string SafeFileName(string name)
    {
        var last = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        var invalid = Path.GetInvalidFileNameChars().Concat([':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var cleaned = new string(last.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (cleaned.Length == 0)
            return "download";
        if (Reserved.Contains(Path.GetFileNameWithoutExtension(cleaned)))
            cleaned = "_" + cleaned;
        if (cleaned.Length > 120)
        {
            var extension = Path.GetExtension(cleaned);
            cleaned = cleaned[..(120 - Math.Min(extension.Length, 10))] + extension[..Math.Min(extension.Length, 10)];
        }
        return cleaned;
    }

    /// <summary>GameBanana descriptions are HTML written by uploaders; only their words and line breaks are kept.</summary>
    public static string PlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";
        var text = Scripts().Replace(html, "");
        text = ListItem().Replace(text, "\n• ");
        text = Breaks().Replace(text, "\n");
        text = Markup().Replace(text, "");
        text = WebUtility.HtmlDecode(text).Replace(' ', ' ');
        text = string.Join('\n', text.Split('\n').Select(line => Whitespace().Replace(line, " ").Trim()));
        text = BlankLines().Replace(text, "\n\n").Trim();
        return Clip(text, MaxTextChars);
    }

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..length].TrimEnd() + "…";

    private static long? Id(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("_idRow", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var value) && value > 0
            ? value
            : null;

    private static JsonElement? Object(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static string? Text(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Int(JsonElement e, string name) => (int)Math.Clamp(Long(e, name), 0, int.MaxValue);

    private static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : 0;

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? Time(JsonElement e, string name) =>
        Long(e, name) is > 0 and < 32503680000 and var seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Scripts();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"<br\s*/?>|</(p|div|ul|ol|h\d|tr|blockquote)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex Breaks();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Markup();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();
}
