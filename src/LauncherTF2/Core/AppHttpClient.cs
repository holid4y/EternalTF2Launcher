using System.Net.Http;

namespace LauncherTF2.Core;

/// <summary>
/// Single shared HttpClient for all outbound requests.
/// Sharing one instance means one connection pool, so TCP connections to the
/// same host (GameBanana, Steam, prices.tf, etc.) are reused across services
/// instead of each service maintaining its own separate pool.
/// </summary>
internal static class AppHttpClient
{
    public static readonly HttpClient Instance = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0.0.0 Safari/537.36");
        return client;
    }
}
