using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Eternal.Core.Servers;

public sealed record ServerInfo(
    string Name,
    string Map,
    string Folder,
    string Game,
    int Players,
    int MaxPlayers,
    int Bots,
    bool Password,
    bool Vac,
    string Version,
    int GamePort,
    ulong SteamId,
    string Keywords,
    ulong GameId)
{
    public IReadOnlyList<string> Tags => Keywords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public sealed record PlayerEntry(string Name, int Score, TimeSpan Time);

public static class SourceQuery
{
    private const int MaxDatagram = 1400 + 64;
    private const int MaxParts = 32;
    private const int MaxMessage = 64 * 1024;
    private const int MaxString = 256;
    private const int MaxRules = 512;

    private static readonly byte[] InfoRequest = [0xFF, 0xFF, 0xFF, 0xFF, 0x54, .. "Source Engine Query"u8, 0];

    public static async Task<(ServerInfo Info, int Ping)?> InfoAsync(IPEndPoint server, TimeSpan timeout, CancellationToken cancellation = default)
    {
        using var socket = Open(server);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout);
        try
        {
            var watch = Stopwatch.StartNew();
            var reply = await ExchangeAsync(socket, InfoRequest, limit.Token).ConfigureAwait(false);
            for (var tries = 0; reply.Length >= 5 && reply[0] == 0x41 && tries < 2; tries++)
            {
                watch.Restart();
                reply = await ExchangeAsync(socket, [.. InfoRequest, .. reply.AsSpan(1, 4)], limit.Token).ConfigureAwait(false);
            }
            var ping = (int)Math.Min(watch.ElapsedMilliseconds, 9999);
            return ParseInfo(reply) is { } info ? (info, ping) : null;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or InvalidDataException && !cancellation.IsCancellationRequested)
        {
            return null;
        }
    }

    public static async Task<IReadOnlyList<PlayerEntry>?> PlayersAsync(IPEndPoint server, TimeSpan timeout, CancellationToken cancellation = default)
    {
        var reply = await ChallengedAsync(server, 0x55, 0x44, timeout, cancellation).ConfigureAwait(false);
        return reply is null ? null : ParsePlayers(reply);
    }

    public static async Task<IReadOnlyList<KeyValuePair<string, string>>?> RulesAsync(IPEndPoint server, TimeSpan timeout, CancellationToken cancellation = default)
    {
        var reply = await ChallengedAsync(server, 0x56, 0x45, timeout, cancellation).ConfigureAwait(false);
        return reply is null ? null : ParseRules(reply);
    }

    private static async Task<byte[]?> ChallengedAsync(IPEndPoint server, byte request, byte expected, TimeSpan timeout, CancellationToken cancellation)
    {
        using var socket = Open(server);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout);
        try
        {
            byte[] challenge = [0xFF, 0xFF, 0xFF, 0xFF];
            for (var tries = 0; tries < 3; tries++)
            {
                var reply = await ExchangeAsync(socket, [0xFF, 0xFF, 0xFF, 0xFF, request, .. challenge], limit.Token).ConfigureAwait(false);
                if (reply.Length >= 5 && reply[0] == 0x41)
                {
                    challenge = reply[1..5];
                    continue;
                }
                return reply.Length > 0 && reply[0] == expected ? reply : null;
            }
            return null;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or InvalidDataException && !cancellation.IsCancellationRequested)
        {
            return null;
        }
    }

    private static Socket Open(IPEndPoint server)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Connect(server);
        return socket;
    }

    private static async Task<byte[]> ExchangeAsync(Socket socket, byte[] request, CancellationToken cancellation)
    {
        await socket.SendAsync(request, SocketFlags.None, cancellation).ConfigureAwait(false);
        var buffer = new byte[MaxDatagram];
        Dictionary<int, byte[]>? parts = null;
        int splitId = 0, total = 0;
        while (true)
        {
            var length = await socket.ReceiveAsync(buffer, SocketFlags.None, cancellation).ConfigureAwait(false);
            if (length < 5)
                throw new InvalidDataException("Reply too short.");
            var header = BinaryPrimitives.ReadInt32LittleEndian(buffer);
            if (header == -1)
                return buffer.AsSpan(4, length - 4).ToArray();
            if (header != -2 || length < 12)
                throw new InvalidDataException("Unknown reply header.");

            var id = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(4));
            if ((id & unchecked((int)0x80000000)) != 0)
                throw new InvalidDataException("Compressed replies aren't accepted.");
            int count = buffer[8], number = buffer[9];
            if (count is 0 or > MaxParts || number >= count)
                throw new InvalidDataException("Bad split reply.");
            if (parts is null)
            {
                parts = [];
                splitId = id;
                total = count;
            }
            else if (id != splitId || count != total)
            {
                continue;
            }
            parts[number] = buffer.AsSpan(12, length - 12).ToArray();
            if (parts.Values.Sum(p => p.Length) > MaxMessage)
                throw new InvalidDataException("Reply too large.");
            if (parts.Count < total)
                continue;

            var whole = new byte[parts.Values.Sum(p => p.Length)];
            var offset = 0;
            for (var i = 0; i < total; i++)
            {
                parts[i].CopyTo(whole, offset);
                offset += parts[i].Length;
            }
            if (whole.Length < 4 || BinaryPrimitives.ReadInt32LittleEndian(whole) != -1)
                throw new InvalidDataException("Bad split payload.");
            return whole[4..];
        }
    }

    internal static ServerInfo? ParseInfo(byte[] reply)
    {
        try
        {
            return reply.Length > 0 && reply[0] == 0x49 ? ReadInfo(new Reader(reply, 1)) : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static ServerInfo ReadInfo(Reader r)
    {
        r.Byte();
        var name = r.String();
        var map = r.String();
        var folder = r.String();
        var game = r.String();
        var appId = r.Short();
        int players = r.Byte(), maxPlayers = r.Byte(), bots = r.Byte();
        r.Byte();
        r.Byte();
        var password = r.Byte() == 1;
        var vac = r.Byte() == 1;
        var version = r.String();
        int gamePort = 0;
        ulong steamId = 0, gameId = (ulong)appId;
        var keywords = "";
        if (r.Remaining > 0)
        {
            var flags = r.Byte();
            if ((flags & 0x80) != 0)
                gamePort = r.Short();
            if ((flags & 0x10) != 0)
                steamId = r.Long();
            if ((flags & 0x40) != 0)
            {
                r.Short();
                r.String();
            }
            if ((flags & 0x20) != 0)
                keywords = r.String();
            if ((flags & 0x01) != 0)
                gameId = r.Long();
        }
        return new ServerInfo(name, map, folder, game, players, maxPlayers, bots, password, vac, version, gamePort, steamId, keywords, gameId);
    }

    internal static IReadOnlyList<PlayerEntry>? ParsePlayers(byte[] reply)
    {
        if (reply.Length < 2 || reply[0] != 0x44)
            return null;
        var r = new Reader(reply, 1);
        var count = r.Byte();
        var players = new List<PlayerEntry>(count);
        try
        {
            for (var i = 0; i < count && r.Remaining > 0; i++)
            {
                r.Byte();
                var name = r.String();
                var score = r.Int();
                var seconds = r.Float();
                players.Add(new PlayerEntry(name, score, float.IsFinite(seconds) && seconds >= 0 ? TimeSpan.FromSeconds(Math.Min(seconds, 86400 * 30)) : TimeSpan.Zero));
            }
        }
        catch (InvalidDataException)
        {
            return null;
        }
        return players;
    }

    internal static IReadOnlyList<KeyValuePair<string, string>>? ParseRules(byte[] reply)
    {
        if (reply.Length < 3 || reply[0] != 0x45)
            return null;
        var r = new Reader(reply, 1);
        var count = Math.Min(r.Short(), MaxRules);
        var rules = new List<KeyValuePair<string, string>>(count);
        try
        {
            for (var i = 0; i < count && r.Remaining > 0; i++)
                rules.Add(new(r.String(), r.String()));
        }
        catch (InvalidDataException)
        {
        }
        return rules;
    }

    private ref struct Reader(byte[] data, int position)
    {
        private int _position = position;

        public readonly int Remaining => data.Length - _position;

        public byte Byte()
        {
            Need(1);
            return data[_position++];
        }

        public int Short()
        {
            Need(2);
            var value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(_position));
            _position += 2;
            return value;
        }

        public int Int()
        {
            Need(4);
            var value = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(_position));
            _position += 4;
            return value;
        }

        public float Float()
        {
            Need(4);
            var value = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(_position));
            _position += 4;
            return value;
        }

        public ulong Long()
        {
            Need(8);
            var value = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(_position));
            _position += 8;
            return value;
        }

        public string String()
        {
            var end = Array.IndexOf(data, (byte)0, _position);
            if (end < 0)
                throw new InvalidDataException("Unterminated string.");
            var text = Clean(Encoding.UTF8.GetString(data, _position, Math.Min(end - _position, MaxString * 4)));
            _position = end + 1;
            return text;
        }

        private readonly void Need(int bytes)
        {
            if (Remaining < bytes)
                throw new InvalidDataException("Reply cut short.");
        }
    }

    internal static string Clean(string text)
    {
        var sb = new StringBuilder(Math.Min(text.Length, MaxString));
        foreach (var c in text)
        {
            if (sb.Length >= MaxString)
                break;
            if (!char.IsControl(c) && c is not ('​' or '‮' or '‭' or '⁦' or '⁧' or '⁨' or '⁩' or '﻿'))
                sb.Append(c);
        }
        return sb.ToString().Trim();
    }
}
