using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Eternal.Core.Logging;
using Eternal.Core.Persistence;

namespace Eternal.Core.Inventory;

public sealed record Price(string Sku, double SellKeys, double SellMetal, double BuyKeys, double BuyMetal, DateTimeOffset UpdatedAt)
{
    public double ValueInRef(double keyRef) => SellKeys * keyRef + SellMetal;
}

public sealed record MarketPrice(string Formatted, double Value, DateTimeOffset FetchedAt);

public sealed class PriceEntry
{
    public string Sku { get; set; } = "";
    public double SellKeys { get; set; }
    public double SellMetal { get; set; }
    public double BuyKeys { get; set; }
    public double BuyMetal { get; set; }
    public long Updated { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
    public bool Missing { get; set; }
}

public sealed class PriceCacheFile
{
    public DateTimeOffset IndexFetchedAt { get; set; }
    public Dictionary<string, string> NameIndex { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, PriceEntry> Prices { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, MarketPrice> Market { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Community prices from pricedb.io (backpack.tf data, no API key) plus the
/// Steam Community Market on demand. Items are matched by name to a SKU
/// because Steam's inventory no longer exposes item definition indexes.
/// </summary>
public sealed class PriceService
{
    public const string KeySku = "5021;6";
    public const string Source = "backpack.tf via pricedb.io";
    private static readonly TimeSpan IndexTtl = TimeSpan.FromDays(1);
    private static readonly TimeSpan PriceTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan MarketGap = TimeSpan.FromSeconds(3.5);

    public static readonly IReadOnlyDictionary<string, int> SteamCurrencies = new Dictionary<string, int>
    {
        ["USD"] = 1, ["GBP"] = 2, ["EUR"] = 3, ["CHF"] = 4, ["RUB"] = 5, ["PLN"] = 6, ["BRL"] = 7, ["JPY"] = 8,
        ["NOK"] = 9, ["UAH"] = 18, ["MXN"] = 19, ["CAD"] = 20, ["AUD"] = 21, ["NZD"] = 22, ["CNY"] = 23,
        ["INR"] = 24, ["KRW"] = 16, ["TRY"] = 17,
    };

    private readonly string _cachePath;
    private readonly SemaphoreSlim _fetch = new(4, 4);
    private readonly SemaphoreSlim _market = new(1, 1);
    private readonly SemaphoreSlim _index = new(1, 1);
    private readonly ConcurrentDictionary<string, PriceEntry> _prices;
    private readonly ConcurrentDictionary<string, MarketPrice> _marketPrices;
    private Dictionary<string, string> _nameIndex;
    private DateTimeOffset _indexFetchedAt;
    private DateTimeOffset _lastMarketCall = DateTimeOffset.MinValue;
    private int _dirty;

    public PriceService(string cacheDir)
    {
        _cachePath = Path.Combine(cacheDir, "prices.json");
        var cache = JsonFile.Load(_cachePath, InventoryJson.Default.PriceCacheFile, () => new PriceCacheFile(), out _);
        _nameIndex = new Dictionary<string, string>(cache.NameIndex, StringComparer.OrdinalIgnoreCase);
        _indexFetchedAt = cache.IndexFetchedAt;
        _prices = new ConcurrentDictionary<string, PriceEntry>(cache.Prices, StringComparer.Ordinal);
        _marketPrices = new ConcurrentDictionary<string, MarketPrice>(cache.Market, StringComparer.Ordinal);
    }

    public double KeyRef => _prices.TryGetValue(KeySku, out var key) && !key.Missing ? key.SellMetal : 0;

    public string? SkuFor(InventoryItem item) =>
        item.PriceNames().Select(n => _nameIndex.GetValueOrDefault(n)).FirstOrDefault(s => s is not null);

    public Price? Cached(InventoryItem item)
    {
        var sku = SkuFor(item);
        return sku is not null && _prices.TryGetValue(sku, out var entry) && !entry.Missing ? ToPrice(entry) : null;
    }

    public MarketPrice? CachedMarket(InventoryItem item, string currency) =>
        _marketPrices.GetValueOrDefault($"{currency}:{item.MarketHashName}");

    public async Task EnsureIndexAsync(CancellationToken cancellation = default)
    {
        if (_nameIndex.Count > 0 && DateTimeOffset.UtcNow - _indexFetchedAt < IndexTtl)
            return;
        await _index.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (_nameIndex.Count > 0 && DateTimeOffset.UtcNow - _indexFetchedAt < IndexTtl)
                return;
            await using var stream = await Http.Client.GetStreamAsync("https://pricedb.io/api/items", cancellation).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellation).ConfigureAwait(false);
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in doc.RootElement.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.Object
                        && entry.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                        && entry.TryGetProperty("sku", out var sku) && sku.ValueKind == JsonValueKind.String)
                        index.TryAdd(name.GetString()!, sku.GetString()!);
                }
            }
            if (index.Count > 0)
            {
                _nameIndex = index;
                _indexFetchedAt = DateTimeOffset.UtcNow;
                Interlocked.Exchange(ref _dirty, 1);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellation.IsCancellationRequested)
        {
            Log.Warn($"Price index unavailable: {ex.Message}");
        }
        finally
        {
            _index.Release();
        }
    }

    /// <summary>Prices every item that can be matched, skipping fresh cache entries.</summary>
    public async Task<int> PriceAllAsync(IEnumerable<InventoryItem> items, IProgress<int>? progress, CancellationToken cancellation = default)
    {
        await EnsureIndexAsync(cancellation).ConfigureAwait(false);
        var skus = items.Where(i => i.Tradable).Select(SkuFor).OfType<string>().Append(KeySku).Distinct().ToList();
        var todo = skus.Where(s => !_prices.TryGetValue(s, out var e) || DateTimeOffset.UtcNow - e.FetchedAt > PriceTtl).ToList();
        var done = 0;

        await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellation }, async (sku, ct) =>
        {
            await FetchPriceAsync(sku, ct).ConfigureAwait(false);
            progress?.Report(Interlocked.Increment(ref done));
        }).ConfigureAwait(false);

        Save();
        return skus.Count(s => _prices.TryGetValue(s, out var e) && !e.Missing);
    }

    private async Task FetchPriceAsync(string sku, CancellationToken cancellation)
    {
        await _fetch.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            using var response = await Http.Client.GetAsync($"https://pricedb.io/api/item/{Uri.EscapeDataString(sku)}", cancellation).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _prices[sku] = new PriceEntry { Sku = sku, Missing = true, FetchedAt = DateTimeOffset.UtcNow };
                return;
            }
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellation).ConfigureAwait(false);
                return;
            }
            if (!response.IsSuccessStatusCode)
                return;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
            var root = doc.RootElement;
            double Part(string side, string field) =>
                root.TryGetProperty(side, out var s) && s.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

            _prices[sku] = new PriceEntry
            {
                Sku = sku,
                SellKeys = Part("sell", "keys"),
                SellMetal = Part("sell", "metal"),
                BuyKeys = Part("buy", "keys"),
                BuyMetal = Part("buy", "metal"),
                Updated = root.TryGetProperty("time", out var time) && time.TryGetInt64(out var t) ? t : 0,
                FetchedAt = DateTimeOffset.UtcNow,
            };
            Interlocked.Exchange(ref _dirty, 1);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellation.IsCancellationRequested)
        {
            Log.Debug($"Price lookup failed for {sku}: {ex.Message}");
        }
        finally
        {
            _fetch.Release();
        }
    }

    /// <summary>Lowest Community Market listing. Throttled to Steam's ~20 requests per minute.</summary>
    public async Task<MarketPrice?> MarketPriceAsync(InventoryItem item, string currency, CancellationToken cancellation = default)
    {
        if (!item.Marketable || string.IsNullOrEmpty(item.MarketHashName))
            return null;
        var key = $"{currency}:{item.MarketHashName}";
        if (_marketPrices.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.FetchedAt < PriceTtl)
            return cached;

        await _market.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var wait = _lastMarketCall + MarketGap - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, cancellation).ConfigureAwait(false);
            _lastMarketCall = DateTimeOffset.UtcNow;

            var code = SteamCurrencies.GetValueOrDefault(currency, 1);
            var url = $"https://steamcommunity.com/market/priceoverview/?appid=440&currency={code}&market_hash_name={Uri.EscapeDataString(item.MarketHashName)}";
            using var response = await Http.Community.GetAsync(url, cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return cached;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
            var root = doc.RootElement;
            var text = root.TryGetProperty("lowest_price", out var lp) ? lp.GetString()
                     : root.TryGetProperty("median_price", out var mp) ? mp.GetString() : null;
            if (text is null)
                return null;
            var price = new MarketPrice(text, ParseMoney(text), DateTimeOffset.UtcNow);
            _marketPrices[key] = price;
            Interlocked.Exchange(ref _dirty, 1);
            return price;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellation.IsCancellationRequested)
        {
            Log.Debug($"Market price failed for {item.MarketHashName}: {ex.Message}");
            return cached;
        }
        finally
        {
            _market.Release();
        }
    }

    internal static double ParseMoney(string text)
    {
        var digits = new string(text.Where(c => char.IsDigit(c) || c is '.' or ',').ToArray()).Trim('.', ',');
        var lastSep = digits.LastIndexOfAny(['.', ',']);
        if (lastSep >= 0 && digits.Length - lastSep - 1 is 1 or 2)
            digits = digits[..lastSep].Replace(".", "").Replace(",", "") + "." + digits[(lastSep + 1)..];
        else
            digits = digits.Replace(".", "").Replace(",", "");
        return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    /// <summary>"2 keys 14.33 ref" — the way TF2 traders write prices.</summary>
    public static string Format(double refValue, double keyRef)
    {
        if (refValue <= 0)
            return "—";
        if (keyRef > 0 && refValue >= keyRef)
        {
            var keys = Math.Floor(refValue / keyRef);
            var rest = Math.Round(refValue - keys * keyRef, 2);
            return rest >= 0.11
                ? $"{keys:0} key{(keys == 1 ? "" : "s")} {rest.ToString("0.##", CultureInfo.InvariantCulture)} ref"
                : $"{keys:0} key{(keys == 1 ? "" : "s")}";
        }
        return $"{refValue.ToString("0.##", CultureInfo.InvariantCulture)} ref";
    }

    private static Price ToPrice(PriceEntry e) =>
        new(e.Sku, e.SellKeys, e.SellMetal, e.BuyKeys, e.BuyMetal, e.Updated > 0 ? DateTimeOffset.FromUnixTimeSeconds(e.Updated) : e.FetchedAt);

    public void Save()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0)
            return;
        try
        {
            var file = new PriceCacheFile
            {
                IndexFetchedAt = _indexFetchedAt,
                NameIndex = _nameIndex,
                Prices = new Dictionary<string, PriceEntry>(_prices, StringComparer.Ordinal),
                Market = new Dictionary<string, MarketPrice>(_marketPrices, StringComparer.Ordinal),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            AtomicFile.WriteAllBytes(_cachePath, JsonSerializer.SerializeToUtf8Bytes(file, InventoryJson.Default.PriceCacheFile), keepBackup: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Couldn't save the price cache: {ex.Message}");
        }
    }
}
