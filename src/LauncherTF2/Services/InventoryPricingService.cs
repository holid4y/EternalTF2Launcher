using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using LauncherTF2.Core;

namespace LauncherTF2.Services;

/// <summary>
/// Queries prices.tf and Steam Market directly — no external backend required.
/// Includes per-host rate limiting and persistent disk cache to avoid API throttling.
/// </summary>
public class InventoryPricingService
{
    private static readonly HttpClient _http = AppHttpClient.Instance;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // Per-host rate limiters — Steam Market caps at ~20 req/min
    private static readonly SemaphoreSlim _pricesTfLimiter = new(3);
    private static readonly SemaphoreSlim _steamMarketLimiter = new(2);
    private static readonly TimeSpan SteamMarketDelay = TimeSpan.FromMilliseconds(3200);
    private static readonly TimeSpan PricesTfDelay = TimeSpan.FromMilliseconds(500);

    private static readonly string DiskCachePath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "price_cache.json");

    private static readonly TimeSpan DiskCacheTtl = TimeSpan.FromHours(2);

    // Shown in the fallback status string so users know the hardcoded prices aren't live data.
    private const string FallbackPricesAsOf = "2025-Q2";

    // UI display order
    public static readonly string[] StoreOrder =
    [
        "prices.tf",
        "Steam Market",
        "backpack.tf",
        "marketplace.tf",
        "mannco.store",
        "stntrading.eu",
        "tradeit.gg"
    ];

    // SKU mappings for prices.tf (items the API can actually resolve)
    private static readonly Dictionary<string, string> KnownSkus = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Scrap Metal"] = "5000;6",
        ["Reclaimed Metal"] = "5001;6",
        ["Refined Metal"] = "5002;6",
        ["Mann Co. Supply Crate Key"] = "5021;6"
    };

    // Hardcoded fallback prices when APIs are unreachable
    private static readonly Dictionary<string, decimal> LocalFallbackUsd = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Scrap Metal"] = 0.03m,
        ["Reclaimed Metal"] = 0.09m,
        ["Refined Metal"] = 0.27m,
        ["Mann Co. Supply Crate Key"] = 1.89m
    };

    private Dictionary<string, DiskCachedPrice>? _diskCache;

    public async Task<PriceSnapshot> GetPriceSnapshotAsync(string itemName, string quality, bool tradable)
    {
        var mapped = new Dictionary<string, PriceResult>(StringComparer.OrdinalIgnoreCase);

        // Try disk cache first
        if (TryGetDiskCachedPrice(itemName, out var cachedResult))
        {
            mapped = cachedResult;
        }
        else
        {
            // Query pricing sources directly
            var pricesTfTask = FetchPricesTfAsync(itemName);
            var steamTask = FetchSteamMarketAsync(itemName);

            await Task.WhenAll(pricesTfTask, steamTask);

            var pricesTf = await pricesTfTask;
            var steam = await steamTask;

            if (pricesTf != null)
                mapped[pricesTf.StoreName] = pricesTf;
            if (steam != null)
                mapped[steam.StoreName] = steam;

            WriteDiskCache(itemName, mapped);
        }

        // Fill remaining stores with "Unavailable" + search links
        foreach (var store in StoreOrder)
        {
            if (!mapped.ContainsKey(store))
                mapped[store] = Unavailable(store, itemName);
        }

        // Local fallback for common items when both APIs fail
        if (AreAllStoresUnavailable(mapped) && LocalFallbackUsd.TryGetValue(itemName.Trim(), out var fallbackUsd))
        {
            mapped["prices.tf"] = new PriceResult
            {
                StoreName = "prices.tf",
                Status = $"Approx (local fallback, as of {FallbackPricesAsOf})",
                PriceRef = $"${fallbackUsd:0.00}",
                ListingUrl = GetStoreSearchUrl("prices.tf", itemName)
            };
        }

        var best = mapped.Values.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.PriceRef));

        return new PriceSnapshot
        {
            StoreResults = mapped,
            PureSummary = best == null ? "unavailable" : best.PriceRef
        };
    }

    #region prices.tf

    private async Task<PriceResult?> FetchPricesTfAsync(string itemName)
    {
        var sku = KnownSkus.TryGetValue(itemName.Trim(), out var resolved) ? resolved : null;
        if (sku == null)
            return null;

        await _pricesTfLimiter.WaitAsync();
        try
        {
            await Task.Delay(PricesTfDelay);

            var url = $"https://api2.prices.tf/prices/{Uri.EscapeDataString(sku)}";
            using var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;

            var payload = await response.Content.ReadFromJsonAsync<PricesTfResponse>(JsonOptions);
            if (payload == null)
                return null;

            var usd = payload.SellPrice > 0 ? payload.SellPrice / 100m : (decimal?)null;

            return new PriceResult
            {
                StoreName = "prices.tf",
                Status = "Live",
                PriceRef = usd.HasValue ? $"${usd.Value:0.00}" : string.Empty,
                ListingUrl = $"https://prices.tf/items/{Uri.EscapeDataString(sku)}"
            };
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[InventoryPricing] prices.tf request failed for '{itemName}'", ex);
            return null;
        }
        finally
        {
            _pricesTfLimiter.Release();
        }
    }

    #endregion

    #region Steam Market

    private async Task<PriceResult?> FetchSteamMarketAsync(string itemName)
    {
        await _steamMarketLimiter.WaitAsync();
        try
        {
            await Task.Delay(SteamMarketDelay);

            var encoded = Uri.EscapeDataString(itemName);
            var url = $"https://steamcommunity.com/market/priceoverview/?appid=440&currency=1&market_hash_name={encoded}";

            using var response = await _http.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var payload = await response.Content.ReadFromJsonAsync<SteamPriceResponse>(JsonOptions);
                if (payload?.Success == true && !string.IsNullOrWhiteSpace(payload.LowestPrice) &&
                    TryParseUsd(payload.LowestPrice, out var usd))
                {
                    return new PriceResult
                    {
                        StoreName = "Steam Market",
                        Status = "Approx",
                        PriceRef = $"${usd:0.00}",
                        ListingUrl = $"https://steamcommunity.com/market/listings/440/{encoded}"
                    };
                }
            }

            // Fallback: broader search endpoint (lower rate-limit impact)
            return await TrySteamSearchFallbackAsync(itemName);
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[InventoryPricing] Steam Market request failed for '{itemName}'", ex);
            return null;
        }
        finally
        {
            _steamMarketLimiter.Release();
        }
    }

    private async Task<PriceResult?> TrySteamSearchFallbackAsync(string itemName)
    {
        try
        {
            var query = Uri.EscapeDataString(itemName);
            var url = $"https://steamcommunity.com/market/search/render/?appid=440&norender=1&count=5&query={query}";

            using var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;

            var payload = await response.Content.ReadFromJsonAsync<SteamSearchResponse>(JsonOptions);
            var first = payload?.Results?.FirstOrDefault();
            if (first == null)
                return null;

            var usd = first.SellPrice > 0
                ? first.SellPrice / 100m
                : 0m;

            if (usd <= 0m && !TryParseUsd(first.SellPriceText ?? string.Empty, out usd))
                return null;

            var hashName = string.IsNullOrWhiteSpace(first.HashName) ? itemName : first.HashName;
            return new PriceResult
            {
                StoreName = "Steam Market",
                Status = "Approx",
                PriceRef = $"${usd:0.00}",
                ListingUrl = $"https://steamcommunity.com/market/listings/440/{Uri.EscapeDataString(hashName)}"
            };
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region Disk Cache

    private bool TryGetDiskCachedPrice(string itemName, out Dictionary<string, PriceResult> results)
    {
        results = new Dictionary<string, PriceResult>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (_diskCache == null && File.Exists(DiskCachePath))
            {
                var json = File.ReadAllText(DiskCachePath);
                _diskCache = JsonSerializer.Deserialize<Dictionary<string, DiskCachedPrice>>(json, JsonOptions)
                             ?? new Dictionary<string, DiskCachedPrice>(StringComparer.OrdinalIgnoreCase);
            }

            _diskCache ??= new Dictionary<string, DiskCachedPrice>(StringComparer.OrdinalIgnoreCase);

            if (!_diskCache.TryGetValue(itemName.Trim(), out var cached))
                return false;

            if (DateTimeOffset.UtcNow - cached.CachedAt > DiskCacheTtl)
                return false;

            results = cached.Stores.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value,
                StringComparer.OrdinalIgnoreCase);

            return results.Count > 0;
        }
        catch (Exception ex)
        {
            Logger.LogWarning("[InventoryPricing] Failed to read disk cache", ex);
            return false;
        }
    }

    private void WriteDiskCache(string itemName, Dictionary<string, PriceResult> results)
    {
        try
        {
            _diskCache ??= new Dictionary<string, DiskCachedPrice>(StringComparer.OrdinalIgnoreCase);

            _diskCache[itemName.Trim()] = new DiskCachedPrice
            {
                CachedAt = DateTimeOffset.UtcNow,
                Stores = results
            };

            var json = JsonSerializer.Serialize(_diskCache, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(DiskCachePath, json);
        }
        catch (Exception ex)
        {
            Logger.LogWarning("[InventoryPricing] Failed to write disk cache", ex);
        }
    }

    #endregion

    #region Helpers

    private static bool TryParseUsd(string raw, out decimal value)
    {
        value = 0m;

        var cleaned = raw
            .Replace("$", string.Empty, StringComparison.Ordinal)
            .Replace("USD", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out value)
               || decimal.TryParse(cleaned.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    private static bool AreAllStoresUnavailable(Dictionary<string, PriceResult> map)
        => map.Values.All(v => string.IsNullOrWhiteSpace(v.PriceRef) && string.IsNullOrWhiteSpace(v.PriceKeys));

    private static PriceResult Unavailable(string storeName, string itemName) => new()
    {
        StoreName = storeName,
        Status = "Unavailable",
        ListingUrl = GetStoreSearchUrl(storeName, itemName)
    };

    public static string GetStoreSearchUrl(string storeName, string itemName)
    {
        var q = Uri.EscapeDataString(itemName);
        return storeName switch
        {
            "prices.tf" => $"https://prices.tf/items?q={q}",
            "Steam Market" => $"https://steamcommunity.com/market/search?appid=440&q={q}",
            "backpack.tf" => $"https://backpack.tf/classifieds?item={q}&quality=6&tradable=1&craftable=1",
            "marketplace.tf" => $"https://marketplace.tf/?query={q}",
            "mannco.store" => $"https://mannco.store/tf2?search={q}",
            "stntrading.eu" => $"https://stntrading.eu/tf2/items?search={q}",
            "tradeit.gg" => $"https://tradeit.gg/tf2/store?search={q}",
            _ => $"https://www.google.com/search?q={q}"
        };
    }

    #endregion

    #region DTOs & Models

    public sealed class PriceSnapshot
    {
        public Dictionary<string, PriceResult> StoreResults { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string PureSummary { get; set; } = "Pure: unavailable";
    }

    public sealed class PriceResult
    {
        public string StoreName { get; set; } = string.Empty;
        public string Status { get; set; } = "Unavailable";
        public string PriceKeys { get; set; } = string.Empty;
        public string PriceRef { get; set; } = string.Empty;
        public string ListingUrl { get; set; } = string.Empty;
        public string FallbackUrl { get; set; } = string.Empty;
    }

    private sealed class DiskCachedPrice
    {
        public DateTimeOffset CachedAt { get; set; }
        public Dictionary<string, PriceResult> Stores { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    // prices.tf API response
    private sealed record PricesTfResponse(int SellPrice, int BuyPrice);

    // Steam Market priceoverview response
    private sealed class SteamPriceResponse
    {
        public bool Success { get; set; }
        public string? LowestPrice { get; set; }
    }

    // Steam Market search/render response
    private sealed class SteamSearchResponse
    {
        public List<SteamSearchResult>? Results { get; set; }
    }

    private sealed class SteamSearchResult
    {
        public string? HashName { get; set; }
        public int SellPrice { get; set; }
        public string? SellPriceText { get; set; }
    }

    #endregion
}
