using System.Net;
using System.Net.Http.Headers;

namespace Eternal.Core;

public static class Http
{
    public static HttpClient Client { get; } = Create(DecompressionMethods.All);

    public static HttpClient Community { get; } = Create(DecompressionMethods.None);

    public static HttpClient NoRedirects { get; } = Create(DecompressionMethods.All, redirects: false);

    public static async Task<byte[]> ReadCappedAsync(HttpContent content, int maxBytes, CancellationToken cancellation)
    {
        if (content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException("The response is larger than expected.");
        await using var input = await content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int n;
        while ((n = await input.ReadAsync(chunk, cancellation).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + n > maxBytes)
                throw new InvalidDataException("The response is larger than expected.");
            buffer.Write(chunk, 0, n);
        }
        return buffer.ToArray();
    }

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
