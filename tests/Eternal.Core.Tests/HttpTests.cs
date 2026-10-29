using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Eternal.Core.Tests;

public sealed class HttpTests
{
    private static async Task<string> RequestHeadersSentBy(HttpClient client)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            var stream = socket.GetStream();
            var buffer = new byte[8192];
            var request = new StringBuilder();
            while (!request.ToString().Contains("\r\n\r\n"))
                request.Append(Encoding.ASCII.GetString(buffer, 0, await stream.ReadAsync(buffer)));
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
            return request.ToString();
        });

        using var response = await client.GetAsync($"http://127.0.0.1:{port}/inventory");
        return await server;
    }

    [Fact]
    public async Task Steam_community_requests_do_not_ask_for_compression()
    {
        // steamcommunity.com answers 429 to anonymous inventory and market
        // requests that accept gzip, and 200 to the same request without it.
        var headers = await RequestHeadersSentBy(Http.Community);

        Assert.DoesNotContain("Accept-Encoding", headers, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Other_requests_still_accept_compressed_responses()
    {
        var headers = await RequestHeadersSentBy(Http.Client);

        Assert.Contains("Accept-Encoding", headers, StringComparison.OrdinalIgnoreCase);
    }
}
