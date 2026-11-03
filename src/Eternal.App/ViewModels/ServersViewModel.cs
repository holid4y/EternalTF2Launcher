using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Eternal.App.Services;
using Eternal.Core;
using Eternal.Core.Logging;
using Eternal.Core.Servers;

namespace Eternal.App.ViewModels;

public sealed partial class ServersViewModel : PageViewModel
{
    private const int Parallel = 48;
    private const int NearPing = 120;
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinRefreshGap = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WatchLimit = TimeSpan.FromMinutes(10);

    private readonly AppServices _services;
    private readonly MainViewModel _main;
    private readonly Dictionary<ServerAddress, ServerRow> _rows = [];
    private readonly ConcurrentQueue<Action> _updates = new();
    private readonly DispatcherTimer _flush;
    private readonly DispatcherTimer _selectedTimer;
    private CancellationTokenSource? _refresh;
    private CancellationTokenSource? _watch;
    private CancellationTokenSource? _details;
    private DateTime _lastInternet = DateTime.MinValue;
    private bool _dirty;
    private bool _loadingFilters;

    public ServersViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
        Regions = [new("auto", "Near me"), .. Eternal.Core.Servers.Regions.Choices.Select(r => new RegionChoice(r.ToString(), Eternal.Core.Servers.Regions.Name(r))), new("all", "Everywhere")];
        Modes = ["All game modes", .. GameModes.All, "Other"];
        PingLimits = ["Any ping", "Under 50", "Under 100", "Under 150", "Under 250"];

        _loadingFilters = true;
        var f = services.Launcher.Settings.Servers;
        _region = Regions.FirstOrDefault(r => r.Id == f.Region) ?? Regions[0];
        _hasPlayers = f.HasPlayers;
        _notFull = f.NotFull;
        _noPassword = f.NoPassword;
        _hideFake = f.HideFake;
        _pingLimit = f.MaxPing switch { 50 => PingLimits[1], 100 => PingLimits[2], 150 => PingLimits[3], 250 => PingLimits[4], _ => PingLimits[0] };
        _mode = Modes.Contains(f.Mode) ? f.Mode : Modes[0];
        _loadingFilters = false;

        _flush = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Flush());
        _selectedTimer = new DispatcherTimer(TimeSpan.FromSeconds(6), DispatcherPriority.Background, (_, _) =>
        {
            if (_main.SelectedTab?.Page == this)
                _ = RefreshSelectedAsync(players: true);
        });
        services.Servers.Changed += () => Dispatcher.UIThread.Post(MarkFavorites);
        services.Launcher.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(HasTeamworkKey));
            OnPropertyChanged(nameof(SourcesNote));
        });
    }

    public IReadOnlyList<RegionChoice> Regions { get; }
    public IReadOnlyList<string> Modes { get; }
    public string[] PingLimits { get; }
    public BulkCollection<ServerRow> Visible { get; } = [];
    public BulkCollection<PlayerRow> SelectedPlayers { get; } = [];
    public BulkCollection<RuleRow> SelectedRules { get; } = [];

    [ObservableProperty] private ServerTab _tab = ServerTab.Internet;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private RegionChoice _region;
    [ObservableProperty] private string _mode;
    [ObservableProperty] private string _pingLimit;
    [ObservableProperty] private bool _hasPlayers;
    [ObservableProperty] private bool _notFull;
    [ObservableProperty] private bool _noPassword;
    [ObservableProperty] private bool _hideFake;
    [ObservableProperty] private ServerSort _sort = ServerSort.Ping;
    [ObservableProperty] private bool _descending;
    [ObservableProperty] private bool _loading;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private ServerRegion _detectedRegion;
    [ObservableProperty] private ServerRow? _selected;
    [ObservableProperty] private string? _mapPicture;
    [ObservableProperty] private bool _loadingPlayers;
    [ObservableProperty] private bool _showRules;
    [ObservableProperty] private string? _watchText;

    public SteamFilesViewModel SteamFiles => _main.SteamFiles;
    public string NameHeader => Header("Server", ServerSort.Name);
    public string PlayersHeader => Header("Players", ServerSort.Players);
    public string MapHeader => Header("Map", ServerSort.Map);
    public string PingHeader => Header("Ping", ServerSort.Ping);
    public string LastPlayedHeader => Header("Played", ServerSort.LastPlayed);
    public string SourcesNote => HasTeamworkKey ? "Lists from GameMonitoring and teamwork.tf. Everything else comes live from each server." : "List from GameMonitoring. Everything else comes live from each server.";

    private string Header(string label, ServerSort column) => Sort == column ? $"{label} {(Descending ? "\u25BE" : "\u25B4")}" : label;

    public bool ShowingInternet => Tab == ServerTab.Internet;
    public bool ShowingFavorites => Tab == ServerTab.Favorites;
    public bool ShowingRecent => Tab == ServerTab.Recent;
    public bool HasSelection => Selected is not null;
    public bool NoPlayersListed => !LoadingPlayers && SelectedPlayers.Count == 0;
    public bool HasTeamworkKey => _services.Launcher.Settings.TeamworkKey is not null;
    public bool Watching => _watch is not null;
    public bool IsEmpty => !Loading && Visible.Count == 0;
    public string EmptyText => Tab switch
    {
        ServerTab.Favorites => "No favorites yet. Star a server, or add one by its address.",
        ServerTab.Recent => "Servers you join from here or from the game show up here.",
        _ when Error is not null => "",
        _ => "No servers match these filters. Try another region, or turn off Has players.",
    };
    public string RegionLabel => Region.Id == "auto" && DetectedRegion != ServerRegion.Unknown
        ? $"Near me: {Eternal.Core.Servers.Regions.Name(DetectedRegion)}"
        : Region.Label;
    public string SelectedTitle => Selected?.Name ?? "";
    public bool SelectedFavorite => Selected?.Favorite == true;
    public string FavoriteLabel => SelectedFavorite ? "Unfavorite" : "Favorite";
    public bool CanWatch => Selected is { Full: true, Answered: true } && !Watching;

    protected override void OnFirstActivation()
    {
        _flush.Start();
        Load(force: false);
    }

    protected override void OnReturn()
    {
        if (Tab != ServerTab.Internet || DateTime.UtcNow - _lastInternet > TimeSpan.FromMinutes(3))
            Load(force: false);
    }

    partial void OnTabChanged(ServerTab value)
    {
        OnPropertyChanged(nameof(ShowingInternet));
        OnPropertyChanged(nameof(ShowingFavorites));
        OnPropertyChanged(nameof(ShowingRecent));
        Sort = value == ServerTab.Recent ? ServerSort.LastPlayed : ServerSort.Ping;
        Descending = value == ServerTab.Recent;
        Load(force: false);
    }

    partial void OnSearchChanged(string value) => Refilter();
    partial void OnRegionChanged(RegionChoice value) { SaveFilters(); Refilter(); OnPropertyChanged(nameof(RegionLabel)); }
    partial void OnModeChanged(string value) { SaveFilters(); Refilter(); }
    partial void OnPingLimitChanged(string value) { SaveFilters(); Refilter(); }
    partial void OnHasPlayersChanged(bool value) { SaveFilters(); Refilter(); }
    partial void OnNotFullChanged(bool value) { SaveFilters(); Refilter(); }
    partial void OnNoPasswordChanged(bool value) { SaveFilters(); Refilter(); }
    partial void OnHideFakeChanged(bool value) { SaveFilters(); Refilter(); }
    partial void OnSortChanged(ServerSort value) => Resorted();
    partial void OnDescendingChanged(bool value) => Resorted();

    private void Resorted()
    {
        foreach (var name in new[] { nameof(NameHeader), nameof(PlayersHeader), nameof(MapHeader), nameof(PingHeader), nameof(LastPlayedHeader) })
            OnPropertyChanged(name);
        Refilter();
    }
    partial void OnDetectedRegionChanged(ServerRegion value) { OnPropertyChanged(nameof(RegionLabel)); Refilter(); }
    partial void OnLoadingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnLoadingPlayersChanged(bool value) => OnPropertyChanged(nameof(NoPlayersListed));
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(EmptyText));

    partial void OnSelectedChanged(ServerRow? oldValue, ServerRow? newValue)
    {
        if (oldValue is not null)
            oldValue.PropertyChanged -= OnSelectedRowChanged;
        if (newValue is not null)
            newValue.PropertyChanged += OnSelectedRowChanged;
        OnPropertyChanged(nameof(HasSelection));
        SelectedChanged();
        ShowRules = false;
        SelectedRules.ReplaceAll([]);
        SelectedPlayers.ReplaceAll([]);
        OnPropertyChanged(nameof(NoPlayersListed));
        MapPicture = null;
        _details?.Cancel();
        if (newValue is null)
        {
            _selectedTimer.Stop();
            return;
        }
        _details = new CancellationTokenSource();
        _ = LoadPictureAsync(newValue, _details.Token);
        _ = RefreshSelectedAsync(players: true);
        _selectedTimer.Start();
    }

    private void OnSelectedRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => SelectedChanged();

    private void SelectedChanged()
    {
        OnPropertyChanged(nameof(SelectedTitle));
        OnPropertyChanged(nameof(SelectedFavorite));
        OnPropertyChanged(nameof(FavoriteLabel));
        OnPropertyChanged(nameof(CanWatch));
    }

    private void SaveFilters()
    {
        if (_loadingFilters)
            return;
        _services.Launcher.Update(s =>
        {
            s.Servers.Region = Region.Id;
            s.Servers.Mode = Mode == Modes[0] ? "" : Mode;
            s.Servers.MaxPing = MaxPing;
            s.Servers.HasPlayers = HasPlayers;
            s.Servers.NotFull = NotFull;
            s.Servers.NoPassword = NoPassword;
            s.Servers.HideFake = HideFake;
        });
    }

    private int MaxPing => Array.IndexOf(PingLimits, PingLimit) switch { 1 => 50, 2 => 100, 3 => 150, 4 => 250, _ => 0 };

    [RelayCommand]
    private void ShowInternet() => Tab = ServerTab.Internet;

    [RelayCommand]
    private void ShowFavorites() => Tab = ServerTab.Favorites;

    [RelayCommand]
    private void ShowRecent() => Tab = ServerTab.Recent;

    [RelayCommand]
    private void SortBy(ServerSort column)
    {
        if (Sort == column)
        {
            Descending = !Descending;
            return;
        }
        Descending = column is ServerSort.Players or ServerSort.LastPlayed;
        Sort = column;
    }

    [RelayCommand]
    private void Refresh() => Load(force: true);

    private void Load(bool force)
    {
        if (Tab == ServerTab.Internet && !force && DateTime.UtcNow - _lastInternet < MinRefreshGap && _rows.Values.Any(r => r.Listed))
        {
            Refilter();
            return;
        }
        _refresh?.Cancel();
        var cts = _refresh = new CancellationTokenSource();
        Error = null;
        Loading = true;

        List<ServerRow> targets;
        if (Tab == ServerTab.Internet)
        {
            _lastInternet = DateTime.UtcNow;
            foreach (var row in _rows.Values)
                row.Listed = false;
            targets = [];
            _ = RunAsync(cts.Token, ListAsync);
        }
        else
        {
            var entries = Tab == ServerTab.Favorites ? _services.Servers.Favorites() : _services.Servers.Recent();
            targets = entries.Select(e =>
            {
                var row = RowFor(e.Address, e.Address.Port);
                if (!row.Answered && e.Name.Length > 0)
                    row.Name = e.Name;
                row.LastPlayed = e.At;
                return row;
            }).ToList();
            _ = RunAsync(cts.Token, (writer, token) => ProduceAsync(targets, writer, token));
        }
        MarkFavorites();
        Refilter();
    }

    private static async Task ProduceAsync(IEnumerable<ServerRow> rows, ChannelWriter<ServerRow> writer, CancellationToken cancellation)
    {
        foreach (var row in rows)
            await writer.WriteAsync(row, cancellation).ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken cancellation, Func<ChannelWriter<ServerRow>, CancellationToken, Task> produce)
    {
        var channel = Channel.CreateUnbounded<ServerRow>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
        var seen = new ConcurrentDictionary<ServerAddress, byte>();
        var producer = Task.Run(async () =>
        {
            try
            {
                await produce(channel.Writer, cancellation).ConfigureAwait(false);
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, cancellation);

        try
        {
            await System.Threading.Tasks.Parallel.ForEachAsync(channel.Reader.ReadAllAsync(cancellation),
                new ParallelOptions { MaxDegreeOfParallelism = Parallel, CancellationToken = cancellation },
                async (row, token) =>
                {
                    if (seen.TryAdd(row.Address, 0))
                        await ProbeAsync(row, token).ConfigureAwait(false);
                }).ConfigureAwait(false);
            await producer.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        _updates.Enqueue(() =>
        {
            if (cancellation.IsCancellationRequested)
                return;
            Loading = false;
            DetectRegion();
            _dirty = true;
        });
    }

    private async Task ListAsync(ChannelWriter<ServerRow> writer, CancellationToken cancellation)
    {
        var country = UserCountry();
        var errors = new ConcurrentBag<string>();
        var sources = new List<Task>
        {
            ReadAsync(new GameMonitoringSource(Http.NoRedirects).ListAsync(country, 8, cancellation), writer, errors, cancellation),
        };
        if (_services.Launcher.Settings.TeamworkKey is { } key)
            sources.Add(ReadAsync(new TeamworkSource(Http.NoRedirects, key).ListAsync(cancellation), writer, errors, cancellation));
        foreach (var favorite in _services.Servers.Favorites())
            await writer.WriteAsync(OnUi(() => RowFor(favorite.Address, favorite.Address.Port)), cancellation).ConfigureAwait(false);
        await Task.WhenAll(sources).ConfigureAwait(false);
        if (!errors.IsEmpty)
            _updates.Enqueue(() => Error = string.Join(" ", errors.Distinct()));
    }

    private static string? UserCountry()
    {
        try
        {
            return CultureInfo.CurrentCulture.IsNeutralCulture || CultureInfo.CurrentCulture.Name.Length == 0
                ? null
                : new RegionInfo(CultureInfo.CurrentCulture.Name).TwoLetterISORegionName;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async Task ReadAsync(IAsyncEnumerable<IReadOnlyList<ListedServer>> pages, ChannelWriter<ServerRow> writer, ConcurrentBag<string> errors, CancellationToken cancellation)
    {
        try
        {
            await foreach (var page in pages.WithCancellation(cancellation).ConfigureAwait(false))
            {
                var rows = OnUi(() =>
                {
                    _dirty = true;
                    return page.Select(listed =>
                    {
                        var row = RowFor(listed.Address, listed.QueryPort);
                        row.Apply(listed);
                        return row;
                    }).ToList();
                });
                foreach (var row in rows)
                    await writer.WriteAsync(row, cancellation).ConfigureAwait(false);
            }
        }
        catch (ServerListException ex)
        {
            Log.Info($"Server list: {ex.Message}");
            errors.Add(ex.Message);
        }
    }

    private static T OnUi<T>(Func<T> work) => Dispatcher.UIThread.Invoke(work);

    private async Task ProbeAsync(ServerRow row, CancellationToken cancellation)
    {
        var endpoint = new IPEndPoint(row.Address.Ip, row.QueryPort);
        var answer = await SourceQuery.InfoAsync(endpoint, QueryTimeout, cancellation).ConfigureAwait(false)
                     ?? await SourceQuery.InfoAsync(endpoint, QueryTimeout, cancellation).ConfigureAwait(false);
        if (answer is not { } a)
        {
            _updates.Enqueue(row.NoAnswer);
            return;
        }
        var fake = false;
        if (a.Info.Players >= 4)
        {
            var players = await SourceQuery.PlayersAsync(endpoint, QueryTimeout, cancellation).ConfigureAwait(false);
            fake = ServerChecks.LooksFake(a.Info.Players, players);
        }
        _updates.Enqueue(() =>
        {
            row.Apply(a.Info, a.Ping);
            row.Fake = fake;
            row.RelayFloor = Eternal.Core.Servers.Regions.IsRelayed(DetectedRegion, row.Region, a.Ping)
                ? Eternal.Core.Servers.Regions.MinimumPing(DetectedRegion, row.Region)
                : 0;
            _services.Servers.Rename(row.Address, row.Name);
        });
    }

    private ServerRow RowFor(ServerAddress address, int queryPort)
    {
        if (!_rows.TryGetValue(address, out var row))
            _rows[address] = row = new ServerRow(address, queryPort);
        return row;
    }

    private void Flush()
    {
        var any = false;
        while (_updates.TryDequeue(out var update))
        {
            update();
            any = true;
        }
        if (any || _dirty)
        {
            _dirty = false;
            Refilter();
        }
    }

    private void Refilter()
    {
        var favorites = Tab == ServerTab.Favorites ? _services.Servers.Favorites().Select(f => f.Address).ToHashSet() : null;
        var recent = Tab == ServerTab.Recent ? _services.Servers.Recent().Select(r => r.Address).ToHashSet() : null;
        var region = Region.Id switch
        {
            "auto" => DetectedRegion,
            "all" => ServerRegion.Unknown,
            var id => Enum.Parse<ServerRegion>(id),
        };
        var regionFilter = Region.Id != "all" && region != ServerRegion.Unknown;
        var query = Search.Trim();
        var maxPing = MaxPing;
        var mode = Mode == Modes[0] ? null : Mode;

        IEnumerable<ServerRow> rows = _rows.Values.Where(r => !r.Valve);
        rows = Tab switch
        {
            ServerTab.Favorites => rows.Where(r => favorites!.Contains(r.Address)),
            ServerTab.Recent => rows.Where(r => recent!.Contains(r.Address)),
            _ => rows.Where(r => r.Listed
                && (!HasPlayers || r.Players > r.Bots)
                && (!NotFull || !r.Full)
                && (!NoPassword || !r.Password)
                && (!HideFake || !r.Fake)
                && (maxPing == 0 || (r.Ping >= 0 && r.EffectivePing < maxPing))
                && (mode is null || r.Mode == mode)
                && (!regionFilter || r.Region == region || (r.Region == ServerRegion.Unknown && r.Ping is >= 0 and <= NearPing))),
        };
        if (query.Length > 0)
            rows = rows.Where(r => r.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || r.Map.Contains(query, StringComparison.OrdinalIgnoreCase)
                || r.AddressText.Contains(query, StringComparison.Ordinal)
                || r.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase)));

        var sorted = Order(rows).ToList();
        Visible.ReplaceAll(sorted);
        OnPropertyChanged(nameof(Selected));
        var players = sorted.Sum(r => Math.Max(0, r.Players - r.Bots));
        Status = sorted.Count == 0 ? "" : $"{Format.Plural(sorted.Count, "server", "servers")} · {Format.Plural(players, "player", "players")}";
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
    }

    private IEnumerable<ServerRow> Order(IEnumerable<ServerRow> rows)
    {
        var unknownLast = rows.OrderBy(r => Sort == ServerSort.Ping && r.Ping < 0);
        if (Sort == ServerSort.Ping)
            return (Descending ? unknownLast.ThenByDescending(r => r.EffectivePing) : unknownLast.ThenBy(r => r.EffectivePing)).ThenBy(r => r.Address);
        IOrderedEnumerable<ServerRow> ordered = Sort switch
        {
            ServerSort.Name => Descending ? unknownLast.ThenByDescending(r => r.Name, StringComparer.CurrentCultureIgnoreCase) : unknownLast.ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
            ServerSort.Players => Descending ? unknownLast.ThenByDescending(r => r.Players - r.Bots).ThenByDescending(r => r.Players) : unknownLast.ThenBy(r => r.Players - r.Bots),
            ServerSort.Map => Descending ? unknownLast.ThenByDescending(r => r.MapName, StringComparer.OrdinalIgnoreCase) : unknownLast.ThenBy(r => r.MapName, StringComparer.OrdinalIgnoreCase),
            ServerSort.LastPlayed => Descending ? unknownLast.ThenByDescending(r => r.LastPlayed) : unknownLast.ThenBy(r => r.LastPlayed),
            _ => Descending ? unknownLast.ThenByDescending(r => r.Ping) : unknownLast.ThenBy(r => r.Ping),
        };
        return ordered.ThenBy(r => r.Address);
    }

    private void DetectRegion()
    {
        var nearest = Eternal.Core.Servers.Regions.Nearest(_rows.Values.Where(r => r.Answered).Select(r => (r.Region, r.Ping)));
        if (nearest != ServerRegion.Unknown)
            DetectedRegion = nearest;
        MarkRelays();
    }

    private void MarkRelays()
    {
        foreach (var row in _rows.Values)
            row.RelayFloor = Eternal.Core.Servers.Regions.IsRelayed(DetectedRegion, row.Region, row.Ping)
                ? Eternal.Core.Servers.Regions.MinimumPing(DetectedRegion, row.Region)
                : 0;
    }

    private void MarkFavorites()
    {
        var favorites = _services.Servers.Favorites().Select(f => f.Address).ToHashSet();
        foreach (var row in _rows.Values)
            row.Favorite = favorites.Contains(row.Address);
        SelectedChanged();
    }

    private async Task LoadPictureAsync(ServerRow row, CancellationToken cancellation)
    {
        var url = await WorkshopMaps.Shared.PreviewAsync(row.Map, cancellation);
        if (!cancellation.IsCancellationRequested && Selected == row)
            MapPicture = url;
    }

    private async Task RefreshSelectedAsync(bool players)
    {
        if (Selected is not { } row)
            return;
        var endpoint = new IPEndPoint(row.Address.Ip, row.QueryPort);
        var token = _details?.Token ?? CancellationToken.None;
        LoadingPlayers = SelectedPlayers.Count == 0;
        try
        {
            var mapBefore = row.Map;
            if (await SourceQuery.InfoAsync(endpoint, QueryTimeout, token) is { } a && Selected == row)
            {
                row.Apply(a.Info, a.Ping);
                if (row.Map != mapBefore)
                    _ = LoadPictureAsync(row, token);
            }
            if (!players)
                return;
            var list = await SourceQuery.PlayersAsync(endpoint, QueryTimeout, token);
            if (Selected != row || list is null)
                return;
            row.Fake = ServerChecks.LooksFake(row.Players, list);
            SelectedPlayers.ReplaceAll(list.OrderByDescending(p => p.Score)
                .Select(p => new PlayerRow(p.Name.Length > 0 ? p.Name : "Connecting…", p.Score.ToString(CultureInfo.InvariantCulture), Duration(p.Time)))
                .ToList());
            OnPropertyChanged(nameof(NoPlayersListed));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            LoadingPlayers = false;
        }
    }

    private static string Duration(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int)time.TotalHours} h {time.Minutes} min" : time.TotalMinutes >= 1 ? $"{(int)time.TotalMinutes} min" : $"{time.Seconds} s";

    [RelayCommand]
    private async Task ToggleRulesAsync()
    {
        ShowRules = !ShowRules;
        if (!ShowRules || Selected is not { } row || SelectedRules.Count > 0)
            return;
        var rules = await SourceQuery.RulesAsync(new IPEndPoint(row.Address.Ip, row.QueryPort), QueryTimeout, _details?.Token ?? default);
        if (Selected == row && rules is not null)
            SelectedRules.ReplaceAll(rules.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase).Select(r => new RuleRow(r.Key, r.Value)).ToList());
    }

    [RelayCommand]
    private async Task JoinAsync(ServerRow? row)
    {
        if ((row ?? Selected) is not { } target)
            return;
        string? password = null;
        if (target.Password && (password = await AskPasswordAsync(target)) is null)
            return;
        if (await _main.JoinServerAsync(target.Address, target.Name, password))
            _main.Notify($"Joining {target.Name}.");
    }

    private async Task<string?> AskPasswordAsync(ServerRow row)
    {
        var password = await _main.PromptAsync("Server password", $"{row.Name} needs a password.", confirm: "Join", secret: true);
        return string.IsNullOrEmpty(password) ? null : password;
    }

    [RelayCommand]
    private void ToggleFavorite(ServerRow? row)
    {
        if ((row ?? Selected) is not { } target)
            return;
        if (target.Favorite)
            _services.Servers.RemoveFavorite(target.Address);
        else
            _services.Servers.AddFavorite(target.Address, target.Name);
        if (Tab == ServerTab.Favorites)
            Refilter();
    }

    [RelayCommand]
    private async Task CopyAddressAsync()
    {
        if (Selected is { } row)
        {
            await Pickers.CopyAsync(row.AddressText);
            _main.Notify($"Copied {row.AddressText}.");
        }
    }

    [RelayCommand]
    private async Task AddServerAsync()
    {
        var text = await _main.PromptAsync("Add a server", "Its address, like 203.0.113.7:27015, or a host name like tf2.example.com:27015.", confirm: "Add");
        if (string.IsNullOrWhiteSpace(text))
            return;
        var address = await ResolveAsync(text);
        if (address is not { } target)
        {
            await _main.AlertAsync("Couldn't add that server", "That isn't a server address the launcher can use. TF2 servers have an IPv4 address and a port, like 203.0.113.7:27015.");
            return;
        }
        var row = RowFor(target, target.Port);
        var answer = await SourceQuery.InfoAsync(target.EndPoint, TimeSpan.FromSeconds(4));
        if (answer is { } a)
            row.Apply(a.Info, a.Ping);
        else if (!await _main.ConfirmAsync("No answer", $"{target} didn't answer. It may be offline or not a TF2 server. Add it anyway?", "Add anyway"))
            return;
        _services.Servers.AddFavorite(target, row.Name);
        Tab = ServerTab.Favorites;
        Selected = row;
    }

    [GeneratedRegex(@"^(?=.{1,253}$)[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)+$")]
    private static partial Regex HostName();

    internal static async Task<ServerAddress?> ResolveAsync(string text)
    {
        text = text.Trim();
        if (ServerAddress.TryParse(text, out var direct))
            return direct;
        var colon = text.LastIndexOf(':');
        var host = colon < 0 ? text : text[..colon];
        var port = ServerAddress.DefaultPort;
        if ((colon >= 0 && !ServerAddress.TryPort(text[(colon + 1)..], out port)) || !HostName().IsMatch(host))
            return null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, timeout.Token);
            return addresses.Select(ip => ServerAddress.From(ip.ToString(), port)).FirstOrDefault(a => a is not null);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            return null;
        }
    }

    [RelayCommand]
    private async Task WatchAsync()
    {
        if (Selected is not { } row || Watching)
            return;
        string? password = null;
        if (row.Password && (password = await AskPasswordAsync(row)) is null)
            return;
        if (password is not null && !ServerJoin.IsSafePassword(password))
        {
            await _main.AlertAsync("Can't use that password", ServerJoin.PasswordRules);
            return;
        }
        var cts = _watch = new CancellationTokenSource(WatchLimit);
        OnPropertyChanged(nameof(Watching));
        OnPropertyChanged(nameof(CanWatch));
        var started = DateTime.UtcNow;
        try
        {
            while (true)
            {
                WatchText = $"Waiting for a free slot on {row.Name} ({(int)(DateTime.UtcNow - started).TotalMinutes} of {(int)WatchLimit.TotalMinutes} min)…";
                if (await SourceQuery.InfoAsync(new IPEndPoint(row.Address.Ip, row.QueryPort), QueryTimeout, cts.Token) is { } a)
                {
                    row.Apply(a.Info, a.Ping);
                    if (a.Info.Players < a.Info.MaxPlayers)
                    {
                        WatchText = null;
                        if (await _main.JoinServerAsync(row.Address, row.Name, password))
                            _main.Notify($"A slot opened on {row.Name}. Joining.");
                        return;
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            if (DateTime.UtcNow - started >= WatchLimit - TimeSpan.FromSeconds(1))
                _main.Notify($"No slot opened on {row.Name} in {(int)WatchLimit.TotalMinutes} minutes.");
        }
        finally
        {
            WatchText = null;
            cts.Dispose();
            _watch = null;
            OnPropertyChanged(nameof(Watching));
            OnPropertyChanged(nameof(CanWatch));
        }
    }

    [RelayCommand]
    private void CancelWatch() => _watch?.Cancel();

    [RelayCommand]
    private void OpenTeamworkSettings() => _main.Navigate("launcher");
}
