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
