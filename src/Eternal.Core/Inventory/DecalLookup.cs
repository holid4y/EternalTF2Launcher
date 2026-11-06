using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Eternal.Core.Logging;

namespace Eternal.Core.Inventory;

public sealed partial class DecalLookup(HttpClient http, string key)
{
    private const int TextureLow = 152;
    private const int TextureHigh = 227;
    private const int MaxItemsBytes = 32 * 1024 * 1024;
    private const int MaxDetailsBytes = 64 * 1024;

    private static readonly ConcurrentDictionary<ulong, string?> Urls = new();

    [GeneratedRegex("^[A-Fa-f0-9]{32}$")]
    private static partial Regex KeyPattern();

    public static bool IsValidKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    public async Task<IReadOnlyDictionary<string, ulong>> DecalsAsync(string steamId64, CancellationToken cancellation = default)
    {
        if (!IsValidKey(key) || !ulong.TryParse(steamId64, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            return new Dictionary<string, ulong>();
        using var response = await http.GetAsync($"https://api.steampowered.com/IEconItems_440/GetPlayerItems/v1/?key={key}&steamid={steamId64}", cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Steam's item API answered {(int)response.StatusCode}.");
        return ParseDecals(await Http.ReadCappedAsync(response.Content, MaxItemsBytes, cancellation).ConfigureAwait(false));
    }

    internal static IReadOnlyDictionary<string, ulong> ParseDecals(byte[] json)
    {
        var result = new Dictionary<string, ulong>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("result", out var root) || !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Array || !item.TryGetProperty("id", out var idElement))
                continue;
            long? low = null, high = null;
            foreach (var attribute in attributes.EnumerateArray())
            {
                if (!attribute.TryGetProperty("defindex", out var d) || !TryNumber(d, out var defindex)
                    || !attribute.TryGetProperty("value", out var v) || !TryNumber(v, out var value))
                    continue;
                if (defindex == TextureLow)
                    low = value;
                else if (defindex == TextureHigh)
                    high = value;
            }
            var id = idElement.ValueKind switch
            {
                JsonValueKind.Number when idElement.TryGetUInt64(out var idNum) => idNum.ToString(CultureInfo.InvariantCulture),
                JsonValueKind.String => idElement.GetString(),
                _ => null,
            };
            if (low is >= 0 and { } lo && high is >= 0 and { } hi && id is not null && ((ulong)hi << 32 | (ulong)lo) is > 0 and var ugc)
                result[id] = ugc;
        }
        return result;
    }

    private static bool TryNumber(JsonElement e, out long value)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Number when e.TryGetInt64(out value):
                return true;
            case JsonValueKind.String when long.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value):
                return true;
            default:
                value = 0;
                return false;
        }
    }

    public async Task<string?> UrlAsync(ulong ugc, CancellationToken cancellation = default)
    {
        if (Urls.TryGetValue(ugc, out var known))
            return known;
        try
        {
            using var response = await http.GetAsync($"https://api.steampowered.com/ISteamRemoteStorage/GetUGCFileDetails/v1/?key={key}&appid=440&ugcid={ugc.ToString(CultureInfo.InvariantCulture)}", cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            var url = ParseUrl(await Http.ReadCappedAsync(response.Content, MaxDetailsBytes, cancellation).ConfigureAwait(false));
            Urls[ugc] = url;
            return url;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException or IOException or TaskCanceledException)
        {
            Log.Debug($"No decal picture for {ugc}: {ex.Message}");
            return null;
        }
    }

    internal static string? ParseUrl(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String
               && Uri.TryCreate(u.GetString(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
               && uri.Host.EndsWith(".steamusercontent.com", StringComparison.OrdinalIgnoreCase)
            ? uri.AbsoluteUri
            : null;
    }
}
