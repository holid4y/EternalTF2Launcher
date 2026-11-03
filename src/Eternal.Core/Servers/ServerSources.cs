using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Eternal.Core.Logging;

namespace Eternal.Core.Servers;

/// <summary>A server some public list knows about. Live details come from querying it directly.</summary>
public sealed record ListedServer(
    ServerAddress Address,
    int QueryPort,
    string Name,
    string Map,
    int Players,
    int MaxPlayers,
    string? Country,
    bool? Password,
    string Source);

public sealed class ServerListException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// GameMonitoring's open API: TF2 servers sorted by player count, a hundred per
/// page. No key. About five pages cover every server with people on it.
/// </summary>
public sealed class GameMonitoringSource(HttpClient http)
{
    public const string Name = "GameMonitoring";
    private const string Api = "https://api.gamemonitoring.net/servers?game=440&limit=100&sort=numplayers";
    private const int PageSize = 100;
    private const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>Servers with players, then (for <paramref name="country"/>) the empty ones too.</summary>
    public async IAsyncEnumerable<IReadOnlyList<ListedServer>> ListAsync(string? country, int maxPages, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        await foreach (var page in PagesAsync("", populatedOnly: true, maxPages, cancellation).ConfigureAwait(false))
            yield return page;
        if (country is { Length: 2 } && country.All(char.IsAsciiLetterUpper))
        {
            await foreach (var page in PagesAsync($"&country={country}", populatedOnly: false, 3, cancellation).ConfigureAwait(false))
                yield return page;
        }
    }

    private async IAsyncEnumerable<IReadOnlyList<ListedServer>> PagesAsync(string filter, bool populatedOnly, int maxPages, [EnumeratorCancellation] CancellationToken cancellation)
    {
        for (var page = 0; page < maxPages; page++)
        {
            var url = $"{Api}{filter}&offset={(page * PageSize).ToString(CultureInfo.InvariantCulture)}";
            var (servers, count) = await FetchAsync(url, cancellation).ConfigureAwait(false);
            var keep = populatedOnly ? servers.Where(s => s.Players > 0).ToList() : servers;
            if (keep.Count > 0)
                yield return keep;
            if (count < PageSize || (populatedOnly && keep.Count < servers.Count))
                yield break;
        }
    }

    private async Task<(IReadOnlyList<ListedServer> Servers, int Count)> FetchAsync(string url, CancellationToken cancellation)
    {
        byte[] body;
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new ServerListException("GameMonitoring asked to slow down. Try again in a minute.");
            if (!response.IsSuccessStatusCode)
                throw new ServerListException($"GameMonitoring answered {(int)response.StatusCode}.");
            body = await Http.ReadCappedAsync(response.Content, MaxBytes, cancellation).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException || ex is TaskCanceledException && !cancellation.IsCancellationRequested)
        {
            throw new ServerListException("GameMonitoring couldn't be reached.", ex);
        }
        try
        {
            return Parse(body);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new ServerListException("GameMonitoring sent something unexpected.", ex);
        }
    }

    internal static (IReadOnlyList<ListedServer> Servers, int Count) Parse(ReadOnlySpan<byte> json)
    {
        using var doc = JsonDocument.Parse(json.ToArray());
        var items = doc.RootElement.GetProperty("response").GetProperty("items");
        var result = new List<ListedServer>();
        var count = 0;
        foreach (var item in items.EnumerateArray())
        {
            count++;
            if (item.ValueKind != JsonValueKind.Object || !Json.Bool(item, "status"))
                continue;
            var port = Json.Int(item, "port");
            if (ServerAddress.From(Json.Text(item, "ip"), port) is not { IsPublic: true } address)
                continue;
            var query = Json.Int(item, "query");
            result.Add(new ListedServer(
                address,
                query is > 0 and <= 65535 ? query : port,
                SourceQuery.Clean(Json.Text(item, "name") ?? ""),
                SourceQuery.Clean(Json.Text(item, "map") ?? ""),
                Math.Clamp(Json.Int(item, "numplayers"), 0, 255),
                Math.Clamp(Json.Int(item, "maxplayers"), 0, 255),
                Json.Text(item, "country") is { Length: 2 } c && c.All(char.IsAsciiLetter) ? c.ToUpperInvariant() : null,
                null,
                Name));
        }
        return (result, count);
    }
}

/// <summary>
/// teamwork.tf's community server lists, one request per game mode. Needs the
/// player's own key, which only ever goes to teamwork.tf and never into a log.
/// </summary>
public sealed partial class TeamworkSource(HttpClient http, string key)
{
    public const string Name = "teamwork.tf";
    private const string Api = "https://teamwork.tf/api/v1/";
    private const int MaxBytes = 4 * 1024 * 1024;
    private const int MaxModes = 64;
    private static readonly TimeSpan Spacing = TimeSpan.FromMilliseconds(700);

    [GeneratedRegex("^[A-Za-z0-9]{16,64}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^[a-z0-9-]{1,40}$")]
    private static partial Regex ModePattern();

    public static bool IsValidKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    public async IAsyncEnumerable<IReadOnlyList<ListedServer>> ListAsync([EnumeratorCancellation] CancellationToken cancellation = default)
    {
        if (!IsValidKey(key))
            throw new ServerListException("That teamwork.tf key doesn't look right.");
        using var modesDoc = await FetchAsync("quickplay", cancellation).ConfigureAwait(false);
        var modes = new List<string>();
        foreach (var group in new[] { "gamemodes_official", "gamemodes_community" })
        {
            if (!modesDoc.RootElement.TryGetProperty(group, out var list) || list.ValueKind != JsonValueKind.Array)
                continue;
            modes.AddRange(list.EnumerateArray().Select(m => Json.Text(m, "id")).OfType<string>().Where(id => ModePattern().IsMatch(id)));
        }
        foreach (var mode in modes.Distinct().Take(MaxModes))
        {
            await Task.Delay(Spacing, cancellation).ConfigureAwait(false);
            IReadOnlyList<ListedServer> servers;
            try
            {
                using var doc = await FetchAsync($"quickplay/{mode}/servers", cancellation).ConfigureAwait(false);
                servers = Parse(doc.RootElement);
            }
            catch (ServerListException ex) when (ex.InnerException is not null)
            {
                Log.Debug($"teamwork.tf {mode}: {ex.Message}");
                continue;
            }
            if (servers.Count > 0)
                yield return servers;
        }
    }

    internal static IReadOnlyList<ListedServer> Parse(JsonElement root)
    {
        var items = root.ValueKind == JsonValueKind.Array ? root : root.TryGetProperty("servers", out var s) ? s : default;
        if (items.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<ListedServer>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || item.TryGetProperty("reachable", out var r) && r.ValueKind == JsonValueKind.False)
                continue;
            var port = Json.Int(item, "port");
            if (ServerAddress.From(Json.Text(item, "ip"), port) is not { IsPublic: true } address)
                continue;
            result.Add(new ListedServer(
                address,
                port,
                SourceQuery.Clean(Json.Text(item, "name") ?? ""),
                SourceQuery.Clean(Json.Text(item, "map_name") ?? ""),
                Math.Clamp(Json.Int(item, "players"), 0, 255),
                Math.Clamp(Json.Int(item, "max_players"), 0, 255),
                null,
                item.TryGetProperty("has_password", out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False ? p.GetBoolean() : null,
                Name));
        }
        return result;
    }

    private async Task<JsonDocument> FetchAsync(string path, CancellationToken cancellation)
    {
        try
        {
            using var response = await http.GetAsync($"{Api}{path}?key={key}", HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || (int)response.StatusCode is >= 300 and < 400)
                throw new ServerListException("teamwork.tf didn't accept the key. Check it in Launcher settings.");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new ServerListException("teamwork.tf asked to slow down. Its list comes back in a few minutes.");
            if (!response.IsSuccessStatusCode)
                throw new ServerListException($"teamwork.tf answered {(int)response.StatusCode}.");
            var body = await Http.ReadCappedAsync(response.Content, MaxBytes, cancellation).ConfigureAwait(false);
            return JsonDocument.Parse(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or JsonException || ex is TaskCanceledException && !cancellation.IsCancellationRequested)
        {
            throw new ServerListException("teamwork.tf couldn't be reached.", ex);
        }
    }
}

internal static class Json
{
    public static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static int Int(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            _ => 0,
        };
    }

    public static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
