using System.Globalization;
using System.Text.Json.Serialization;
using Eternal.Core.Logging;
using Eternal.Core.Persistence;
using Eternal.Core.Servers;

namespace Eternal.Core.Steam;

public enum SteamWriteKind { AddFavorite, RemoveFavorite, AddHistory, RemoveScreenshot, ResizeScreenshot }

public sealed class SteamWrite
{
    public SteamWriteKind Kind { get; set; }
    public string UserDir { get; set; } = "";
    public string Target { get; set; } = "";
    public long Time { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    internal bool IsServer => Kind is SteamWriteKind.AddFavorite or SteamWriteKind.RemoveFavorite or SteamWriteKind.AddHistory;
}

public sealed class SteamWriteList : IJsonOnDeserialized
{
    public List<SteamWrite> Writes { get; set; } = [];

    void IJsonOnDeserialized.OnDeserialized() => JsonChecks.NoEmptyEntries(Writes, "writes");
}

[JsonSourceGenerationOptions(RespectNullableAnnotations = true, WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SteamWriteList))]
internal sealed partial class SteamWriteJson : JsonSerializerContext;

public sealed record SteamWriteResult(int Applied, bool SteamRunning, IReadOnlyList<string> Problems);

public sealed class SteamWriteQueue
{
    public const string ScreenshotsFile = "760/screenshots.vdf";
    public const string ServerHistoryFile = "7/remote/serverbrowser_hist.vdf";
    private const int BackupsKept = 10;

    private readonly string _file;
    private readonly string _backups;
    private readonly Func<bool> _steamRunning;
    private readonly object _gate = new();
    private readonly SteamWriteList _list;

    public SteamWriteQueue(string file, string backups, Func<bool>? steamRunning = null)
    {
        _file = file;
        _backups = backups;
        _steamRunning = steamRunning ?? SteamProcess.IsRunning;
        _list = JsonFile.Load(file, SteamWriteJson.Default.SteamWriteList, () => new SteamWriteList(), out _);
        _list.Writes.RemoveAll(w => !IsValid(w));
    }

    public event Action? Changed;

    public IReadOnlyList<SteamWrite> Pending
    {
        get
        {
            lock (_gate)
                return _list.Writes.ToList();
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
                return _list.Writes.Count;
        }
    }

    public void Add(SteamWrite write)
    {
        if (!IsValid(write))
            throw new ArgumentException("That change can't be written to Steam's files.", nameof(write));
        lock (_gate)
        {
            var writes = _list.Writes;
            switch (write.Kind)
            {
                case SteamWriteKind.AddFavorite or SteamWriteKind.RemoveFavorite:
                    writes.RemoveAll(w => w.UserDir == write.UserDir && w.Target == write.Target && w.Kind is SteamWriteKind.AddFavorite or SteamWriteKind.RemoveFavorite);
                    break;
                case SteamWriteKind.AddHistory:
                    writes.RemoveAll(w => w.UserDir == write.UserDir && w.Target == write.Target && w.Kind == SteamWriteKind.AddHistory);
                    break;
                case SteamWriteKind.RemoveScreenshot:
                    writes.RemoveAll(w => w.UserDir == write.UserDir && w.Target == write.Target && w.Kind is SteamWriteKind.RemoveScreenshot or SteamWriteKind.ResizeScreenshot);
                    break;
                case SteamWriteKind.ResizeScreenshot:
                    if (writes.Any(w => w.UserDir == write.UserDir && w.Target == write.Target && w.Kind == SteamWriteKind.RemoveScreenshot))
                        return;
                    writes.RemoveAll(w => w.UserDir == write.UserDir && w.Target == write.Target && w.Kind == SteamWriteKind.ResizeScreenshot);
                    break;
            }
            writes.Add(write);
            Save();
        }
        Changed?.Invoke();
    }

    public SteamWriteResult TryApply()
    {
        if (Count == 0)
            return new SteamWriteResult(0, false, []);
        if (_steamRunning())
            return new SteamWriteResult(0, true, []);

        var problems = new List<string>();
        var applied = 0;
        List<SteamWrite> writes;
        lock (_gate)
            writes = _list.Writes.ToList();

        foreach (var group in writes.GroupBy(w => (w.UserDir, w.IsServer)))
        {
            var (userDir, isServer) = group.Key;
            var path = Path.Combine(userDir, isServer ? ServerHistoryFile : ScreenshotsFile);
            try
            {
                var text = AtomicFile.ReadAllTextOrNull(path);
                if (text is null && !isServer)
                {
                    Done(group);
                    continue;
                }
                var root = text is null ? new VdfNode() : Vdf.Parse(text);
                if (isServer)
                    ApplyServers(root, group);
                else
                    ApplyScreenshots(root, group);
                if (text is not null)
                    Backup(userDir, path, text);
                AtomicFile.WriteAllText(path, Vdf.Write(root), keepBackup: false);
                applied += group.Count();
                Done(group);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{Path.GetFileName(path)}: {ex.Message}");
                Log.Warn($"Couldn't update {path}: {ex.Message}");
            }
        }
        Changed?.Invoke();
        return new SteamWriteResult(applied, false, problems);
    }

    private void Done(IEnumerable<SteamWrite> writes)
    {
        lock (_gate)
        {
            foreach (var w in writes)
                _list.Writes.Remove(w);
            Save();
        }
    }

    internal static void ApplyServers(VdfNode root, IEnumerable<SteamWrite> writes)
    {
        var filters = root.Ensure("Filters");
        var favorites = filters.Ensure("Favorites");
        var history = filters.Ensure("History");
        foreach (var write in writes)
        {
            switch (write.Kind)
            {
                case SteamWriteKind.AddFavorite when Find(favorites, write.Target) is null:
                    Append(favorites, write.Target, write.Time);
                    break;
                case SteamWriteKind.RemoveFavorite:
                    while (Find(favorites, write.Target) is { } key)
                        favorites.Remove(key);
                    break;
                case SteamWriteKind.AddHistory:
                    if (Find(history, write.Target) is { } existing && history.Section(existing) is { } entry)
                        entry.Set("LastPlayed", write.Time.ToString(CultureInfo.InvariantCulture));
                    else
                        Append(history, write.Target, write.Time);
                    break;
            }
        }
    }

    private static string? Find(VdfNode list, string address) =>
        list.Children.FirstOrDefault(c => c.Value is VdfNode n && n.Value("address") == address).Key;

    private static void Append(VdfNode list, string address, long time)
    {
        var next = list.Children.Select(c => int.TryParse(c.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0).DefaultIfEmpty(0).Max() + 1;
        var entry = new VdfNode();
        entry.Set("name", address);
        entry.Set("address", address);
        entry.Set("LastPlayed", time.ToString(CultureInfo.InvariantCulture));
        entry.Set("appid", "0");
        entry.Set("accountid", "0");
        list.Set(next.ToString(CultureInfo.InvariantCulture), entry);
    }

    internal static void ApplyScreenshots(VdfNode root, IEnumerable<SteamWrite> writes)
    {
        foreach (var game in writes.GroupBy(w => SteamScreenshots.AppOf(w.Target)))
        {
            if (game.Key is { } key && root.Path("screenshots", key) is { } app)
                ApplyToApp(app, game);
        }
    }

    private static void ApplyToApp(VdfNode app, IEnumerable<SteamWrite> writes)
    {
        foreach (var write in writes)
        {
            var key = app.Children.FirstOrDefault(c => c.Value is VdfNode n && n.Value("filename") == write.Target).Key;
            if (key is null)
                continue;
            if (write.Kind == SteamWriteKind.RemoveScreenshot)
            {
                app.Remove(key);
            }
            else if (app.Section(key) is { } entry)
            {
                entry.Set("width", write.Width.ToString(CultureInfo.InvariantCulture));
                entry.Set("height", write.Height.ToString(CultureInfo.InvariantCulture));
            }
        }
        var entries = app.Children.ToList();
        foreach (var (key, _) in entries)
            app.Remove(key);
        for (var i = 0; i < entries.Count; i++)
            app.Set(i.ToString(CultureInfo.InvariantCulture), entries[i].Value);
    }

    private void Backup(string userDir, string path, string text)
    {
        try
        {
            Directory.CreateDirectory(_backups);
            var name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Path.GetFileName(Path.TrimEndingDirectorySeparator(userDir))}-{Path.GetFileName(path)}";
            File.WriteAllText(Path.Combine(_backups, name), text);
            foreach (var old in Directory.EnumerateFiles(_backups).OrderByDescending(f => f, StringComparer.Ordinal).Skip(BackupsKept))
                File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Couldn't back up {Path.GetFileName(path)} first, so it was left alone: {ex.Message}", ex);
        }
    }

    private void Save()
    {
        try
        {
            JsonFile.Save(_file, _list, SteamWriteJson.Default.SteamWriteList);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Couldn't save the Steam changes waiting to be written: {ex.Message}");
        }
    }

    internal static bool IsValid(SteamWrite write)
    {
        if (!IsUserDir(write.UserDir))
            return false;
        return write.Kind switch
        {
            SteamWriteKind.AddFavorite or SteamWriteKind.RemoveFavorite or SteamWriteKind.AddHistory =>
                ServerAddress.TryParse(write.Target, out var a) && a.ToString() == write.Target && write.Time >= 0,
            SteamWriteKind.RemoveScreenshot => SteamScreenshots.IsRecordedName(write.Target),
            SteamWriteKind.ResizeScreenshot => SteamScreenshots.IsRecordedName(write.Target) && write.Width is > 0 and <= 32768 && write.Height is > 0 and <= 32768,
            _ => false,
        };
    }

    private static bool IsUserDir(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Path.IsPathFullyQualified(dir))
            return false;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        var id = Path.GetFileName(full);
        return id.Length is > 0 and <= 10 && id.All(char.IsAsciiDigit)
            && string.Equals(Path.GetFileName(Path.GetDirectoryName(full)), "userdata", StringComparison.OrdinalIgnoreCase);
    }
}
