using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eternal.Core.Logging;
using Eternal.Core.Persistence;

namespace Eternal.Core.Inventory;

public enum InventorySource { Live, Cache, StaleCache }

public sealed record InventorySnapshot(string SteamId, IReadOnlyList<InventoryItem> Items, DateTimeOffset FetchedAt, InventorySource Source, string? Notice = null);

public class InventoryException(string message) : Exception(message);

public sealed class InventoryRateLimitedException() : InventoryException("Steam is rate limiting inventory requests.");

/// <summary>
/// The public TF2 backpack from the Steam Community, read without cookies or
/// credentials. Each account has its own cache file, served only to that account.
/// </summary>
public sealed class InventoryService(string cacheDir)
{
    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(10);
    private const int AppId = 440;
    private const int ContextId = 2;

    private string CachePath(string steamId) => Path.Combine(cacheDir, $"{steamId}.json");

    public static bool IsSteamId64(string? id) => id is { Length: 17 } && id.All(char.IsAsciiDigit) && id.StartsWith("7656", StringComparison.Ordinal);

    public async Task<InventorySnapshot> LoadAsync(string steamId, bool force, CancellationToken cancellation = default)
    {
        if (!IsSteamId64(steamId))
            throw new InventoryException("That doesn't look like a SteamID64.");

        var cached = ReadCache(steamId);
        if (!force && cached is not null && DateTimeOffset.UtcNow - cached.FetchedAt < FreshFor)
            return Snapshot(cached, InventorySource.Cache);

        try
        {
            var items = await FetchAsync(steamId, cancellation).ConfigureAwait(false);
            var envelope = new InventoryCacheFile { SteamId = steamId, AppId = AppId, ContextId = ContextId, FetchedAt = DateTimeOffset.UtcNow, Items = items };
            WriteCache(envelope);
            return Snapshot(envelope, InventorySource.Live);
        }
        catch (InventoryRateLimitedException) when (cached is not null)
        {
            return Snapshot(cached, InventorySource.StaleCache, "Steam is rate limiting requests, so this is the last saved copy. Try again in a few minutes.");
        }
        catch (HttpRequestException) when (cached is not null)
        {
            return Snapshot(cached, InventorySource.StaleCache, "Steam couldn't be reached, so this is the last saved copy.");
        }
    }

    private static InventorySnapshot Snapshot(InventoryCacheFile file, InventorySource source, string? notice = null)
    {
        Log.Info($"Backpack: {file.Items.Count} items ({source})");
        return new(file.SteamId, file.Items, file.FetchedAt, source, notice);
    }

    private static async Task<List<InventoryItem>> FetchAsync(string steamId, CancellationToken cancellation)
    {
        var items = new List<InventoryItem>();
        string? start = null;
        long position = 0;
        do
        {
            var url = $"https://steamcommunity.com/inventory/{steamId}/{AppId}/{ContextId}?l=english&count=2000";
            if (start is not null)
                url += $"&start_assetid={Uri.EscapeDataString(start)}";

            using var response = await Http.Community.GetAsync(url, cancellation).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new InventoryRateLimitedException();
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                throw new InventoryException("This backpack is private. Set Inventory to Public in your Steam privacy settings to see it here.");
            if (!response.IsSuccessStatusCode)
                throw new InventoryException($"Steam answered {(int)response.StatusCode} {response.ReasonPhrase}.");

            var json = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
            start = ParsePage(json, items, ref position);
        }
        while (start is not null);

        return items;
    }

    /// <summary>Adds one page of a community inventory response; returns the next page cursor.</summary>
    internal static string? ParsePage(string json, List<InventoryItem> items, ref long position)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InventoryException("This backpack is private or empty.");

        var descriptions = new Dictionary<(string, string), JsonElement>();
        if (root.TryGetProperty("descriptions", out var descs))
        {
            foreach (var d in descs.EnumerateArray())
                descriptions[(d.GetProperty("classid").GetString()!, d.GetProperty("instanceid").GetString()!)] = d;
        }
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var key = (asset.GetProperty("classid").GetString()!, asset.GetProperty("instanceid").GetString()!);
                if (descriptions.TryGetValue(key, out var description))
                    items.Add(InventoryItem.From(asset, description, position++));
            }
        }

        var more = root.TryGetProperty("more_items", out var flag)
            && (flag.ValueKind == JsonValueKind.True || flag.ValueKind == JsonValueKind.Number && flag.TryGetInt32(out var n) && n != 0);
        return more && root.TryGetProperty("last_assetid", out var last) ? last.GetString() : null;
    }

    internal InventoryCacheFile? ReadCache(string steamId)
    {
        var file = JsonFile.Load(CachePath(steamId), InventoryJson.Default.InventoryCacheFile, () => new InventoryCacheFile(), out var outcome);
        if (outcome.State is not (Persistence.LoadState.Loaded or Persistence.LoadState.RestoredFromBackup))
            return null;
        return file.SteamId == steamId && file.AppId == AppId && file.ContextId == ContextId ? file : null;
    }

    private void WriteCache(InventoryCacheFile file)
    {
        try
        {
            Directory.CreateDirectory(cacheDir);
            AtomicFile.WriteAllBytes(CachePath(file.SteamId), JsonSerializer.SerializeToUtf8Bytes(file, InventoryJson.Default.InventoryCacheFile), keepBackup: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Couldn't cache the backpack: {ex.Message}");
        }
    }
}

public sealed class InventoryCacheFile
{
    public string SteamId { get; set; } = "";
    public int AppId { get; set; }
    public int ContextId { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
    public List<InventoryItem> Items { get; set; } = [];
}

[JsonSourceGenerationOptions(RespectNullableAnnotations = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(InventoryCacheFile))]
[JsonSerializable(typeof(PriceCacheFile))]
[JsonSerializable(typeof(Dictionary<string, int>))]
internal sealed partial class InventoryJson : JsonSerializerContext;
