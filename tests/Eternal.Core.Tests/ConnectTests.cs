using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Eternal.Core.Tests;

public sealed class ConnectTests
{
    [Fact]
    public async Task A_dead_address_does_not_stop_the_next_one_from_being_tried()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = listener.AcceptSocketAsync();
        var watch = Stopwatch.StartNew();

        using var socket = await Http.ConnectAsync("files.example", [IPAddress.Parse("10.255.255.1"), IPAddress.Loopback], port,
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

        Assert.True(socket.Connected);
        Assert.Equal(IPAddress.Loopback, ((IPEndPoint)socket.RemoteEndPoint!).Address);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        (await accepted).Dispose();
    }

    [Fact]
    public async Task When_no_address_answers_the_host_is_named()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Http.ConnectAsync("files.example", [IPAddress.Loopback], port, CancellationToken.None));

        Assert.Contains("files.example", ex.Message);
    }
}
