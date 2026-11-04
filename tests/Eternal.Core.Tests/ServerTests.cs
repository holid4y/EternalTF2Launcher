using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Eternal.Core.Servers;

namespace Eternal.Core.Tests;

public class ServerTests
{
    [Theory]
    [InlineData("91.216.250.10:27015", "91.216.250.10:27015")]
    [InlineData(" 91.216.250.10 ", "91.216.250.10:27015")]
    [InlineData("192.168.1.20:27016", "192.168.1.20:27016")]
    public void Addresses_parse_to_ip_and_port(string text, string expected)
    {
        Assert.True(ServerAddress.TryParse(text, out var address));
        Assert.Equal(expected, address.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("example.com:27015")]
    [InlineData("1.2.3.4:0")]
    [InlineData("1.2.3.4:70000")]
    [InlineData("1.2.3.4:27015/pass")]
    [InlineData("0.0.0.0:27015")]
    [InlineData("255.255.255.255:27015")]
    [InlineData("239.1.1.1:27015")]
    [InlineData("127.1:27015")]
    [InlineData("0x7f.0.0.1:27015")]
    [InlineData("[::1]:27015")]
    [InlineData("1.2.3.4:+27015")]
    public void Anything_but_a_plain_ipv4_server_address_is_refused(string text) =>
        Assert.False(ServerAddress.TryParse(text, out _));

    [Fact]
    public void Info_reply_reads_every_field_including_the_extra_data()
    {
        var reply = Info("My ‮server\n", "cp_badlands", players: 18, max: 24, bots: 2, password: true, vac: true, keywords: "cp,increased_maxplayers");
        var info = SourceQuery.ParseInfo(reply)!;

        Assert.Equal("My server", info.Name);
        Assert.Equal("cp_badlands", info.Map);
        Assert.Equal((18, 24, 2), (info.Players, info.MaxPlayers, info.Bots));
        Assert.True(info.Password);
        Assert.True(info.Vac);
        Assert.Equal(27015, info.GamePort);
        Assert.Equal(["cp", "increased_maxplayers"], info.Tags);
    }

    [Fact]
    public void Truncated_or_foreign_replies_give_nothing()
    {
        var reply = Info("Server", "ctf_2fort", 1, 24, 0, false, true, "");
        Assert.Null(SourceQuery.ParseInfo(reply[..12]));
        Assert.Null(SourceQuery.ParseInfo([0x6D, 1, 2, 3]));
        Assert.Null(SourceQuery.ParsePlayers([0x44]));
    }

    [Fact]
    public void Player_and_rule_replies_are_read_up_to_where_they_stop()
    {
        var players = new List<byte> { 0x44, 2 };
        players.AddRange(Player("Heavy", 12, 300));
        players.AddRange(Player("Medic", 4, float.NaN));
        var list = SourceQuery.ParsePlayers([.. players])!;
        Assert.Equal(2, list.Count);
        Assert.Equal(TimeSpan.FromSeconds(300), list[0].Time);
        Assert.Equal(TimeSpan.Zero, list[1].Time);

        var rules = new List<byte> { 0x45, 3, 0 };
        rules.AddRange([.. "mp_timelimit"u8, 0, .. "30"u8, 0, .. "sv_cheats"u8, 0, .. "0"u8, 0, .. "cut"u8]);
        Assert.Equal(2, SourceQuery.ParseRules([.. rules])!.Count);
    }

    [Fact]
    public async Task Queries_answer_the_challenge_and_join_split_replies()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)server.Client.LocalEndPoint!;
        var info = Info("Fake", "pl_upward", 3, 24, 0, false, false, "payload");
        var serve = Task.Run(async () =>
        {
            var first = await server.ReceiveAsync();
            await server.SendAsync(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x41, 1, 2, 3, 4 }.AsMemory(), first.RemoteEndPoint);
            var second = await server.ReceiveAsync();
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, second.Buffer[^4..]);
            var payload = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }.Concat(info).ToArray();
            var half = payload.Length / 2;
            await server.SendAsync(Split(7, 2, 1, payload[half..]).AsMemory(), second.RemoteEndPoint);
            await server.SendAsync(Split(7, 2, 0, payload[..half]).AsMemory(), second.RemoteEndPoint);
        });

        var result = await SourceQuery.InfoAsync(endpoint, TimeSpan.FromSeconds(5));
        await serve;
        Assert.NotNull(result);
        Assert.Equal("pl_upward", result.Value.Info.Map);
    }

    [Fact]
    public async Task Compressed_split_replies_are_refused()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)server.Client.LocalEndPoint!;
        var serve = Task.Run(async () =>
        {
            var request = await server.ReceiveAsync();
            await server.SendAsync(Split(unchecked((int)0x80000001), 1, 0, new byte[32]).AsMemory(), request.RemoteEndPoint);
        });
        var result = await SourceQuery.InfoAsync(endpoint, TimeSpan.FromSeconds(3));
        await serve;
        Assert.Null(result);
    }

    [Fact]
    public void Game_monitoring_entries_are_checked_before_they_are_kept()
    {
        var json = """
            {"response":{"items":[
              {"ip":"170.168.115.57","port":27015,"query":27015,"status":true,"numplayers":11,"maxplayers":32,"name":"Good\u0007","map":"ctf_2fort","country":"ru"},
              {"ip":"10.0.0.1; rm -rf","port":27015,"status":true,"numplayers":5,"maxplayers":24,"name":"Bad ip","map":"x"},
              {"ip":"1.2.3.4","port":99999,"status":true,"numplayers":5,"maxplayers":24,"name":"Bad port","map":"x"},
              {"ip":"1.2.3.5","port":27015,"status":false,"numplayers":5,"maxplayers":24,"name":"Offline","map":"x"},
              {"ip":"1.2.3.6","port":27015,"query":0,"status":true,"numplayers":900,"maxplayers":24,"name":"Liar","map":"x","country":"Brazil"},
              {"ip":"127.0.0.1","port":27015,"status":true,"numplayers":5,"maxplayers":24,"name":"Loopback","map":"x"},
              {"ip":"192.168.1.1","port":27015,"status":true,"numplayers":5,"maxplayers":24,"name":"Router","map":"x"}
            ]}}
            """;
        var (servers, count) = GameMonitoringSource.Parse(Encoding.UTF8.GetBytes(json));

        Assert.Equal(7, count);
        Assert.Equal(2, servers.Count);
        Assert.Equal("Good", servers[0].Name);
        Assert.Equal("RU", servers[0].Country);
        Assert.Equal(255, servers[1].Players);
        Assert.Equal(27015, servers[1].QueryPort);
        Assert.Null(servers[1].Country);
    }

    [Fact]
    public void Master_server_replies_are_read_as_ip_and_port_pairs()
    {
        // FF FF FF FF 66 0A, then 6-byte entries: IPv4 big-endian + port big-endian.
        byte[] packet =
        [
            0xFF, 0xFF, 0xFF, 0xFF, 0x66, 0x0A,
            91, 216, 250, 10, 0x69, 0x87,   // 91.216.250.10:27015
            1, 2, 3, 4, 0x00, 0x00,          // 1.2.3.4:0 — end-of-list marker
        ];
        var entries = MasterServerSource.ParseMasterReply(packet);
        Assert.Equal(2, entries.Count);
        Assert.Equal(IPAddress.Parse("91.216.250.10"), entries[0].Ip);
        Assert.Equal(27015, entries[0].Port);
        Assert.Equal(IPAddress.Parse("1.2.3.4"), entries[1].Ip);
        Assert.Equal(0, entries[1].Port);
    }

    [Fact]
    public void Anything_but_a_master_reply_is_ignored()
    {
        Assert.Empty(MasterServerSource.ParseMasterReply([0xFF, 0xFF, 0xFF, 0xFF, 0x36, 0x00, 1, 2, 3, 4, 0, 1])); // legacy 0x36 header
        Assert.Empty(MasterServerSource.ParseMasterReply([0x00]));
        Assert.Empty(MasterServerSource.ParseMasterReply([]));
    }

    [Theory]
    [InlineData("workshop/cp_glassworks_rc7a.ugc454118349", 454118349UL)]
    [InlineData("workshop/454118349", 454118349UL)]
    [InlineData("cp_badlands", null)]
    [InlineData("workshop/../../etc.ugc1", null)]
    public void Workshop_maps_give_their_item_number(string map, ulong? id) =>
        Assert.Equal(id, WorkshopMaps.WorkshopId(map));

    [Fact]
    public void Workshop_pictures_only_come_from_steam_image_hosts()
    {
        static byte[] Reply(string url) => Encoding.UTF8.GetBytes(
            $$$"""{"response":{"publishedfiledetails":[{"result":1,"consumer_app_id":440,"preview_url":"{{{url}}}"}]}}""");

        Assert.Equal("https://images.steamusercontent.com/ugc/1/A/", WorkshopMaps.ParsePreview(Reply("https://images.steamusercontent.com/ugc/1/A/")));
        Assert.Null(WorkshopMaps.ParsePreview(Reply("http://images.steamusercontent.com/ugc/1/A/")));
        Assert.Null(WorkshopMaps.ParsePreview(Reply("https://evil.example/ugc/1/A/")));
    }

    [Fact]
    public void Nearest_region_needs_enough_servers_to_judge()
    {
        var samples = Enumerable.Repeat((ServerRegion.SouthAmerica, 30), 6)
            .Concat(Enumerable.Repeat((ServerRegion.Europe, 200), 20))
            .Append((ServerRegion.Asia, 5));
        Assert.Equal(ServerRegion.SouthAmerica, Regions.Nearest(samples));
        Assert.Equal(ServerRegion.SouthAmerica, Regions.OfCountry("br"));
        Assert.Equal(ServerRegion.Unknown, Regions.OfCountry("ZZ"));
    }

    [Theory]
    [InlineData(ServerRegion.SouthAmerica, ServerRegion.Europe, 20, true)]
    [InlineData(ServerRegion.SouthAmerica, ServerRegion.Europe, 190, false)]
    [InlineData(ServerRegion.SouthAmerica, ServerRegion.NorthAmerica, 100, false)]
    [InlineData(ServerRegion.Europe, ServerRegion.Europe, 5, false)]
    [InlineData(ServerRegion.Unknown, ServerRegion.Europe, 5, false)]
    public void Pings_faster_than_the_distance_allows_come_from_a_relay(ServerRegion player, ServerRegion server, int ping, bool relayed) =>
        Assert.Equal(relayed, Regions.IsRelayed(player, server, ping));

    [Theory]
    [InlineData(24, 0, true)]
    [InlineData(24, 23, false)]
    [InlineData(20, 12, true)]
    [InlineData(3, 0, false)]
    [InlineData(10, 10, false)]
    public void Fake_player_counts_are_spotted(int claimed, int listed, bool fake)
    {
        var players = Enumerable.Range(0, listed).Select(i => new PlayerEntry($"p{i}", 0, TimeSpan.FromMinutes(5))).ToList();
        Assert.Equal(fake, ServerChecks.LooksFake(claimed, players));
        Assert.False(ServerChecks.LooksFake(claimed, null));
    }

    private static byte[] Info(string name, string map, int players, int max, int bots, bool password, bool vac, string keywords)
    {
        var b = new List<byte> { 0x49, 17 };
        void Str(string s) => b.AddRange([.. Encoding.UTF8.GetBytes(s), 0]);
        Str(name);
        Str(map);
        Str("tf");
        Str("Team Fortress");
        b.AddRange([0xB8, 0x01]);
        b.AddRange([(byte)players, (byte)max, (byte)bots, (byte)'d', (byte)'l', (byte)(password ? 1 : 0), (byte)(vac ? 1 : 0)]);
        Str("10183095");
        b.Add(0x80 | 0x20);
        b.AddRange([0x87, 0x69]);
        Str(keywords);
        return [.. b];
    }

    private static byte[] Player(string name, int score, float seconds)
    {
        var b = new List<byte> { 0 };
        b.AddRange([.. Encoding.UTF8.GetBytes(name), 0]);
        var buffer = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, score);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(4), seconds);
        b.AddRange(buffer);
        return [.. b];
    }

    private static byte[] Split(int id, byte total, byte number, byte[] payload)
    {
        var header = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(header, -2);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), id);
        header[8] = total;
        header[9] = number;
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(10), 1248);
        return [.. header, .. payload];
    }
}

public class ServerJoinTests
{
    [Theory]
    [InlineData("hunter2", true)]
    [InlineData("a.b_c-d~e!f$g(h)i*j,k=l:m@n", true)]
    [InlineData("-quit", false)]
    [InlineData("two words", false)]
    [InlineData("semi;colon", false)]
    [InlineData("quote\"", false)]
    [InlineData("slash/x", false)]
    [InlineData("percent%41", false)]
    [InlineData("", false)]
    public void Only_passwords_that_survive_a_url_and_a_command_line_are_used(string password, bool safe) =>
        Assert.Equal(safe, ServerJoin.IsSafePassword(password));

    [Fact]
    public void The_password_is_set_before_connecting()
    {
        ServerAddress.TryParse("1.2.3.4:27015", out var address);
        Assert.Equal(["+password", "pw", "+connect", "1.2.3.4:27015"], ServerJoin.LaunchArguments(address, "pw"));
        Assert.Throws<ArgumentException>(() => ServerJoin.LaunchArguments(address, "x +exec evil"));
    }

    [Fact]
    public void Launch_logs_hide_the_password() =>
        Assert.Equal(["-novid", "+password", "***", "+connect", "1.2.3.4:27015"],
            Eternal.Core.Steam.GameLauncher.Redacted(["-novid", "+password", "secret", "+connect", "1.2.3.4:27015"]));
}
