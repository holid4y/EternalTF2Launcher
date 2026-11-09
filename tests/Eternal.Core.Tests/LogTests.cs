using Eternal.Core.Logging;

namespace Eternal.Core.Tests;

public class LogTests
{
    [Theory]
    [InlineData("GET https://api.example.com/x?key=ABC123&steamid=7", "GET https://api.example.com/x?key=***&steamid=7")]
    [InlineData("failed for https://teamwork.tf/api?token=zzz", "failed for https://teamwork.tf/api?token=***")]
    [InlineData("nothing secret here", "nothing secret here")]
    public void Keys_in_addresses_never_reach_the_log(string line, string expected) => Assert.Equal(expected, Log.Redact(line));
}
