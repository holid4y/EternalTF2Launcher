using System.Net;
using System.Net.Http.Headers;

namespace Eternal.Core;

public static class Http
{
    public static HttpClient Client { get; } = Create(DecompressionMethods.All);

    /// <summary>
    /// For steamcommunity.com, which answers 429 to anonymous inventory and
    /// market requests that accept compressed responses.
    /// </summary>
    public static HttpClient Community { get; } = Create(DecompressionMethods.None);

    /// <summary>Hands redirects back to the caller, to check where each one leads before following it.</summary>
    public static HttpClient NoRedirects { get; } = Create(DecompressionMethods.All, redirects: false);

    private static HttpClient Create(DecompressionMethods decompression, bool redirects = true)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = decompression,
            AllowAutoRedirect = redirects,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 64 * 1024 * 1024 };
        var version = typeof(Http).Assembly.GetName().Version?.ToString(3) ?? "2.0.0";
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("EternalLauncher", version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}
