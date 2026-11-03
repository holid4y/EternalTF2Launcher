using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Eternal.Core.Servers;

public readonly record struct ServerAddress(IPAddress Ip, int Port) : IComparable<ServerAddress>
{
    public const int DefaultPort = 27015;

    public IPEndPoint EndPoint => new(Ip, Port);

    public override string ToString() => $"{Ip}:{Port.ToString(CultureInfo.InvariantCulture)}";

    public static bool TryParse(string? text, out ServerAddress address)
    {
        address = default;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 21)
            return false;

        var colon = text.LastIndexOf(':');
        var host = colon < 0 ? text : text[..colon];
        var port = DefaultPort;
        if (colon >= 0 && !TryPort(text[(colon + 1)..], out port))
            return false;
        if (!IsDottedQuad(host) || !IPAddress.TryParse(host, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || !Routable(ip))
            return false;
        address = new ServerAddress(ip, port);
        return true;
    }

    public bool IsPublic
    {
        get
        {
            var b = Ip.GetAddressBytes();
            return b[0] is not (10 or 127)
                && !(b[0] == 172 && b[1] is >= 16 and <= 31)
                && !(b[0] == 192 && b[1] == 168)
                && !(b[0] == 169 && b[1] == 254)
                && !(b[0] == 100 && b[1] is >= 64 and <= 127);
        }
    }

    public static ServerAddress? From(string? ip, int port) =>
        port is > 0 and <= 65535 && TryParse($"{ip}:{port.ToString(CultureInfo.InvariantCulture)}", out var a) ? a : null;

    public static bool TryPort(string text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;

    private static bool IsDottedQuad(string host)
    {
        var parts = host.Split('.');
        return parts.Length == 4 && parts.All(p => p.Length is > 0 and <= 3 && p.All(char.IsAsciiDigit));
    }

    private static bool Routable(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] != 0 && b[0] < 224 && !ip.Equals(IPAddress.Broadcast);
    }

    public int CompareTo(ServerAddress other)
    {
        var a = Ip.GetAddressBytes();
        var b = other.Ip.GetAddressBytes();
        for (var i = 0; i < 4; i++)
        {
            if (a[i] != b[i])
                return a[i].CompareTo(b[i]);
        }
        return Port.CompareTo(other.Port);
    }
}
