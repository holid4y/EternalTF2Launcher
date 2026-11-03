using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Eternal.Core.Servers;

namespace Eternal.App.ViewModels;

public enum ServerTab { Internet, Favorites, Recent }

public enum ServerSort { Name, Players, Map, Ping, LastPlayed }

public sealed partial class ServerRow(ServerAddress address, int queryPort) : ObservableObject
{
    public ServerAddress Address { get; } = address;
    public int QueryPort { get; set; } = queryPort;
    public string AddressText => Address.ToString();

    [ObservableProperty] private string _name = address.ToString();
    [ObservableProperty] private string _map = "";
    [ObservableProperty] private int _players;
    [ObservableProperty] private int _maxPlayers;
    [ObservableProperty] private int _bots;
    [ObservableProperty] private int _ping = -1;
    [ObservableProperty] private bool _password;
    [ObservableProperty] private bool _vac;
    [ObservableProperty] private IReadOnlyList<string> _tags = [];
    [ObservableProperty] private string? _country;
    [ObservableProperty] private bool _fake;
    [ObservableProperty] private bool _answered;
    [ObservableProperty] private bool _valve;
    [ObservableProperty] private bool _favorite;
    [ObservableProperty] private DateTimeOffset? _lastPlayed;
    [ObservableProperty] private int _relayFloor;

    public bool Relayed => RelayFloor > 0;

    public int EffectivePing => Relayed ? RelayFloor : Ping;

    public bool Listed { get; set; }
    public bool Queried { get; set; }

    public string MapName => WorkshopMaps.DisplayName(Map);
    public string Mode => GameModes.Of(Map);
    public string ModeText => Mode == "Other" ? "" : Mode;
    public ServerRegion Region => Regions.OfCountry(Country);
    public bool Full => MaxPlayers > 0 && Players >= MaxPlayers;
    public string PlayersText => MaxPlayers > 0 ? $"{Players}/{MaxPlayers}" : Answered ? "0" : "";
    public string BotsText => Bots > 0 ? $"{Bots} bot{(Bots == 1 ? "" : "s")}" : "";
    public string PingText => Relayed ? $"~{RelayFloor.ToString(CultureInfo.InvariantCulture)}" : Ping >= 0 ? Ping.ToString(CultureInfo.InvariantCulture) : Queried ? "—" : "";
    public string? PingTip => Relayed
        ? $"It answers through a relay near you in {Ping} ms, but the server itself is in {Regions.Name(Region)}, so expect about {RelayFloor} ms or more."
        : null;
    public bool PingGood => EffectivePing is >= 0 and < 70;
    public bool PingBad => EffectivePing >= 150;
    public string LastPlayedText => LastPlayed is { } at ? MainViewModel.Ago(at) : "";
    public string TagText => string.Join(", ", Tags);

    partial void OnMapChanged(string value)
    {
        OnPropertyChanged(nameof(MapName));
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(ModeText));
    }

    partial void OnPlayersChanged(int value) => Counts();
    partial void OnMaxPlayersChanged(int value) => Counts();
    partial void OnBotsChanged(int value) => OnPropertyChanged(nameof(BotsText));
    partial void OnAnsweredChanged(bool value) => Counts();
    partial void OnPingChanged(int value) => PingChanged();
    partial void OnRelayFloorChanged(int value)
    {
        OnPropertyChanged(nameof(Relayed));
        PingChanged();
    }
    partial void OnTagsChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(TagText));
    partial void OnCountryChanged(string? value) => OnPropertyChanged(nameof(Region));
    partial void OnLastPlayedChanged(DateTimeOffset? value) => OnPropertyChanged(nameof(LastPlayedText));

    private void Counts()
    {
        OnPropertyChanged(nameof(PlayersText));
        OnPropertyChanged(nameof(Full));
    }

    private void PingChanged()
    {
        OnPropertyChanged(nameof(PingText));
        OnPropertyChanged(nameof(PingTip));
        OnPropertyChanged(nameof(PingGood));
        OnPropertyChanged(nameof(PingBad));
        OnPropertyChanged(nameof(EffectivePing));
    }

    internal void Apply(ListedServer listed)
    {
        Listed = true;
        QueryPort = listed.QueryPort;
        Country ??= listed.Country;
        if (Answered)
            return;
        if (listed.Name.Length > 0)
            Name = listed.Name;
        Map = listed.Map;
        Players = listed.Players;
        MaxPlayers = listed.MaxPlayers;
        if (listed.Password is { } password)
            Password = password;
    }

    internal void Apply(ServerInfo info, int ping)
    {
        Answered = true;
        Queried = true;
        Name = info.Name.Length > 0 ? info.Name : AddressText;
        Map = info.Map;
        Players = info.Players;
        MaxPlayers = info.MaxPlayers;
        Bots = info.Bots;
        Password = info.Password;
        Vac = info.Vac;
        Tags = info.Tags;
        Ping = ping;
        Valve = ServerChecks.IsValve(info.Name, info.Tags);
        PingChanged();
    }

    internal void NoAnswer()
    {
        Queried = true;
        PingChanged();
    }
}

public sealed record RegionChoice(string Id, string Label)
{
    public override string ToString() => Label;
}

public sealed record PlayerRow(string Name, string Score, string Time);

public sealed record RuleRow(string Name, string Value);
