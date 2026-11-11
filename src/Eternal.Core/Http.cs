using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace Eternal.Core;

public static class Http
{
    public static HttpClient Client { get; } = Create(DecompressionMethods.All);

    public static HttpClient Community { get; } = Create(DecompressionMethods.None);

    public static HttpClient NoRedirects { get; } = Create(DecompressionMethods.All, redirects: false);

    /// <summary>
    /// For files: no overall time limit, since a large mod on a slow line can take minutes.
    /// Callers stop a download that stops moving, and check what arrives byte for byte.
    /// </summary>
    public static HttpClient Downloads { get; } = Create(DecompressionMethods.None, redirects: false, timeout: Timeout.InfiniteTimeSpan);

    /// <summary>
    /// Connects to the first of <paramref name="addresses"/> that answers, starting the next one
    /// every 1.5 s. On its own .NET spends the whole connect timeout on the first address, so one
    /// dead server among the many GameBanana's file host lists failed every download.
    /// </summary>
    internal static async Task<Socket> ConnectAsync(string host, IReadOnlyList<IPAddress> addresses, int port, CancellationToken cancellation)
    {
        if (addresses.Count == 0)
            throw new HttpRequestException($"{host} has no address.");
        using var others = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var pending = new List<Task<Socket>>();
        Exception? failure = null;
        var next = 0;
        while (next < addresses.Count || pending.Count > 0)
        {
            if (next < addresses.Count)
                pending.Add(ConnectOneAsync(addresses[next++], port, others.Token));
            var done = next < addresses.Count
                ? await Task.WhenAny([.. pending, Task.Delay(NextAddressAfter, cancellation)]).ConfigureAwait(false)
                : await Task.WhenAny(pending).ConfigureAwait(false);
            if (done is not Task<Socket> attempt)
                continue;
            pending.Remove(attempt);
            if (attempt.IsCompletedSuccessfully)
            {
                await others.CancelAsync().ConfigureAwait(false);
                foreach (var late in pending)
                    _ = late.ContinueWith(t => t.Result.Dispose(), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
                return attempt.Result;
            }
            failure = attempt.Exception?.InnerException ?? failure;
        }
        cancellation.ThrowIfCancellationRequested();
        throw new HttpRequestException($"None of the {addresses.Count} addresses of {host} accepted a connection.", failure);
    }

    private static async Task<Socket> ConnectOneAsync(IPAddress address, int port, CancellationToken cancellation)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(address, port, cancellation).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

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

    private static readonly TimeSpan NextAddressAfter = TimeSpan.FromMilliseconds(1500);

    private static HttpClient Create(DecompressionMethods decompression, bool redirects = true, TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = decompression,
            AllowAutoRedirect = redirects,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = async (context, cancellation) =>
            {
                var host = context.DnsEndPoint.Host;
                var addresses = IPAddress.TryParse(host, out var literal)
                    ? [literal]
                    : await Dns.GetHostAddressesAsync(host, cancellation).ConfigureAwait(false);
                return new NetworkStream(await ConnectAsync(host, addresses, context.DnsEndPoint.Port, cancellation).ConfigureAwait(false), ownsSocket: true);
            },
        };
        var client = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 64 * 1024 * 1024 };
        var version = typeof(Http).Assembly.GetName().Version?.ToString(3) ?? "2.0.0";
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("EternalLauncher", version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}
