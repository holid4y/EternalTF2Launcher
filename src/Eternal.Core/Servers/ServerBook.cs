using System.Globalization;
using System.Text.Json.Serialization;
using Eternal.Core.Logging;
using Eternal.Core.Persistence;
using Eternal.Core.Steam;

namespace Eternal.Core.Servers;

public sealed class SavedServer
{
    public string Address { get; set; } = "";
    public string Name { get; set; } = "";
    public long At { get; set; }
}

public sealed class ServerBookData : IJsonOnDeserialized
{
    public List<SavedServer> Favorites { get; set; } = [];
    public List<SavedServer> Recent { get; set; } = [];

    void IJsonOnDeserialized.OnDeserialized()
    {
        JsonChecks.NoEmptyEntries(Favorites, "favorites");
        JsonChecks.NoEmptyEntries(Recent, "recent");
    }
}

[JsonSourceGenerationOptions(RespectNullableAnnotations = true, WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ServerBookData))]
internal sealed partial class ServerBookJson : JsonSerializerContext;

public sealed record BookEntry(ServerAddress Address, string Name, DateTimeOffset? At);

public sealed class ServerBook
{
    private const int RecentKept = 60;
    private const int MaxName = 128;

    private readonly string _file;
    private readonly SteamWriteQueue _steam;
    private readonly Func<string?> _userDir;
    private readonly object _gate = new();
    private readonly ServerBookData _data;

    public ServerBook(string file, SteamWriteQueue steam, Func<string?> userDir)
    {
        _file = file;
        _steam = steam;
        _userDir = userDir;
        _data = JsonFile.Load(file, ServerBookJson.Default.ServerBookData, () => new ServerBookData(), out var outcome);
        LoadOutcome = outcome;
        Clean(_data.Favorites);
        Clean(_data.Recent);
    }

    public LoadOutcome LoadOutcome { get; }

    public event Action? Changed;

    public IReadOnlyList<BookEntry> Favorites()
    {
        var (steamFavorites, _) = ReadSteam();
        var removed = PendingTargets(SteamWriteKind.RemoveFavorite);
        lock (_gate)
        {
            var result = new Dictionary<ServerAddress, BookEntry>();
            foreach (var saved in _data.Favorites)
                Add(result, saved);
            foreach (var entry in steamFavorites.Where(e => !removed.Contains(e.Address.ToString())))
                result.TryAdd(entry.Address, entry);
            return [.. result.Values.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)];
        }
    }

    public IReadOnlyList<BookEntry> Recent()
    {
        var (_, steamHistory) = ReadSteam();
        lock (_gate)
        {
            var result = new Dictionary<ServerAddress, BookEntry>();
            foreach (var saved in _data.Recent)
                Add(result, saved);
            foreach (var entry in steamHistory)
            {
                if (!result.TryGetValue(entry.Address, out var known))
                    result[entry.Address] = entry;
                else if ((known.At ?? DateTimeOffset.MinValue) < (entry.At ?? DateTimeOffset.MinValue))
                    result[entry.Address] = known with { At = entry.At };
            }
            return [.. result.Values.OrderByDescending(e => e.At).Take(RecentKept)];
        }
    }

    public bool IsFavorite(ServerAddress address) => Favorites().Any(f => f.Address == address);

    public void AddFavorite(ServerAddress address, string name)
    {
        var target = address.ToString();
        lock (_gate)
        {
            _data.Favorites.RemoveAll(f => f.Address == target);
            _data.Favorites.Add(new SavedServer { Address = target, Name = Trim(name), At = Now() });
            Save();
        }
        Queue(SteamWriteKind.AddFavorite, target);
        Changed?.Invoke();
    }

    public void RemoveFavorite(ServerAddress address)
    {
        var target = address.ToString();
        lock (_gate)
        {
            _data.Favorites.RemoveAll(f => f.Address == target);
            Save();
        }
        Queue(SteamWriteKind.RemoveFavorite, target);
        Changed?.Invoke();
    }

    public void Played(ServerAddress address, string name)
    {
        var target = address.ToString();
        lock (_gate)
        {
            _data.Recent.RemoveAll(r => r.Address == target);
            _data.Recent.Insert(0, new SavedServer { Address = target, Name = Trim(name), At = Now() });
            if (_data.Recent.Count > RecentKept)
                _data.Recent.RemoveRange(RecentKept, _data.Recent.Count - RecentKept);
            Save();
        }
        Queue(SteamWriteKind.AddHistory, target);
        Changed?.Invoke();
    }

    public void Rename(ServerAddress address, string name)
    {
        var target = address.ToString();
        var changed = false;
        lock (_gate)
        {
            foreach (var saved in _data.Favorites.Concat(_data.Recent).Where(s => s.Address == target && s.Name != Trim(name)))
            {
                saved.Name = Trim(name);
                changed = true;
            }
            if (changed)
                Save();
        }
    }

    private void Queue(SteamWriteKind kind, string target)
    {
        if (_userDir() is not { } dir)
            return;
        try
        {
            _steam.Add(new SteamWrite { Kind = kind, UserDir = dir, Target = target, Time = Now() });
        }
        catch (ArgumentException ex)
        {
            Log.Warn($"Not passing {target} to Steam: {ex.Message}");
        }
    }

    private HashSet<string> PendingTargets(SteamWriteKind kind) =>
        _steam.Pending.Where(w => w.Kind == kind).Select(w => w.Target).ToHashSet(StringComparer.Ordinal);

    private (List<BookEntry> Favorites, List<BookEntry> History) ReadSteam()
    {
        if (_userDir() is not { } dir)
            return ([], []);
        string? text;
        try
        {
            text = AtomicFile.ReadAllTextOrNull(Path.Combine(dir, SteamWriteQueue.ServerHistoryFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Couldn't read Steam's own server lists, so only the launcher's are shown: {ex.Message}");
            return ([], []);
        }
        if (text is null)
            return ([], []);
        var filters = Vdf.Parse(text).Section("Filters");
        return (Entries(filters?.Section("Favorites")), Entries(filters?.Section("History")));
    }

    internal static List<BookEntry> Entries(VdfNode? list)
    {
        var result = new List<BookEntry>();
        foreach (var (_, value) in list?.Children ?? [])
        {
            if (value is not VdfNode node || !ServerAddress.TryParse(node.Value("address"), out var address))
                continue;
            if (node.Value("appid") is { } app && app is not ("0" or "440"))
                continue;
            DateTimeOffset? at = long.TryParse(node.Value("LastPlayed"), NumberStyles.None, CultureInfo.InvariantCulture, out var t) && t is > 0 and < 253402300799
                ? DateTimeOffset.FromUnixTimeSeconds(t) : null;
            var name = node.Value("name") is { Length: > 0 } n && n != node.Value("address") ? Trim(SourceQuery.Clean(n)) : address.ToString();
            result.Add(new BookEntry(address, name, at));
        }
        return result;
    }

    private static void Add(Dictionary<ServerAddress, BookEntry> into, SavedServer saved)
    {
        if (ServerAddress.TryParse(saved.Address, out var address))
            into[address] = new BookEntry(address, saved.Name.Length > 0 ? saved.Name : saved.Address, saved.At > 0 ? DateTimeOffset.FromUnixTimeSeconds(saved.At) : null);
    }

    private static void Clean(List<SavedServer> list)
    {
        list.RemoveAll(s => !ServerAddress.TryParse(s.Address, out var a) || a.ToString() != s.Address || s.At is < 0 or > 253402300799);
        foreach (var s in list)
            s.Name = Trim(SourceQuery.Clean(s.Name ?? ""));
    }

    private static string Trim(string name) => name.Length > MaxName ? name[..MaxName] : name;

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private void Save()
    {
        if (!LoadOutcome.CanSave)
            return;
        try
        {
            JsonFile.Save(_file, _data, ServerBookJson.Default.ServerBookData);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Couldn't save the server lists: {ex.Message}");
        }
    }
}
