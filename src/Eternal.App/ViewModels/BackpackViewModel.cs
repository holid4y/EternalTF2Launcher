using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Eternal.App.Services;
using Eternal.Core;
using Eternal.Core.Inventory;
using Eternal.Core.Logging;
using Eternal.Core.Platform;
using Eternal.Core.Steam;

namespace Eternal.App.ViewModels;

public sealed record DetailLine(string Text, IBrush Brush);

public sealed partial class ItemViewModel(InventoryItem item) : ObservableObject
{
    public InventoryItem Item { get; } = item;
    public string Name => Item.Name;
    public string Type => Item.Type;
    public string? ImageUrl => Item.ImageUrl;
    public IBrush NameBrush { get; } = Readable(item.NameColor);
    public IBrush QualityBrush { get; } = Brushes(item.QualityColor);
    public string Quality => Item.Quality;
    public bool Tradable => Item.Tradable;
    public string? Amount => Item.Amount > 1 ? $"×{Item.Amount}" : null;

    public IReadOnlyList<DetailLine> Lines => Item.Lines
        .Select(l => new DetailLine(l.Text, l.Color is null ? Brushes("#B9AA90") : Brushes(l.Color)))
        .ToArray();

    public string Flags => string.Join(" · ", new[]
    {
        Item.Tradable ? null : "Not tradable",
        Item.Craftable ? null : "Not craftable",
        Item.Marketable ? "On the Steam Market" : null,
        Item.Wear,
    }.Where(s => s is not null));

    [ObservableProperty] private string _price = "";
    [ObservableProperty] private double _valueRef;
    [ObservableProperty] private string _buyPrice = "";
    [ObservableProperty] private string _marketPrice = "";
    [ObservableProperty] private bool _checkingMarket;

    public bool HasPrice => ValueRef > 0;

    partial void OnValueRefChanged(double value) => OnPropertyChanged(nameof(HasPrice));

    private static IBrush Brushes(string hex) => Color.TryParse(hex, out var c) ? new SolidColorBrush(c) : new SolidColorBrush(Colors.Gray);

    /// <summary>
    /// Steam serves dimmed quality colours (Unique is #7D6D00); on a dark
    /// ground names need the in-game brightness to stay readable.
    /// </summary>
    private static IBrush Readable(string hex)
    {
        if (string.Equals(hex, "#7D6D00", StringComparison.OrdinalIgnoreCase))
            return new SolidColorBrush(Color.Parse("#FFD700"));
        if (!Color.TryParse(hex, out var c))
            return new SolidColorBrush(Colors.Gray);
        var luminance = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
        if (luminance >= 0.4)
            return new SolidColorBrush(c);
        byte Lift(byte v) => (byte)(v + (255 - v) * 0.35);
        return new SolidColorBrush(Color.FromRgb(Lift(c.R), Lift(c.G), Lift(c.B)));
    }
}

public sealed partial class BackpackViewModel : PageViewModel
{
    private readonly AppServices _services;
    private readonly IDialogs _dialogs;
    private static readonly int[] RetrySeconds = [30, 60, 120];
    private List<ItemViewModel> _all = [];
    private string? _itemsOwner;
    private CancellationTokenSource? _pricing;
    private DispatcherTimer? _retry;
    private int _retryAttempt;
    private int _retryLeft;

    public BackpackViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _dialogs = main;
    }

    public string[] Sorts { get; } = ["Most valuable", "Newest", "Name", "Quality"];
    public ObservableCollection<string> Qualities { get; } = ["All qualities"];
    public ObservableCollection<string> Classes { get; } = ["All classes"];
    public string[] Currencies { get; } = [.. PriceService.SteamCurrencies.Keys];

    [ObservableProperty] private IReadOnlyList<ItemViewModel> _visible = [];
    [ObservableProperty] private ItemViewModel? _selected;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _sort = "Most valuable";
    [ObservableProperty] private string _quality = "All qualities";
    [ObservableProperty] private string _class = "All classes";
    [ObservableProperty] private bool _tradableOnly;
    [ObservableProperty] private bool _loading;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private string _owner = "";
    [ObservableProperty] private string _total = "—";
    [ObservableProperty] private string _coverage = "";
    [ObservableProperty] private string _keyPrice = "";
    [ObservableProperty] private bool _pricingRunning;
    [ObservableProperty] private string? _steamId;

    public bool HasItems => _all.Count > 0;
    public bool HasSelection => Selected is not null;
    public bool NoAccount => SteamId is null && !Loading;

    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnSortChanged(string value) => ApplyFilter();
    partial void OnQualityChanged(string value) => ApplyFilter();
    partial void OnClassChanged(string value) => ApplyFilter();
    partial void OnTradableOnlyChanged(bool value) => ApplyFilter();
    partial void OnLoadingChanged(bool value) => OnPropertyChanged(nameof(NoAccount));
    partial void OnSteamIdChanged(string? value) => OnPropertyChanged(nameof(NoAccount));

    partial void OnSelectedChanged(ItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        if (value is not null)
            _ = LoadMarketPriceAsync(value);
    }

    protected override void OnFirstActivation() => _ = LoadAsync(force: false);

    [RelayCommand]
    private Task RefreshAsync()
    {
        _retry?.Stop();
        _retryAttempt = 0;
        return LoadAsync(force: true);
    }

    private async Task LoadAsync(bool force)
    {
        // Re-detect each time: the player may have switched Steam accounts.
        var account = SteamLocator.ActiveAccount();
        SteamId = _services.Launcher.Settings.SteamIdOverride is { Length: > 0 } custom ? custom : account?.SteamId64;
        Owner = _services.Launcher.Settings.SteamIdOverride is { Length: > 0 } ? $"SteamID {SteamId}" : account?.PersonaName ?? "";
        // The items on screen must belong to the account named above them, even if loading the new one fails.
        if (SteamId != _itemsOwner)
            ClearItems();
        if (SteamId is null)
            return;

        Loading = true;
        Error = null;
        Notice = null;
        try
        {
            var snapshot = await _services.Inventory.LoadAsync(SteamId, force);
            _retry?.Stop();
            _retryAttempt = 0;
            Notice = snapshot.Notice;
            _itemsOwner = snapshot.SteamId;
            _all = snapshot.Items.Select(i => new ItemViewModel(i)).ToList();
            RebuildFacets();
            ApplyPrices();
            ApplyFilter();
            OnPropertyChanged(nameof(HasItems));
            _ = PriceAsync();
        }
        catch (InventoryRateLimitedException)
        {
            ScheduleRetry();
        }
        catch (InventoryException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Error = "Steam couldn't be reached. Check your connection and try again.";
            Log.Info($"Backpack load failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            Error = $"Steam sent a backpack the launcher couldn't read: {ex.Message}";
            Log.Error("Backpack load failed", ex);
        }
        finally
        {
            Loading = false;
        }
    }

    private void ClearItems()
    {
        _pricing?.Cancel();
        _itemsOwner = null;
        _all = [];
        Selected = null;
        RebuildFacets();
        ApplyPrices();
        ApplyFilter();
        OnPropertyChanged(nameof(HasItems));
    }

    /// <summary>Steam's limit lifts after a minute or two; try again on our own instead of making the player guess.</summary>
    private void ScheduleRetry()
    {
        if (_retryAttempt >= RetrySeconds.Length)
        {
            Error = "Steam is still rate limiting inventory requests. Wait a few minutes and press Refresh.";
            _retryAttempt = 0;
            return;
        }
        _retryLeft = RetrySeconds[_retryAttempt++];
        Error = null;
        Notice = $"Steam is busy right now. Trying again in {_retryLeft} s…";
        _retry?.Stop();
        _retry = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            if (--_retryLeft > 0)
            {
                Notice = $"Steam is busy right now. Trying again in {_retryLeft} s…";
                return;
            }
            _retry?.Stop();
            _ = LoadAsync(force: true);
        });
        _retry.Start();
    }

    private async Task PriceAsync()
    {
        _pricing?.Cancel();
        var cts = _pricing = new CancellationTokenSource();
        PricingRunning = true;
        try
        {
            var items = _all.Select(i => i.Item).ToList();
            var progress = new Progress<int>(_ => ApplyPrices());
            await _services.Prices.PriceAllAsync(items, progress, cts.Token);
            Dispatcher.UIThread.Post(() =>
            {
                ApplyPrices();
                if (Sort == "Most valuable")
                    ApplyFilter();
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn("Pricing failed", ex);
        }
        finally
        {
            PricingRunning = false;
        }
    }

    private void ApplyPrices()
    {
        var keyRef = _services.Prices.KeyRef;
        double total = 0;
        var priced = 0;
        var tradable = 0;
        foreach (var vm in _all)
        {
            if (vm.Tradable)
                tradable++;
            var price = _services.Prices.Cached(vm.Item);
            if (price is null)
            {
                vm.ValueRef = 0;
                vm.Price = vm.Tradable ? "" : "Untradable";
                continue;
            }
            var value = price.ValueInRef(keyRef);
            vm.ValueRef = value;
            vm.Price = PriceService.Format(value, keyRef);
            vm.BuyPrice = PriceService.Format(price.BuyKeys * keyRef + price.BuyMetal, keyRef);
            if (vm.Tradable)
            {
                total += value * vm.Item.Amount;
                priced++;
            }
        }
        Total = priced == 0 ? "—" : PriceService.Format(total, keyRef);
        Coverage = tradable == 0 ? "" : $"{priced} of {tradable} tradable items priced · {PriceService.Source}";
        KeyPrice = keyRef > 0 ? $"Key ≈ {keyRef.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} ref" : "";
    }

    private void RebuildFacets()
    {
        Qualities.Clear();
        Qualities.Add("All qualities");
        foreach (var q in _all.Select(i => i.Quality).Distinct().Order())
            Qualities.Add(q);
        Classes.Clear();
        Classes.Add("All classes");
        foreach (var c in _all.SelectMany(i => i.Item.Classes).Distinct().Order())
            Classes.Add(c);
        Quality = "All qualities";
        Class = "All classes";
    }

    private void ApplyFilter()
    {
        var query = Search.Trim();
        IEnumerable<ItemViewModel> items = _all.Where(i =>
            (query.Length == 0 || i.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || i.Type.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (i.Item.Effect?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            && (Quality == "All qualities" || i.Quality == Quality)
            && (Class == "All classes" || i.Item.Classes.Contains(Class))
            && (!TradableOnly || i.Tradable));

        items = Sort switch
        {
            "Newest" => items.OrderByDescending(i => i.Item.Position),
            "Name" => items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
            "Quality" => items.OrderBy(i => i.Quality).ThenByDescending(i => i.ValueRef),
            _ => items.OrderByDescending(i => i.ValueRef).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        Visible = items.ToArray();
    }

    private async Task LoadMarketPriceAsync(ItemViewModel item)
    {
        var currency = _services.Launcher.Settings.Currency;
        if (_services.Prices.CachedMarket(item.Item, currency) is { } cached)
        {
            item.MarketPrice = cached.Formatted;
            return;
        }
        if (!item.Item.Marketable)
        {
            item.MarketPrice = "";
            return;
        }
        item.CheckingMarket = true;
        try
        {
            var price = await _services.Prices.MarketPriceAsync(item.Item, currency);
            item.MarketPrice = price?.Formatted ?? "No listings";
        }
        finally
        {
            item.CheckingMarket = false;
        }
    }

    [GeneratedRegex(@"steamcommunity\.com/(?<kind>profiles|id)/(?<value>[^/?#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ProfileUrl();

    [RelayCommand]
    private async Task ChangeAccountAsync()
    {
        var input = await _dialogs.PromptAsync("Whose backpack?",
            "Paste a SteamID64 or a Steam profile link. Leave empty to use the account signed in to Steam.",
            _services.Launcher.Settings.SteamIdOverride ?? "", "Show");
        if (input is null)
            return;
        if (input.Length == 0)
        {
            _services.Launcher.Update(s => s.SteamIdOverride = null);
            await LoadAsync(force: false);
            return;
        }

        var id = await ResolveSteamIdAsync(input);
        if (id is null)
        {
            await _dialogs.AlertAsync("Account not found", "That isn't a SteamID64 or a Steam profile link.");
            return;
        }
        _services.Launcher.Update(s => s.SteamIdOverride = id);
        await LoadAsync(force: false);
    }

    private static async Task<string?> ResolveSteamIdAsync(string input)
    {
        input = input.Trim();
        if (InventoryService.IsSteamId64(input))
            return input;
        var match = ProfileUrl().Match(input);
        if (!match.Success)
            return null;
        var value = match.Groups["value"].Value;
        if (match.Groups["kind"].Value.Equals("profiles", StringComparison.OrdinalIgnoreCase))
            return InventoryService.IsSteamId64(value) ? value : null;
        try
        {
            var xml = await Http.Community.GetStringAsync($"https://steamcommunity.com/id/{Uri.EscapeDataString(value)}/?xml=1");
            var id = Regex.Match(xml, @"<steamID64>(\d{17})</steamID64>").Groups[1].Value;
            return InventoryService.IsSteamId64(id) ? id : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    [RelayCommand]
    private void Select(ItemViewModel? item) => Selected = item;

    [RelayCommand]
    private void OpenBackpackTf() => Shell.OpenUrl(SteamId is null ? null : $"https://backpack.tf/profiles/{SteamId}");

    [RelayCommand]
    private void OpenSteamInventory() => Shell.OpenUrl(SteamId is null ? null : $"https://steamcommunity.com/profiles/{SteamId}/inventory/#440");

    [RelayCommand]
    private static void OpenPrivacy() => Shell.OpenUrl("https://steamcommunity.com/my/edit/settings");

    [RelayCommand]
    private void OpenItemStats(ItemViewModel? item)
    {
        if (item is null)
            return;
        var name = item.Item.PriceNames().First();
        Shell.OpenUrl($"https://backpack.tf/stats/{Uri.EscapeDataString(item.Quality)}/{Uri.EscapeDataString(name)}/Tradable/{(item.Item.Craftable ? "Craftable" : "Non-Craftable")}");
    }

    [RelayCommand]
    private static void OpenMarket(ItemViewModel? item)
    {
        if (item is not null && item.Item.Marketable)
            Shell.OpenUrl($"https://steamcommunity.com/market/listings/440/{Uri.EscapeDataString(item.Item.MarketHashName)}");
    }
}
