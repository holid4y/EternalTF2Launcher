namespace Eternal.Core.Servers;

public enum ServerRegion { Unknown, NorthAmerica, SouthAmerica, Europe, Asia, Oceania, MiddleEast, Africa }

public static class Regions
{
    public static readonly ServerRegion[] Choices =
        [ServerRegion.NorthAmerica, ServerRegion.SouthAmerica, ServerRegion.Europe, ServerRegion.Asia, ServerRegion.Oceania, ServerRegion.MiddleEast, ServerRegion.Africa];

    public static string Name(ServerRegion region) => region switch
    {
        ServerRegion.NorthAmerica => "North America",
        ServerRegion.SouthAmerica => "South America",
        ServerRegion.Europe => "Europe",
        ServerRegion.Asia => "Asia",
        ServerRegion.Oceania => "Oceania",
        ServerRegion.MiddleEast => "Middle East",
        ServerRegion.Africa => "Africa",
        _ => "Unknown",
    };

    private static readonly Dictionary<string, ServerRegion> ByCountry = Build();

    // Rough lower bounds for a round trip between regions, from fibre distance. Nothing real answers faster.
    private static readonly Dictionary<(ServerRegion, ServerRegion), int> Floors = new()
    {
        [(ServerRegion.NorthAmerica, ServerRegion.SouthAmerica)] = 90,
        [(ServerRegion.NorthAmerica, ServerRegion.Europe)] = 70,
        [(ServerRegion.NorthAmerica, ServerRegion.Asia)] = 100,
        [(ServerRegion.NorthAmerica, ServerRegion.Oceania)] = 140,
        [(ServerRegion.NorthAmerica, ServerRegion.MiddleEast)] = 120,
        [(ServerRegion.NorthAmerica, ServerRegion.Africa)] = 150,
        [(ServerRegion.SouthAmerica, ServerRegion.Europe)] = 160,
        [(ServerRegion.SouthAmerica, ServerRegion.Asia)] = 250,
        [(ServerRegion.SouthAmerica, ServerRegion.Oceania)] = 200,
        [(ServerRegion.SouthAmerica, ServerRegion.MiddleEast)] = 220,
        [(ServerRegion.SouthAmerica, ServerRegion.Africa)] = 180,
        [(ServerRegion.Europe, ServerRegion.Asia)] = 120,
        [(ServerRegion.Europe, ServerRegion.Oceania)] = 250,
        [(ServerRegion.Europe, ServerRegion.MiddleEast)] = 50,
        [(ServerRegion.Europe, ServerRegion.Africa)] = 60,
        [(ServerRegion.Asia, ServerRegion.Oceania)] = 90,
        [(ServerRegion.Asia, ServerRegion.MiddleEast)] = 80,
        [(ServerRegion.Asia, ServerRegion.Africa)] = 180,
        [(ServerRegion.Oceania, ServerRegion.MiddleEast)] = 180,
        [(ServerRegion.Oceania, ServerRegion.Africa)] = 250,
        [(ServerRegion.MiddleEast, ServerRegion.Africa)] = 80,
    };

    public static int MinimumPing(ServerRegion from, ServerRegion to) =>
        from == to || from == ServerRegion.Unknown || to == ServerRegion.Unknown ? 0
        : Floors.TryGetValue((from, to), out var ms) || Floors.TryGetValue((to, from), out ms) ? ms : 0;

    /// <summary>
    /// A ping under half of what the distance allows: the server answers through
    /// a relay near the player (DDoS protection does this), not from where it plays.
    /// </summary>
    public static bool IsRelayed(ServerRegion player, ServerRegion server, int ping) =>
        ping >= 0 && ping < MinimumPing(player, server) / 2;

    public static ServerRegion OfCountry(string? code) =>
        code is { Length: 2 } && ByCountry.TryGetValue(code.ToUpperInvariant(), out var region) ? region : ServerRegion.Unknown;

    /// <summary>
    /// The region whose servers answer fastest, judged by the median ping of at
    /// least five of them, so one oddly fast server elsewhere doesn't decide it.
    /// </summary>
    public static ServerRegion Nearest(IEnumerable<(ServerRegion Region, int Ping)> samples)
    {
        var best = ServerRegion.Unknown;
        var bestPing = int.MaxValue;
        foreach (var group in samples.Where(s => s.Region != ServerRegion.Unknown && s.Ping > 0).GroupBy(s => s.Region))
        {
            var pings = group.Select(s => s.Ping).Order().ToArray();
            if (pings.Length < 5)
                continue;
            var median = pings[pings.Length / 2];
            if (median < bestPing)
                (best, bestPing) = (group.Key, median);
        }
        return best;
    }

    private static Dictionary<string, ServerRegion> Build()
    {
        var map = new Dictionary<string, ServerRegion>(StringComparer.Ordinal);
        void Add(ServerRegion region, string codes)
        {
            foreach (var code in codes.Split(' '))
                map[code] = region;
        }
        Add(ServerRegion.NorthAmerica, "US CA MX GT BZ HN SV NI CR PA CU DO HT JM PR BS BB TT AG DM GD KN LC VC GL BM KY TC VG VI AW CW SX MQ GP");
        Add(ServerRegion.SouthAmerica, "BR AR CL UY PY BO PE EC CO VE GY SR GF FK");
        Add(ServerRegion.Europe, "GB IE FR DE NL BE LU CH AT IT ES PT AD MC SM VA MT DK NO SE FI IS EE LV LT PL CZ SK HU SI HR BA RS ME MK AL GR BG RO MD UA BY RU XK CY GI IM JE GG FO AX LI");
        Add(ServerRegion.Asia, "CN HK MO TW JP KR KP MN SG MY ID TH VN PH KH LA MM BN TL IN PK BD LK NP BT MV KZ UZ TM KG TJ AF");
        Add(ServerRegion.Oceania, "AU NZ PG FJ SB VU NC PF WS TO KI FM MH PW NR TV GU MP AS CK NU");
        Add(ServerRegion.MiddleEast, "TR IL PS JO LB SY IQ IR SA AE QA BH KW OM YE AM AZ GE");
        Add(ServerRegion.Africa, "EG LY TN DZ MA SD SS ET ER DJ SO KE UG TZ RW BI CD CG CF CM GA GQ NG NE TD ML MR SN GM GW GN SL LR CI BF GH TG BJ AO ZM ZW MW MZ MG MU SC KM NA BW ZA LS SZ CV ST RE YT");
        return map;
    }
}
