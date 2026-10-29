using Eternal.Core.Steam;

namespace Eternal.Core.Tests;

public class SteamTests
{
    [Fact]
    public void Vdf_parses_nested_sections_escapes_and_comments()
    {
        var root = Vdf.Parse("""
            "libraryfolders"
            {
                // first library
                "0"
                {
                    "path"  "D:\\Games\\Steam"
                    "apps" { "440" "123" }
                }
            }
            """);

        var library = root.Path("libraryfolders", "0");
        Assert.Equal(@"D:\Games\Steam", library?.Value("path"));
        Assert.Equal("123", library?.Section("apps")?.Value("440"));
    }

    [Fact]
    public void Newer_clients_pick_the_auto_login_account_with_the_latest_timestamp()
    {
        var users = SteamLocator.ParseLoginUsers("""
            "users"
            {
                "76561198000000001" { "PersonaName" "Old" "AutoLogin" "0" "Timestamp" "1790000000" }
                "76561198000000002" { "PersonaName" "Main" "AutoLogin" "1" "Timestamp" "1790165590" }
                "76561198000000003" { "PersonaName" "Alt" "AutoLogin" "0" "Timestamp" "1790999999" }
            }
            """);

        var picked = users.OrderByDescending(u => u.MostRecent).ThenByDescending(u => u.Timestamp).First().Account;

        Assert.Equal("76561198000000002", picked.SteamId64);
        Assert.Equal("Main", picked.PersonaName);
    }

    [Fact]
    public void Entries_that_are_not_steam_ids_are_ignored()
    {
        var users = SteamLocator.ParseLoginUsers("\"users\" { \"../evil\" { \"PersonaName\" \"x\" } }");

        Assert.Empty(users);
    }
}
