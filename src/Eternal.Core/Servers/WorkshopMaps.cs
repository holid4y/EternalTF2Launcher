using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Eternal.Core.Logging;

namespace Eternal.Core.Servers;

/// <summary>
/// Preview pictures for Workshop maps. A server running one reports its map as
/// "workshop/cp_name.ugc123456", and that number is the Workshop item, which
/// Steam describes without an API key. Other maps have no picture.
/// </summary>
public sealed partial class WorkshopMaps(HttpClient http)
{
    private const string Details = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";
    private const int MaxBytes = 256 * 1024;
    private static readonly string[] ImageHosts = ["steamuserimages-a.akamaihd.net", "images.steamusercontent.com"];

    private readonly ConcurrentDictionary<ulong, Task<string?>> _previews = new();

    public static WorkshopMaps Shared { get; } = new(Http.NoRedirects);

    [GeneratedRegex(@"^workshop/(?:[A-Za-z0-9_\-]{1,64}\.ugc)?(\d{1,20})$")]
    private static partial Regex WorkshopMap();

    public static ulong? WorkshopId(string? map) =>
        map is not null && WorkshopMap().Match(map) is { Success: true } m
        && ulong.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;

    /// <summary>The map's name without the Workshop prefix and item number.</summary>
    public static string DisplayName(string map)
    {
        if (!map.StartsWith("workshop/", StringComparison.Ordinal))
            return map;
        var name = map["workshop/".Length..];
        var ugc = name.LastIndexOf(".ugc", StringComparison.Ordinal);
        return ugc > 0 ? name[..ugc] : name;
    }

    public Task<string?> PreviewAsync(string? map, CancellationToken cancellation = default) =>
        WorkshopId(map) is { } id ? _previews.GetOrAdd(id, i => FetchAsync(i, cancellation)) : Task.FromResult<string?>(null);

    private async Task<string?> FetchAsync(ulong id, CancellationToken cancellation)
    {
        try
        {
            using var form = new FormUrlEncodedContent([
                new("itemcount", "1"),
                new("publishedfileids[0]", id.ToString(CultureInfo.InvariantCulture)),
            ]);
            using var response = await http.PostAsync(Details, form, cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            var body = await Http.ReadCappedAsync(response.Content, MaxBytes, cancellation).ConfigureAwait(false);
            return ParsePreview(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or JsonException or OperationCanceledException)
        {
            Log.Debug($"No Workshop picture for {id}: {ex.Message}");
            _previews.TryRemove(id, out _);
            return null;
        }
    }

    internal static string? ParsePreview(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("response", out var response)
            || !response.TryGetProperty("publishedfiledetails", out var list) || list.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var item in list.EnumerateArray())
        {
            if (Json.Int(item, "result") != 1 || Json.Int(item, "consumer_app_id") != 440)
                continue;
            if (Json.Text(item, "preview_url") is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps && ImageHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
                return uri.AbsoluteUri;
        }
        return null;
    }
}
