using LauncherTF2.Core;
using Microsoft.Data.Sqlite;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LauncherTF2.Services;

public class SteamInventoryService
{
    private const string InventoryBaseUrl = "https://steamcommunity.com/inventory";
    private const string SteamCommunityHost = "steamcommunity.com";

    private static readonly HttpClient _http = AppHttpClient.Instance;

    private readonly string _inventoryCachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tf2_inventory_cache_v2.json");

    public async Task<SteamInventoryFetchResult> GetBackpackItemsAsync(string steamId64, CancellationToken cancellationToken = default, bool forceRefresh = false)
    {
        if (string.IsNullOrWhiteSpace(steamId64))
            throw new InvalidOperationException("SteamID64 is required.");

        var freshCache = forceRefresh ? null : await Task.Run(() =>
        {
            return TryLoadCache(out var cache, TimeSpan.FromMinutes(10)) ? cache : null;
        }, cancellationToken).ConfigureAwait(false);

        if (freshCache != null)
        {
            return freshCache with
            {
                FromCache = true,
                Message = $"Loaded {freshCache.Items.Count} items from local cache."
            };
        }

        var cookies = await Task.Run(LoadSteamCommunityCookies, cancellationToken).ConfigureAwait(false);
        var hasCookies = !string.IsNullOrWhiteSpace(cookies.SessionId) && !string.IsNullOrWhiteSpace(cookies.SteamLoginSecure);

        try
        {
            var items = await FetchInventoryFromCommunityAsync(steamId64.Trim(), hasCookies ? cookies : null, cancellationToken).ConfigureAwait(false);

            var cookieStatus = hasCookies
                ? "Steam cookies loaded."
                : "Steam cookies not found. Using public Steam Community access.";

            var result = new SteamInventoryFetchResult
            {
                Items = items,
                CookieStatus = cookieStatus,
                Message = $"Backpack loaded: {items.Count} items."
            };

            SaveCache(result);
            return result;
        }
        catch (InvalidOperationException ex) when (!hasCookies)
        {
            throw new InvalidOperationException("Steam must be running and logged in.", ex);
        }
        catch (SteamRateLimitException)
        {
            if (TryLoadCache(out var staleCache, null))
            {
                return staleCache with
                {
                    FromCache = true,
                    RateLimitedFallback = true,
                    CookieStatus = "Steam cookies loaded.",
                    Message = "Rate limited - try again in a few minutes. Showing cached inventory."
                };
            }

            throw new InvalidOperationException("Rate limited - try again in a few minutes.");
        }
    }

    private async Task<List<SteamInventoryItem>> FetchInventoryFromCommunityAsync(
        string steamId64,
        SteamCookiePair? cookies,
        CancellationToken cancellationToken)
    {
        var allAssets = new List<InventoryAsset>();
        var descriptionMap = new Dictionary<string, InventoryDescription>(StringComparer.OrdinalIgnoreCase);

        string? startAssetId = null;

        do
        {
            var page = await FetchInventoryPageAsync(steamId64, cookies, startAssetId, cancellationToken).ConfigureAwait(false);
            allAssets.AddRange(page.Assets);

            foreach (var description in page.Descriptions)
            {
                descriptionMap[BuildDescriptorKey(description.ClassId, description.InstanceId)] = description;
            }

            startAssetId = page.MoreItems && !string.IsNullOrWhiteSpace(page.LastAssetId)
                ? page.LastAssetId
                : null;
        }
        while (!string.IsNullOrWhiteSpace(startAssetId));

        var items = new List<SteamInventoryItem>(allAssets.Count);

        foreach (var asset in allAssets)
        {
            var key = BuildDescriptorKey(asset.ClassId, asset.InstanceId);
            descriptionMap.TryGetValue(key, out var description);

            var qualityTag = description?.Tags.FirstOrDefault(t =>
                string.Equals(t.Category, "Quality", StringComparison.OrdinalIgnoreCase));

            var isEquipped = description?.Tags.Any(t =>
                (!string.IsNullOrWhiteSpace(t.Category) && t.Category.Contains("equip", StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(t.InternalName) && t.InternalName.Contains("equipped", StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(t.LocalizedTagName) && t.LocalizedTagName.Contains("equipped", StringComparison.OrdinalIgnoreCase))) == true;

            items.Add(new SteamInventoryItem
            {
                AssetId = asset.AssetId,
                ClassId = asset.ClassId,
                InstanceId = asset.InstanceId,
                Name = string.IsNullOrWhiteSpace(description?.Name) ? $"Unknown item ({asset.ClassId})" : description.Name,
                ImageUrl = BuildImageUrl(description?.IconUrl),
                BorderColorHex = NormalizeHexColor(qualityTag?.Color) ?? "#444444",
                QualityName = qualityTag?.LocalizedTagName ?? "Unknown",
                Type = description?.Type ?? "Unknown",
                Tradable = description?.Tradable == 1,
                Amount = asset.Amount,
                IsEquipped = isEquipped,
                Rarity = description?.Tags.FirstOrDefault(t =>
                    string.Equals(t.Category, "Rarity", StringComparison.OrdinalIgnoreCase))?.LocalizedTagName,
                UnusualEffect = ExtractHint(description?.Type, description?.Tags, "unusual"),
                Paint = ExtractHint(description?.Type, description?.Tags, "paint"),
                KillstreakTier = ExtractKillstreakTier(description?.Name, description?.Type),
                KillstreakSheen = ExtractHint(description?.Type, description?.Tags, "sheen"),
                Killstreaker = ExtractHint(description?.Type, description?.Tags, "killstreaker"),
                Spell = ExtractHint(description?.Type, description?.Tags, "spell"),
                CraftNumber = ExtractCraftNumber(description?.Name),
                EquippedOn = ExtractEquippedOn(description?.Tags),
                Description = string.IsNullOrWhiteSpace(description?.Description) ? null : description.Description
            });
        }

        return items;
    }

    private async Task<InventoryPageResponse> FetchInventoryPageAsync(
        string steamId64,
        SteamCookiePair? cookies,
        string? startAssetId,
        CancellationToken cancellationToken)
    {
        var url = new StringBuilder($"{InventoryBaseUrl}/{steamId64}/440/2?l=english&count=2000");
        if (!string.IsNullOrWhiteSpace(startAssetId))
            url.Append($"&start_assetid={Uri.EscapeDataString(startAssetId)}");

        using var request = new HttpRequestMessage(HttpMethod.Get, url.ToString());
        if (!string.IsNullOrWhiteSpace(cookies?.SteamLoginSecure) && !string.IsNullOrWhiteSpace(cookies?.SessionId))
        {
            request.Headers.TryAddWithoutValidation("Cookie", $"steamLoginSecure={cookies.SteamLoginSecure}; sessionid={cookies.SessionId}");
        }

        request.Headers.Referrer = new Uri("https://steamcommunity.com/");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new SteamRateLimitException();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var sanitized = string.IsNullOrWhiteSpace(json) ? string.Empty : json;
            if (sanitized.Contains("private", StringComparison.OrdinalIgnoreCase) ||
                response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                throw new InvalidOperationException("Steam must be running and logged in.");
            }

            throw new InvalidOperationException($"Steam Community request failed: {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var assets = new List<InventoryAsset>();
        var descriptions = new List<InventoryDescription>();

        if (root.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsElement.EnumerateArray())
            {
                assets.Add(new InventoryAsset
                {
                    AssetId = GetString(asset, "assetid"),
                    ClassId = GetString(asset, "classid"),
                    InstanceId = GetString(asset, "instanceid"),
                    Amount = GetInt(asset, "amount")
                });
            }
        }

        if (root.TryGetProperty("descriptions", out var descriptionsElement) && descriptionsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var description in descriptionsElement.EnumerateArray())
            {
                descriptions.Add(new InventoryDescription
                {
                    ClassId = GetString(description, "classid"),
                    InstanceId = GetString(description, "instanceid"),
                    Name = GetString(description, "name"),
                    IconUrl = GetString(description, "icon_url"),
                    Type = GetString(description, "type"),
                    Tradable = GetInt(description, "tradable"),
                    Tags = ParseTags(description),
                    Description = ParseDescriptionLines(description)
                });
            }
        }

        return new InventoryPageResponse
        {
            Assets = assets,
            Descriptions = descriptions,
            MoreItems = GetBoolLike(root, "more_items"),
            LastAssetId = GetString(root, "last_assetid")
        };
    }

    private SteamCookiePair LoadSteamCommunityCookies()
    {
        var masterKey = TryGetChromiumMasterKey();

        foreach (var cookiePath in GetCookieDbCandidates())
        {
            if (!File.Exists(cookiePath))
                continue;

            var tempDb = CopyCookieDbToTemp(cookiePath);
            if (tempDb == null)
                continue;

            try
            {
                var cookies = QueryCookies(tempDb, masterKey);
                if (!string.IsNullOrWhiteSpace(cookies.SessionId) && !string.IsNullOrWhiteSpace(cookies.SteamLoginSecure))
                    return cookies;
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Inventory] Failed to read Steam cookies from '{cookiePath}'", ex);
            }
            finally
            {
                TryDeleteTempFile(tempDb);
            }
        }

        return new SteamCookiePair();
    }

    private static IEnumerable<string> GetCookieDbCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // Legacy and Chromium profile-based cookie DB locations used by Steam.
        var fixedCandidates = new[]
        {
            Path.Combine(localAppData, "Steam", "htmlcache", "Cookies"),
            Path.Combine(localAppData, "Steam", "config", "htmlcache", "Cookies"),
            Path.Combine(localAppData, "Steam", "htmlcache", "Default", "Cookies"),
            Path.Combine(localAppData, "Steam", "htmlcache", "Default", "Network", "Cookies"),
            Path.Combine(localAppData, "Steam", "config", "htmlcache", "Default", "Cookies"),
            Path.Combine(localAppData, "Steam", "config", "htmlcache", "Default", "Network", "Cookies")
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in fixedCandidates)
        {
            if (seen.Add(candidate))
                yield return candidate;
        }

        // Fallback scan for unforeseen profile names/folders.
        var scanRoots = new[]
        {
            Path.Combine(localAppData, "Steam", "htmlcache"),
            Path.Combine(localAppData, "Steam", "config", "htmlcache")
        };

        foreach (var root in scanRoots)
        {
            if (!Directory.Exists(root))
                continue;

            IEnumerable<string> found;
            try
            {
                found = Directory.EnumerateFiles(root, "Cookies", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var path in found)
            {
                if (seen.Add(path))
                    yield return path;
            }
        }
    }

    private static string? CopyCookieDbToTemp(string sourcePath)
    {
        try
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"tf2launcher_cookies_{Guid.NewGuid():N}.db");

            using var sourceStream = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            using var targetStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            sourceStream.CopyTo(targetStream);

            return tempPath;
        }
        catch
        {
            return null;
        }
    }

    private static SteamCookiePair QueryCookies(string sqlitePath, byte[]? masterKey)
    {
        var result = new SteamCookiePair();

        using var connection = new SqliteConnection($"Data Source={sqlitePath};Mode=ReadOnly;Cache=Private");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT name, value, encrypted_value
FROM cookies
    WHERE host_key LIKE '%steamcommunity.com'
AND name IN ('steamLoginSecure', 'sessionid');";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            var plainValue = !reader.IsDBNull(1) ? reader.GetString(1) : string.Empty;
            var encryptedBytes = !reader.IsDBNull(2) ? (byte[])reader[2] : Array.Empty<byte>();

            var value = !string.IsNullOrWhiteSpace(plainValue)
                ? plainValue
                : DecryptCookieValue(encryptedBytes, masterKey);

            if (string.Equals(name, "steamLoginSecure", StringComparison.OrdinalIgnoreCase))
                result.SteamLoginSecure = value;
            else if (string.Equals(name, "sessionid", StringComparison.OrdinalIgnoreCase))
                result.SessionId = value;
        }

        return result;
    }

    private byte[]? TryGetChromiumMasterKey()
    {
        foreach (var localStatePath in GetLocalStateCandidates())
        {
            if (!File.Exists(localStatePath))
                continue;

            try
            {
                var json = File.ReadAllText(localStatePath);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("os_crypt", out var osCrypt))
                    continue;

                if (!osCrypt.TryGetProperty("encrypted_key", out var keyElement))
                    continue;

                var encryptedKeyB64 = keyElement.GetString();
                if (string.IsNullOrWhiteSpace(encryptedKeyB64))
                    continue;

                var encryptedKey = Convert.FromBase64String(encryptedKeyB64);
                if (encryptedKey.Length <= 5)
                    continue;

                // Chromium prefixes the key with ASCII 'DPAPI'.
                var dpapiBlob = encryptedKey[5..];
                return ProtectedData.Unprotect(dpapiBlob, null, DataProtectionScope.CurrentUser);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Inventory] Failed to decode Chromium master key from '{localStatePath}'", ex);
            }
        }

        return null;
    }

    private static IEnumerable<string> GetLocalStateCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(localAppData, "Steam", "htmlcache", "Local State");
        yield return Path.Combine(localAppData, "Steam", "config", "htmlcache", "Local State");
    }

    private static string DecryptCookieValue(byte[] encryptedBytes, byte[]? masterKey)
    {
        if (encryptedBytes.Length == 0)
            return string.Empty;

        try
        {
            if (encryptedBytes.Length > 3 &&
                encryptedBytes[0] == (byte)'v' &&
                (encryptedBytes[1] == (byte)'1') &&
                (encryptedBytes[2] == (byte)'0' || encryptedBytes[2] == (byte)'1') &&
                masterKey is { Length: > 0 })
            {
                // Chromium encrypted cookie format:
                // [3-byte version][12-byte nonce][ciphertext][16-byte authTag]
                const int nonceLen = 12;
                const int tagLen = 16;
                var payload = encryptedBytes[3..];
                if (payload.Length > nonceLen + tagLen)
                {
                    var nonce = payload[..nonceLen];
                    var cipherAndTag = payload[nonceLen..];
                    var cipherText = cipherAndTag[..^tagLen];
                    var tag = cipherAndTag[^tagLen..];
                    var plain = new byte[cipherText.Length];

                    using var aes = new AesGcm(masterKey, tagLen);
                    aes.Decrypt(nonce, cipherText, tag, plain);
                    return Encoding.UTF8.GetString(plain).Trim('\0');
                }
            }

            var decrypted = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted).Trim('\0');
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void TryDeleteTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Ignore cleanup issues.
        }
    }

    private bool TryLoadCache(out SteamInventoryFetchResult result, TimeSpan? maxAge)
    {
        result = new SteamInventoryFetchResult();

        if (!File.Exists(_inventoryCachePath))
            return false;

        try
        {
            if (maxAge != null)
            {
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(_inventoryCachePath);
                if (age > maxAge.Value)
                    return false;
            }

            var json = File.ReadAllText(_inventoryCachePath);
            var cached = JsonSerializer.Deserialize<SteamInventoryFetchResult>(json);
            if (cached == null)
                return false;

            result = cached;
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning("[Inventory] Failed to load inventory cache", ex);
            return false;
        }
    }

    private void SaveCache(SteamInventoryFetchResult result)
    {
        try
        {
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = false
            });

            File.WriteAllText(_inventoryCachePath, json);
        }
        catch (Exception ex)
        {
            Logger.LogWarning("[Inventory] Failed to save inventory cache", ex);
        }
    }

    private static string BuildDescriptorKey(string classId, string instanceId) => $"{classId}:{instanceId}";

    private static string? BuildImageUrl(string? iconUrl)
    {
        if (string.IsNullOrWhiteSpace(iconUrl))
            return null;

        return $"https://community.cloudflare.steamstatic.com/economy/image/{iconUrl}";
    }

    private static string? NormalizeHexColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
            return null;

        var normalized = color.Trim();
        return normalized.StartsWith('#') ? normalized : $"#{normalized}";
    }

    private static string ParseDescriptionLines(JsonElement description)
    {
        if (!description.TryGetProperty("descriptions", out var lines) || lines.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var pieces = new List<string>();
        foreach (var line in lines.EnumerateArray())
        {
            var value = GetString(line, "value");
            if (string.IsNullOrWhiteSpace(value)) continue;
            pieces.Add(value.Trim());
        }
        return string.Join("\n", pieces);
    }

    private static List<InventoryTag> ParseTags(JsonElement description)
    {
        var tags = new List<InventoryTag>();

        if (!description.TryGetProperty("tags", out var tagsElement) || tagsElement.ValueKind != JsonValueKind.Array)
            return tags;

        foreach (var tag in tagsElement.EnumerateArray())
        {
            tags.Add(new InventoryTag
            {
                Category = GetString(tag, "category"),
                InternalName = GetString(tag, "internal_name"),
                LocalizedTagName = GetString(tag, "localized_tag_name"),
                Color = GetString(tag, "color")
            });
        }

        return tags;
    }

    private static bool GetBoolLike(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var element))
            return false;

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => element.TryGetInt32(out var i) && i != 0,
            JsonValueKind.String => bool.TryParse(element.GetString(), out var b) && b,
            _ => false
        };
    }

    private static int GetInt(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var element))
            return 0;

        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt32(out var i) => i,
            JsonValueKind.String when int.TryParse(element.GetString(), out var parsed) => parsed,
            _ => 0
        };
    }

    private static string GetString(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var element))
            return string.Empty;

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.ToString(),
            _ => string.Empty
        };
    }

    private static string? ExtractHint(string? type, List<InventoryTag>? tags, string marker)
    {
        if (!string.IsNullOrWhiteSpace(type) && type.Contains(marker, StringComparison.OrdinalIgnoreCase))
            return type;

        var tag = tags?.FirstOrDefault(t =>
            (!string.IsNullOrWhiteSpace(t.LocalizedTagName) && t.LocalizedTagName.Contains(marker, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(t.InternalName) && t.InternalName.Contains(marker, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(t.Category) && t.Category.Contains(marker, StringComparison.OrdinalIgnoreCase)));

        return tag?.LocalizedTagName;
    }

    private static string? ExtractKillstreakTier(string? name, string? type)
    {
        if (!string.IsNullOrWhiteSpace(name) && name.Contains("Professional Killstreak", StringComparison.OrdinalIgnoreCase))
            return "Professional Killstreak";
        if (!string.IsNullOrWhiteSpace(name) && name.Contains("Specialized Killstreak", StringComparison.OrdinalIgnoreCase))
            return "Specialized Killstreak";
        if ((!string.IsNullOrWhiteSpace(name) && name.Contains("Killstreak", StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(type) && type.Contains("Killstreak", StringComparison.OrdinalIgnoreCase)))
            return "Killstreak";

        return null;
    }

    private static string? ExtractCraftNumber(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var marker = "#";
        var idx = name.LastIndexOf(marker, StringComparison.Ordinal);
        if (idx < 0 || idx == name.Length - 1)
            return null;

        var numberPart = new string(name[(idx + 1)..].TakeWhile(char.IsDigit).ToArray());
        return string.IsNullOrWhiteSpace(numberPart) ? null : numberPart;
    }

    private static string? ExtractEquippedOn(List<InventoryTag>? tags)
    {
        var equippedTags = tags?
            .Where(t =>
                (!string.IsNullOrWhiteSpace(t.Category) && t.Category.Contains("Class", StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(t.LocalizedTagName) && t.LocalizedTagName.Contains("Equipped", StringComparison.OrdinalIgnoreCase)))
            .Select(t => t.LocalizedTagName)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return equippedTags is { Count: > 0 } ? string.Join(", ", equippedTags) : null;
    }

    public sealed record SteamInventoryFetchResult
    {
        public List<SteamInventoryItem> Items { get; set; } = [];
        public bool FromCache { get; set; }
        public bool RateLimitedFallback { get; set; }
        public string CookieStatus { get; set; } = "Steam cookies unavailable.";
        public string Message { get; set; } = string.Empty;
    }

    public sealed class SteamInventoryItem
    {
        public string AssetId { get; set; } = string.Empty;
        public string ClassId { get; set; } = string.Empty;
        public string InstanceId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? ImageUrl { get; set; }
        public string BorderColorHex { get; set; } = "#444444";
        public string QualityName { get; set; } = "Unknown";
        public string Type { get; set; } = "Unknown";
        public bool Tradable { get; set; }
        public int Amount { get; set; }
        public bool IsEquipped { get; set; }
        public string? Rarity { get; set; }
        public string? UnusualEffect { get; set; }
        public string? Paint { get; set; }
        public string? KillstreakTier { get; set; }
        public string? KillstreakSheen { get; set; }
        public string? Killstreaker { get; set; }
        public string? Spell { get; set; }
        public string? CraftNumber { get; set; }
        public string? EquippedOn { get; set; }
        public string? Description { get; set; }
    }

    private sealed class InventoryPageResponse
    {
        public List<InventoryAsset> Assets { get; init; } = [];
        public List<InventoryDescription> Descriptions { get; init; } = [];
        public bool MoreItems { get; init; }
        public string LastAssetId { get; init; } = string.Empty;
    }

    private sealed class InventoryAsset
    {
        public string AssetId { get; init; } = string.Empty;
        public string ClassId { get; init; } = string.Empty;
        public string InstanceId { get; init; } = string.Empty;
        public int Amount { get; init; }
    }

    private sealed class InventoryDescription
    {
        public string ClassId { get; init; } = string.Empty;
        public string InstanceId { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string IconUrl { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public int Tradable { get; init; }
        public List<InventoryTag> Tags { get; init; } = [];
        public string Description { get; init; } = string.Empty;
    }

    private sealed class InventoryTag
    {
        public string Category { get; init; } = string.Empty;
        public string InternalName { get; init; } = string.Empty;
        public string LocalizedTagName { get; init; } = string.Empty;
        public string Color { get; init; } = string.Empty;
    }

    private sealed class SteamCookiePair
    {
        public string SteamLoginSecure { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
    }

    private sealed class SteamRateLimitException : Exception;
}
