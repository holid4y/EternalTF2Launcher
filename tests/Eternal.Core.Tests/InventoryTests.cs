using System.Text.Json;
using Eternal.Core.Inventory;

namespace Eternal.Core.Tests;

public sealed class InventoryTests : IDisposable
{
    private const string AccountA = "76561198000000001";
    private const string AccountB = "76561198000000002";
    private readonly string _dir = Directory.CreateTempSubdirectory("eternal-inventory-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void WriteCache(string fileFor, string claimedOwner) =>
        File.WriteAllText(Path.Combine(_dir, fileFor + ".json"), JsonSerializer.Serialize(
            new InventoryCacheFile { SteamId = claimedOwner, AppId = 440, ContextId = 2, FetchedAt = DateTimeOffset.UtcNow, Items = [new InventoryItem { Name = "Team Captain" }] },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    [Fact]
    public void A_cached_backpack_is_only_served_to_its_own_account()
    {
        WriteCache(AccountA, AccountA);
        WriteCache(AccountB, AccountA);
        var service = new InventoryService(_dir);

        Assert.NotNull(service.ReadCache(AccountA));
        Assert.Null(service.ReadCache(AccountB));
    }

    [Theory]
    [InlineData("76561198000000001", true)]
    [InlineData("7656119800000000", false)]
    [InlineData("../../etc/passwd", false)]
    [InlineData("12345678901234567", false)]
    public void Only_steam_id64s_are_accepted(string id, bool valid)
    {
        Assert.Equal(valid, InventoryService.IsSteamId64(id));
    }

    [Fact]
    public void Page_parser_joins_assets_with_their_descriptions()
    {
        const string page = """
            {"assets":[{"assetid":"1","classid":"10","instanceid":"0","amount":"1"}],
             "descriptions":[{"classid":"10","instanceid":"0","name":"Unusual Team Captain","market_hash_name":"Unusual Team Captain",
               "tradable":1,"marketable":1,"type":"Level 1 Hat","name_color":"8650AC",
               "tags":[{"category":"Quality","localized_tag_name":"Unusual","color":"8650AC"},{"category":"Class","localized_tag_name":"Soldier"}],
               "descriptions":[{"value":"★ Unusual Effect: Burning Flames","color":"ffd700"},{"value":"( Not Usable in Crafting )"}]}],
             "total_inventory_count":1}
            """;
        var items = new List<InventoryItem>();
        long position = 0;

        var next = InventoryService.ParsePage(page, items, ref position);

        var item = Assert.Single(items);
        Assert.Null(next);
        Assert.Equal("Burning Flames", item.Effect);
        Assert.False(item.Craftable);
        Assert.Equal(1, item.Level);
        Assert.Equal(["Soldier"], item.Classes);
    }

    [Fact]
    public void Price_names_follow_backpack_tf_naming()
    {
        var unusual = new InventoryItem { MarketHashName = "Unusual Team Captain", Effect = "Burning Flames" };
        var article = new InventoryItem { MarketHashName = "The Black Box" };
        var uncraftable = new InventoryItem { MarketHashName = "Strange Professional Killstreak Ham Shank", Craftable = false };

        Assert.Contains("Burning Flames Team Captain", unusual.PriceNames());
        Assert.Contains("Black Box", article.PriceNames());
        Assert.Equal("Strange Professional Killstreak Non-Craftable Ham Shank", uncraftable.PriceNames().First());
    }

    [Theory]
    [InlineData(0.88, 64.88, "0.88 ref")]
    [InlineData(64.88, 64.88, "1 key")]
    [InlineData(68.75, 64.88, "1 key 3.87 ref")]
    [InlineData(200, 64.88, "3 keys 5.36 ref")]
    [InlineData(0, 64.88, "—")]
    public void Prices_are_written_the_way_traders_write_them(double refValue, double keyRef, string expected)
    {
        Assert.Equal(expected, PriceService.Format(refValue, keyRef));
    }

    [Theory]
    [InlineData("$0.03", 0.03)]
    [InlineData("R$ 1,23", 1.23)]
    [InlineData("1.234,56₽", 1234.56)]
    [InlineData("12,345.67 руб.", 12345.67)]
    [InlineData("¥ 300", 300)]
    public void Market_prices_parse_in_any_locale(string text, double expected)
    {
        Assert.Equal(expected, PriceService.ParseMoney(text), 2);
    }

    [Theory]
    [InlineData("0", null)]
    [InlineData("false", null)]
    [InlineData("1", "555")]
    [InlineData("true", "555")]
    public void Only_a_true_more_items_flag_asks_for_another_page(string flag, string? next)
    {
        var page = $$"""{"assets": [], "descriptions": [], "more_items": {{flag}}, "last_assetid": "555"}""";
        long position = 0;

        Assert.Equal(next, InventoryService.ParsePage(page, [], ref position));
    }
}

public class ItemNameTests
{
    private static InventoryItem Parse(string description)
    {
        using var asset = System.Text.Json.JsonDocument.Parse("""{"assetid":"9001","classid":"1","instanceid":"2","amount":"1"}""");
        using var desc = System.Text.Json.JsonDocument.Parse(description);
        return InventoryItem.From(asset.RootElement, desc.RootElement, 0);
    }

    [Fact]
    public void Name_tags_keep_the_custom_name_and_the_original()
    {
        var item = Parse("""
            {"name":"''U MAD YET''","market_hash_name":"Strange Professional Killstreak Scorch Shot",
             "fraudwarnings":["This item has been renamed.\nOriginal name: \"Scorch Shot\""],
             "descriptions":[{"value":"100% mini-crits vs burning players"}]}
            """);
        Assert.Equal("U MAD YET", item.Name);
        Assert.Equal("U MAD YET", item.CustomName);
        Assert.Equal("Scorch Shot", item.OriginalName);
        Assert.Equal("Strange Professional Killstreak Scorch Shot", item.MarketHashName);
    }

    [Fact]
    public void Description_tags_come_out_of_the_description_lines()
    {
        var item = Parse("""
            {"name":"Bat","market_hash_name":"Bat","descriptions":[{"value":"''pyrocynical''"},{"value":"Sheen: Deadly Daffodil"}]}
            """);
        Assert.Null(item.CustomName);
        Assert.Equal("pyrocynical", item.CustomDescription);
        Assert.DoesNotContain(item.Lines, l => l.Text.Contains("pyrocynical"));
        Assert.Equal("Deadly Daffodil", item.Sheen);
    }

    [Fact]
    public void Decal_items_are_recognised_from_their_description()
    {
        var item = Parse("""
            {"name":"The Conscientious Objector","market_hash_name":"The Conscientious Objector",
             "descriptions":[{"value":"We gave peace a chance. It didn't work.\n\nCustom decals can be applied to this item."}]}
            """);
        Assert.True(item.TakesDecal);
    }

    [Fact]
    public void Decal_ids_join_the_two_halves_of_the_upload_id()
    {
        var json = """
            {"result":{"status":1,"items":[
              {"id":9001,"defindex":474,"attributes":[{"defindex":152,"value":1234},{"defindex":227,"value":5}]},
              {"id":9002,"defindex":474,"attributes":[{"defindex":152,"value":1}]},
              {"id":9003,"defindex":5020}
            ]}}
            """u8.ToArray();
        var decals = DecalLookup.ParseDecals(json);
        Assert.Equal((5UL << 32) | 1234, Assert.Single(decals).Value);
        Assert.Equal("9001", decals.Keys.Single());
    }

    [Fact]
    public void Decal_fields_sent_as_strings_are_still_read()
    {
        var json = """
            {"result":{"status":1,"items":[
              {"id":"9001","attributes":[{"defindex":"152","value":"1234"},{"defindex":"227","value":"5"}]},
              {"id":9002,"attributes":[{"defindex":"152","value":"1"}]},
              {"id":9003,"attributes":[{"defindex":152,"value":7},{"defindex":227,"value":"0"}]}
            ]}}
            """u8.ToArray();
        var decals = DecalLookup.ParseDecals(json);
        Assert.Equal((5UL << 32) | 1234, decals["9001"]);
        Assert.Equal(7UL, decals["9003"]);
        Assert.False(decals.ContainsKey("9002"));
    }

    [Theory]
    [InlineData("https://cloud-3.steamusercontent.com/ugc/123/ABC/", "https://cloud-3.steamusercontent.com/ugc/123/ABC/")]
    [InlineData("http://cloud-3.steamusercontent.com/ugc/123/ABC/", "https://cloud-3.steamusercontent.com/ugc/123/ABC/")]
    [InlineData("http://images.akamai.steamusercontent.com/ugc/1/A/", "https://images.akamai.steamusercontent.com/ugc/1/A/")]
    [InlineData("https://steamusercontent-a.akamaihd.net/ugc/1/A/", "https://steamusercontent-a.akamaihd.net/ugc/1/A/")]
    [InlineData("http://steamuserimages-a.akamaihd.net/ugc/1/A/", "https://steamuserimages-a.akamaihd.net/ugc/1/A/")]
    [InlineData("https://steamusercontent.com.evil.example/ugc/", null)]
    [InlineData("https://evil.akamaihd.net/ugc/", null)]
    [InlineData("ftp://cloud-3.steamusercontent.com/ugc/", null)]
    public void Decal_pictures_only_come_from_steam_and_always_over_https(string url, string? expected) =>
        Assert.Equal(expected, DecalLookup.ParseUrl(System.Text.Encoding.UTF8.GetBytes($$$"""{"data":{"url":"{{{url}}}"}}""")));

    [Fact]
    public void A_private_backpack_is_told_apart_from_having_no_decals()
    {
        var ex = Assert.Throws<DecalLookupException>(() =>
            DecalLookup.ParseDecals("""{"result":{"status":15,"statusDetail":"Private"}}"""u8.ToArray()));

        Assert.Contains("private", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
