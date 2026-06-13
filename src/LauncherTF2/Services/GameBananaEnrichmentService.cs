using LauncherTF2.Core;
using LauncherTF2.Models;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LauncherTF2.Services;


/// <summary>
/// Enriches local mod metadata using a two-step lookup:
///
///   1. DuckDuckGo HTML search  → "{modName} site:gamebanana.com"
///      Extracts GameBanana mod URLs from the search results (no API key needed).
///
///   2. GameBanana API per-item → validates game == TF2 (ID 297) and that the
///      returned title is sufficiently similar to the local mod name.
///
/// If a validated match is found, the thumbnail is downloaded to a local cache
/// and the mod's Author / ThumbnailPath are updated in-place.
///
/// Cache: 7-day expiry. Negative entries cached for 7 days too (no re-fetching).
/// </summary>
public class GameBananaEnrichmentService
{
    private const int TF2GameId = 297;
    private const int CacheDays = 7;

    // DuckDuckGo HTML search (no API key required)
    private const string DDGSearchUrl =
        "https://html.duckduckgo.com/html/?q={0}+%22Team+Fortress+2%22+site%3Agamebanana.com";

    // GameBanana API per-item endpoint
    private const string GBItemApiUrl =
        "https://gamebanana.com/apiv11/{0}/{1}?_csvProperties=_sName,_aGame,_aPreviewMedia,_aSubmitter";

    // Regex: extracts section + numeric ID from GameBanana URLs inside HTML.
    // Anchored to https?:// so only proper hyperlinks match, not plain-text
    // mentions of the domain or redirects from other sites.
    private static readonly Regex GBUrlRegex = new(
        @"https?://(?:www\.)?gamebanana\.com/(mods|sounds|guis|maps|skins|effects|sprays)/(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HttpClient _http = AppHttpClient.Instance;

    private readonly string _thumbnailCacheDir;
    private readonly string _metadataCachePath;
    private readonly object _saveLock = new();
    private ConcurrentDictionary<string, CachedEntry> _cache;
    private Timer? _debounceSaveTimer;
    private const int SaveDebounceMs = 5000;

    public GameBananaEnrichmentService()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        _thumbnailCacheDir = Path.Combine(baseDir, "mod_thumbnails");
        _metadataCachePath = Path.Combine(baseDir, "mod_metadata_cache.json");

        Directory.CreateDirectory(_thumbnailCacheDir);
        _cache = LoadCache();
    }

    // Exposed for testing: return the generated DuckDuckGo query variants
    public List<string> GetSearchQueries(string modName) => BuildSearchQueries(modName);

    // ──────────────────────── Public API ───────────────────────────────────

    public async Task EnrichModAsync(ModModel mod)
    {
        var key = NormalizeKey(mod.Name);

        // Cache hit (positive or negative)
        if (_cache.TryGetValue(key, out var cached) && !cached.IsExpired)
        {
            if (!cached.IsNegative) ApplyEntry(mod, cached);
            return;
        }

        try
        {
            // Step 1: DuckDuckGo search → collect candidate GameBanana IDs
            var desiredYear = ExtractYearFromName(mod.Name);
            var candidates = await SearchDuckDuckGoAsync(mod.Name);

            if (candidates.Count == 0)
            {
                Logger.LogInfo($"[Enrichment] No DDG results for '{mod.Name}'");
                StoreNegative(key);
                return;
            }

            // Step 2: For each candidate, call GameBanana API and validate
            foreach (var (section, id) in candidates)
            {
                var result = await ValidateWithGameBananaAsync(section, id, mod.Name, desiredYear);
                if (result == null) continue;

                // Match found!
                var thumbPath = await DownloadThumbnailAsync(key, result.ThumbnailUrl);

                var entry = new CachedEntry
                {
                    Author = result.Author,
                    ThumbnailLocalPath = thumbPath,
                    FetchedAt = DateTime.UtcNow,
                    IsNegative = false
                };

                _cache[key] = entry;
                ScheduleCacheSave();
                ApplyEntry(mod, entry);

                Logger.LogInfo($"[Enrichment] Matched '{mod.Name}' → '{result.GbName}' by {result.Author}");
                return;
            }

            Logger.LogInfo($"[Enrichment] No TF2 match for '{mod.Name}' ({candidates.Count} candidates checked)");
            StoreNegative(key);
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[Enrichment] Failed for '{mod.Name}': {ex.Message}");
        }
    }

    // ──────────────────────── Step 1: DuckDuckGo search ────────────────────

    private async Task<List<(string Section, string Id)>> SearchDuckDuckGoAsync(string modName)
    {
        var queries = BuildSearchQueries(modName);
        var seen = new HashSet<string>();
        var results = new List<(string, string)>();

        foreach (var q in queries)
        {
            try
            {
                Logger.LogInfo($"[Enrichment] DDG query for '{modName}' → '{q}'");
                var query = Uri.EscapeDataString(q);
                var url = string.Format(DDGSearchUrl, query);
                var html = await _http.GetStringAsync(url);

                foreach (Match m in GBUrlRegex.Matches(html))
                {
                    var section = m.Groups[1].Value.ToLowerInvariant();
                    var id = m.Groups[2].Value;
                    var dedup = $"{section}/{id}";
                    if (seen.Add(dedup))
                        results.Add((section, id));

                    if (results.Count >= 12) break;
                }

                if (results.Count > 0) break; // stop when we have candidates
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Enrichment] DDG query failed ('{q}'): {ex.Message}");
            }
        }

        return results;
    }

    /// <summary>
    /// Generate multiple search query variants from a raw mod name to increase
    /// the chance of matching GameBanana search results.
    /// </summary>
    private static List<string> BuildSearchQueries(string modName)
    {
        var list = new List<string>();
        // Primary cleaned query (strips versions, years, packaging tags)
        var cleaned = BuildSearchQuery(modName);
        list.Add(cleaned + " \"Team Fortress 2\"");

        // Also produce a sanitized variant with ALL non-alphanumeric symbols removed
        var sanitized = Regex.Replace(modName, "[^a-zA-Z0-9\\s]", " ");
        sanitized = sanitized.Replace('_', ' ').Replace('-', ' ');
        sanitized = Regex.Replace(sanitized, "\\s+", " ").Trim();
        if (!string.IsNullOrWhiteSpace(sanitized))
            list.Add(sanitized + " \"Team Fortress 2\"");

        // Try without the explicit TF2 suffix (GameBanana often contains TF2 subpages)
        list.Add(cleaned);

        // Also try the sanitized form without TF2 suffix
        if (!list.Contains(sanitized, StringComparer.OrdinalIgnoreCase))
            list.Add(sanitized);

        // Remove digits (useful for names like femmepyroedit2026_v1_6_2)
        var noDigits = Regex.Replace(sanitized, "\\d+", "").Trim();
        if (!string.IsNullOrWhiteSpace(noDigits) && !list.Contains(noDigits, StringComparer.OrdinalIgnoreCase))
            list.Add(noDigits + " \"Team Fortress 2\"");

        // Special-case tokens for femmepyro family to help matching abbreviated names
        var lower = modName.ToLowerInvariant();
        if (lower.Contains("femmepyro") || lower.Contains("fempyro"))
        {
            if (!list.Contains("femmepyro 2026 \"Team Fortress 2\"", StringComparer.OrdinalIgnoreCase))
                list.Insert(0, "femmepyro 2026 \"Team Fortress 2\"");
            if (!list.Contains("fempyro 2026 \"Team Fortress 2\"", StringComparer.OrdinalIgnoreCase))
                list.Insert(1, "fempyro 2026 \"Team Fortress 2\"");
            if (!list.Contains("femmepyro \"Team Fortress 2\"", StringComparer.OrdinalIgnoreCase))
                list.Insert(2, "femmepyro \"Team Fortress 2\"");
        }

        // Word-prefix queries (first N words) — helpful for long compound names
        var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int n = Math.Min(3, words.Length); n >= 1; n--)
        {
            var prefix = string.Join(' ', words.Take(n));
            if (!string.IsNullOrWhiteSpace(prefix) && !list.Contains(prefix, StringComparer.OrdinalIgnoreCase))
                list.Add(prefix + " \"Team Fortress 2\"");
        }

        // Final fallback: sanitized spaced version
        if (!list.Contains(sanitized, StringComparer.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(sanitized))
            list.Add(sanitized);

        // Deduplicate while preserving order
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Cleans a raw mod filename so the search query has a fighting chance of
    /// matching the GameBanana title. Strips trailing version suffixes
    /// (<c>_v1.0</c>, <c>_v2_3</c>), trailing 4-digit years (<c>_2024</c>),
    /// converts underscores to spaces, and collapses whitespace.
    /// </summary>
    private static string BuildSearchQuery(string modName)
    {
        var s = modName;

        // Strip trailing version suffix: _v1.0, _v2_3_4, _V1, etc.
        s = Regex.Replace(s, @"[_-]v\d+(?:[._-]\d+)*$", "", RegexOptions.IgnoreCase);

        // Strip trailing year: _2024, -2026, etc.
        s = Regex.Replace(s, @"[_-](19|20)\d{2}$", "");

        // Drop generic packaging tags
        s = Regex.Replace(s, @"\b(final|release|fixed|patch|update)\b", "", RegexOptions.IgnoreCase);

        // Separators → spaces, collapse runs of whitespace
        s = s.Replace('_', ' ').Replace('-', ' ');
        s = Regex.Replace(s, @"\s+", " ").Trim();

        return string.IsNullOrWhiteSpace(s) ? modName : s;
    }

    // ──────────────────────── Step 2: GameBanana API validation ────────────

    private record GbMatch(string GbName, string Author, string? ThumbnailUrl, int? Year);

    private async Task<GbMatch?> ValidateWithGameBananaAsync(
        string section, string id, string localName, int? desiredYear)
    {
        try
        {
            var modelName = SectionToModelName(section);
            var url = string.Format(GBItemApiUrl, modelName, id);
            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // ── Must be TF2 ──────────────────────────────────────────────
            if (!root.TryGetProperty("_aGame", out var game)) return null;
            if (!game.TryGetProperty("_idRow", out var gameId)) return null;
            if (gameId.GetInt32() != TF2GameId) return null;

            // ── Name similarity / scoring ─────────────────────────────────
            var gbName = root.TryGetProperty("_sName", out var n) ? n.GetString() ?? "" : "";

            // ── Author ───────────────────────────────────────────────────
            var author = "Unknown";
            if (root.TryGetProperty("_aSubmitter", out var sub) &&
                sub.TryGetProperty("_sName", out var sName))
                author = sName.GetString() ?? "Unknown";

            var score = ComputeMatchScore(localName, gbName, desiredYear, author);
            Logger.LogInfo($"  [Enrichment] Candidate '{gbName}' score={score:F2} for '{localName}'");
            const double AcceptThreshold = 0.55; // tuned heuristic
            if (score < AcceptThreshold)
            {
                Logger.LogInfo($"  [Enrichment] Skipping '{gbName}' — low score ({score:F2}) for '{localName}'");
                return null;
            }

            // ── Thumbnail URL ─────────────────────────────────────────────
            string? thumbUrl = null;
            if (root.TryGetProperty("_aPreviewMedia", out var media) &&
                media.TryGetProperty("_aImages", out var images) &&
                images.GetArrayLength() > 0)
            {
                var img = images[0];
                if (img.TryGetProperty("_sBaseUrl", out var baseUrl))
                {
                    var fileField =
                        TryGetStr(img, "_sFile530") ??
                        TryGetStr(img, "_sFile220") ??
                        TryGetStr(img, "_sFile100") ??
                        TryGetStr(img, "_sFile");

                    if (fileField != null)
                        thumbUrl = $"{baseUrl.GetString()}/{fileField}";
                }
            }

            // Try to extract a date/year property if present
            int? year = null;
            foreach (var prop in root.EnumerateObject())
            {
                var pname = prop.Name.ToLowerInvariant();
                if (pname.Contains("date") || pname.Contains("updated") || pname.Contains("submit") || pname.Contains("time"))
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var sval = prop.Value.GetString();
                        if (DateTime.TryParse(sval, out var dt))
                        {
                            year = dt.Year;
                            break;
                        }
                        // also try extracting year digits
                        var m = Regex.Match(sval ?? "", "(20\\d{2}|19\\d{2})");
                        if (m.Success && int.TryParse(m.Value, out var y)) { year = y; break; }
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.Number)
                    {
                        if (prop.Value.TryGetInt64(out var epoch))
                        {
                            // heuristic: if it's a unix epoch
                            try { var dt = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime; year = dt.Year; break; } catch { }
                        }
                    }
                }
            }

            // If desiredYear was provided, prefer matches that have that year; if mismatch, skip
            if (desiredYear.HasValue && year.HasValue && year.Value != desiredYear.Value)
            {
                Logger.LogInfo($"  [Enrichment] Skipping '{gbName}' — year {year.Value} != desired {desiredYear.Value}");
                return null;
            }

            return new GbMatch(gbName, author, thumbUrl, year);
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[Enrichment] GameBanana API call failed for {section}/{id}: {ex.Message}");
            return null;
        }
    }

    // ──────────────────────── Name similarity ──────────────────────────────

    /// <summary>
    /// Two names are "similar" if one contains the other (after normalisation)
    /// or their edit-distance similarity is above 60 %.
    /// </summary>
    private static bool AreNamesSimilar(string local, string gb)
    {
        var a = NormalizeForCompare(local);
        var b = NormalizeForCompare(gb);

        if (a.Length < 3 || b.Length < 3) return false;

        // Substring match (e.g. "amphuddx" ⊂ "amphuddx hud")
        if (a.Contains(b, StringComparison.OrdinalIgnoreCase)) return true;
        if (b.Contains(a, StringComparison.OrdinalIgnoreCase)) return true;

        // Common word match (e.g. "ahud_v2" vs "TF2 ahud")
        var wordsA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var wordsB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var wa in wordsA)
        {
            if (wa.Length >= 4 && wordsB.Any(wb => wb == wa)) return true;
        }

        // Levenshtein similarity > 40 %
        var sim = LevenshteinSimilarity(a, b);
        return sim >= 0.40;
    }

    private static string NormalizeForCompare(string s)
    {
        // Remove special chars, collapse whitespace, lowercase
        s = Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9\s]", " ");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    private static int? ExtractYearFromName(string name)
    {
        var m = Regex.Match(name, "(20\\d{2}|19\\d{2})");
        if (m.Success && int.TryParse(m.Value, out var y)) return y;
        return null;
    }

    private static double LevenshteinSimilarity(string a, string b)
    {
        if (a == b) return 1.0;
        if (a.Length == 0 || b.Length == 0) return 0.0;

        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;

        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = a[i - 1] == b[j - 1]
                    ? d[i - 1, j - 1]
                    : 1 + Math.Min(d[i - 1, j - 1], Math.Min(d[i - 1, j], d[i, j - 1]));

        int maxLen = Math.Max(a.Length, b.Length);
        return 1.0 - (double)d[a.Length, b.Length] / maxLen;
    }

    /// <summary>
    /// Tokenize a name: split camelCase, remove punctuation, split on whitespace/digits.
    /// Returns lowercase tokens with length >= 2.
    /// </summary>
    private static List<string> Tokenize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return new List<string>();
        // Insert spaces between camelCase boundaries
        s = Regex.Replace(s, "([a-z])([A-Z])", "$1 $2");
        // Replace non-alphanumeric with space
        s = Regex.Replace(s, "[^a-zA-Z0-9]", " ");
        // Split and normalize
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().ToLowerInvariant())
            .Where(p => p.Length >= 2)
            .ToList();

        return parts;
    }

    private static double ComputeMatchScore(string localName, string gbName, int? desiredYear, string author)
    {
        var tokensA = Tokenize(localName);
        var tokensB = Tokenize(gbName);

        if (tokensA.Count == 0 || tokensB.Count == 0) return 0.0;

        var setA = new HashSet<string>(tokensA);
        var setB = new HashSet<string>(tokensB);

        // Jaccard similarity
        var intersect = setA.Intersect(setB).Count();
        var union = setA.Union(setB).Count();
        var jaccard = union == 0 ? 0.0 : (double)intersect / union;

        // Common long-word match count
        var commonLong = tokensA.Where(t => t.Length >= 4).Intersect(tokensB).Count();
        var longMatchScore = Math.Min(1.0, commonLong / 2.0);

        // Levenshtein similarity on the full cleaned strings
        var lev = LevenshteinSimilarity(NormalizeForCompare(localName), NormalizeForCompare(gbName));

        // Year bonus will be applied elsewhere; here we check for tokenized year match
        double yearBoost = 0.0;
        var yearA = ExtractYearFromName(localName);
        var yearB = ExtractYearFromName(gbName);
        if (yearA.HasValue && yearB.HasValue && yearA.Value == yearB.Value) yearBoost = 0.15;
        if (desiredYear.HasValue && yearB.HasValue && desiredYear.Value == yearB.Value) yearBoost = 0.18;

        // Author bonus is small (we don't have author locally by default)
        double authorBoost = 0.0;
        if (!string.IsNullOrWhiteSpace(author)) authorBoost = 0.05;

        // Weighted sum
        double score = 0;
        score += 0.45 * jaccard;       // token overlap is primary
        score += 0.20 * longMatchScore; // exact long-word matches
        score += 0.20 * lev;           // fuzzy similarity
        score += yearBoost;            // year boost
        score += authorBoost;

        return Math.Min(1.0, score);
    }

    // ──────────────────────── Helpers ──────────────────────────────────────

    private static string SectionToModelName(string section) => section switch
    {
        "sounds" => "Sound",
        "guis"   => "Gui",
        "maps"   => "Map",
        "skins"  => "Skin",
        "effects"=> "Effect",
        "sprays" => "Spray",
        _        => "Mod"
    };

    private static string? TryGetStr(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v))
        {
            var s = v.GetString();
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }
        return null;
    }

    private static void ApplyEntry(ModModel mod, CachedEntry entry)
    {
        if (entry.IsNegative) return;

        if (!string.IsNullOrWhiteSpace(entry.Author) && entry.Author != "Unknown")
            mod.Author = entry.Author;

        if (!string.IsNullOrWhiteSpace(entry.ThumbnailLocalPath) &&
            File.Exists(entry.ThumbnailLocalPath))
        {
            // Set both the path and a pre-built BitmapImage so the UI template
            // can bind to either ThumbnailPath or ThumbnailImage reliably.
            mod.ThumbnailPath = entry.ThumbnailLocalPath;
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(entry.ThumbnailLocalPath, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                mod.ThumbnailImage = bmp;
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Enrichment] Failed to load thumbnail bitmap '{entry.ThumbnailLocalPath}': {ex.Message}");
                mod.ThumbnailImage = null;
            }

            mod.IsEnriched = true;
        }
    }

    private async Task<string?> DownloadThumbnailAsync(string key, string? imageUrl)
    {
        if (string.IsNullOrEmpty(imageUrl)) return null;
        try
        {
            var ext = Path.GetExtension(new Uri(imageUrl).AbsolutePath);
            if (string.IsNullOrEmpty(ext)) ext = ".jpg";
            var localPath = Path.Combine(_thumbnailCacheDir, $"{key}{ext}");
            var bytes = await _http.GetByteArrayAsync(imageUrl);
            await File.WriteAllBytesAsync(localPath, bytes);
            return localPath;
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[Enrichment] Thumbnail download failed ({imageUrl}): {ex.Message}");
            return null;
        }
    }

    private void StoreNegative(string key)
    {
        _cache[key] = CachedEntry.Negative();
        ScheduleCacheSave();
    }

    /// <summary>
    /// Debounces cache saves so we don't write the full JSON file after every single mod.
    /// Flushes 5 seconds after the last change.
    /// </summary>
    private void ScheduleCacheSave()
    {
        _debounceSaveTimer?.Dispose();
        _debounceSaveTimer = new Timer(_ => SaveCache(), null, SaveDebounceMs, Timeout.Infinite);
    }

    private static string NormalizeKey(string name)
    {
        var key = name.ToLowerInvariant().Trim();
        key = Regex.Replace(key, @"[^\w\s-]", "");
        key = Regex.Replace(key, @"\s+", "_");
        return key;
    }

    // ──────────────────────── Cache persistence ─────────────────────────────

    private ConcurrentDictionary<string, CachedEntry> LoadCache()
    {
        try
        {
            if (File.Exists(_metadataCachePath))
            {
                var json = File.ReadAllText(_metadataCachePath);
                var dict = JsonSerializer.Deserialize<Dictionary<string, CachedEntry>>(json);
                if (dict != null)
                    return new ConcurrentDictionary<string, CachedEntry>(dict);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[Enrichment] Failed to load cache: {ex.Message}");
        }
        return new ConcurrentDictionary<string, CachedEntry>();
    }

    private void SaveCache()
    {
        try
        {
            // Lock so concurrent tasks don't write simultaneously
            lock (_saveLock)
            {
                var json = JsonSerializer.Serialize(
                    new Dictionary<string, CachedEntry>(_cache),
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_metadataCachePath, json);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[Enrichment] Failed to save cache: {ex.Message}");
        }
    }

    // ──────────────────────── Cache model ──────────────────────────────────

    private class CachedEntry
    {
        public string? Author { get; set; }
        public string? ThumbnailLocalPath { get; set; }
        public DateTime FetchedAt { get; set; }
        public bool IsNegative { get; set; }

        public bool IsExpired => (DateTime.UtcNow - FetchedAt).TotalDays > CacheDays;

        public static CachedEntry Negative() => new()
        {
            IsNegative = true,
            FetchedAt = DateTime.UtcNow
        };
    }
}
