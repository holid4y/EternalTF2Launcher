using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using LauncherTF2.Core;
using LauncherTF2.Models.Inventory;
using LauncherTF2.Services;

namespace LauncherTF2.ViewModels.Inventory;

public class BackpackViewModel : ViewModelBase, IDisposable
{
    // Ordered Common → Rare so filter chips appear in grade order, not alphabetically.
    private static readonly string[] Rarities7 =
    {
        "Civilian Grade", "Freelance Grade", "Mercenary Grade",
        "Commando Grade", "Assassin Grade", "Elite Grade", "Self-Made"
    };

    // Prefixes for contains-based ranking so "Civilian Grade Rifle" matches at rank 0.
    private static readonly string[] RarityKeywords =
    {
        "Civilian", "Freelance", "Mercenary", "Commando", "Assassin", "Elite", "Self-Made"
    };

    private readonly SteamDetectionService _steamDetection = ServiceLocator.SteamDetection;
    private readonly SteamInventoryService _steamInventory = ServiceLocator.SteamInventory;
    private readonly InventoryPricingService _pricing = ServiceLocator.Pricing;
    private readonly InventoryImageCache _imageCache = ServiceLocator.ImageCache;

    private readonly List<BackpackGridItem> _allItems = new();
    private readonly Dictionary<string, InventoryPricingService.PriceSnapshot> _sessionPriceCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _searchDebounce;
    private CancellationTokenSource? _refreshCts;

    private string _steamId64 = string.Empty;
    private string _statusMessage = "Preparing inventory...";
    private string _cookieStatus = string.Empty;
    private bool _isLoading;
    private bool _isFilterPanelOpen = true;
    private bool _isDetailPanelOpen;
    private BackpackGridItem? _selectedItem;

    public BackpackViewModel()
    {
        Items = new ObservableCollection<BackpackGridItem>();

        Filters = new InventoryFilterState();
        Filters.FilterChanged += OnFilterChanged;
        Filters.SearchTextChanged += OnSearchTextChanged;

        _searchDebounce = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _searchDebounce.Tick += OnDebounceTick;

        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(forceRefresh: true), _ => !IsLoading);
        SelectItemCommand = new AsyncRelayCommand(o => SelectAsync(o as BackpackGridItem));
        CloseDetailCommand = new RelayCommand(_ => CloseDetail());
        ToggleFiltersCommand = new RelayCommand(_ => IsFilterPanelOpen = !IsFilterPanelOpen);
        ClearFiltersCommand = new RelayCommand(_ => Filters.Clear());
        CycleSortCommand = new RelayCommand(_ => CycleSort());
        OpenStoreLinkCommand = new RelayCommand(o => OpenStoreLink(o?.ToString()));

        TryDetectSteamId();
    }

    #region Public properties bound to the View

    // Maintained in-place via incremental Add/Remove/Move to avoid rebuilding the entire grid.
    public ObservableCollection<BackpackGridItem> Items { get; }

    public InventoryFilterState Filters { get; }

    public ObservableCollection<StorePriceRow> SelectedItemPrices { get; } = new();

    public string SteamId64
    {
        get => _steamId64;
        set => SetProperty(ref _steamId64, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string CookieStatus
    {
        get => _cookieStatus;
        set => SetProperty(ref _cookieStatus, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            if (SetProperty(ref _isLoading, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasItems => _allItems.Count > 0;

    public bool IsFilterPanelOpen
    {
        get => _isFilterPanelOpen;
        set => SetProperty(ref _isFilterPanelOpen, value);
    }

    public bool IsDetailPanelOpen
    {
        get => _isDetailPanelOpen;
        set => SetProperty(ref _isDetailPanelOpen, value);
    }

    public BackpackGridItem? SelectedItem
    {
        get => _selectedItem;
        private set
        {
            if (SetProperty(ref _selectedItem, value))
                OnPropertyChanged(nameof(HasSelectedItem));
        }
    }

    public bool HasSelectedItem => _selectedItem != null;

    public ICommand RefreshCommand { get; }
    public ICommand SelectItemCommand { get; }
    public ICommand CloseDetailCommand { get; }
    public ICommand ToggleFiltersCommand { get; }
    public ICommand ClearFiltersCommand { get; }
    public ICommand CycleSortCommand { get; }
    public ICommand OpenStoreLinkCommand { get; }

    #endregion

    #region Activation / refresh

    public Task OnActivatedAsync()
    {
        if (!HasItems && !IsLoading)
            return RefreshAsync();
        return Task.CompletedTask;
    }

    private void TryDetectSteamId()
    {
        SteamId64 = _steamDetection.GetActiveSteamId() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(SteamId64))
        {
            StatusMessage = "No active Steam user detected — open Steam and try again.";
            Logger.LogWarning("[Inventory] Active Steam user not detected from registry.");
        }
        else
        {
            StatusMessage = "Ready — backpack will load on activation.";
        }
    }

    public async Task RefreshAsync(bool forceRefresh = false)
    {
        if (IsLoading) return;

        if (string.IsNullOrWhiteSpace(SteamId64))
        {
            TryDetectSteamId();
            if (string.IsNullOrWhiteSpace(SteamId64)) return;
        }

        _refreshCts?.Cancel();
        _refreshCts = new CancellationTokenSource();
        var token = _refreshCts.Token;

        IsLoading = true;
        _allItems.Clear();
        Items.Clear();
        SelectedItemPrices.Clear();
        SelectedItem = null;
        IsDetailPanelOpen = false;
        StatusMessage = "Loading backpack from Steam Community...";

        try
        {
            var result = await _steamInventory.GetBackpackItemsAsync(SteamId64.Trim(), token, forceRefresh);
            token.ThrowIfCancellationRequested();

            foreach (var raw in result.Items)
            {
                var classes = ItemMetadataExtractor.ExtractClasses(raw.EquippedOn);
                var item = new BackpackGridItem
                {
                    ItemKey = $"{raw.Name}|{raw.QualityName}|{(raw.Tradable ? "T" : "NT")}",
                    Name = raw.Name,
                    ImageUrl = raw.ImageUrl,
                    BorderColorHex = raw.BorderColorHex,
                    IsEquipped = raw.IsEquipped,
                    QualityName = raw.QualityName,
                    TypeRaw = raw.Type,
                    ItemType = ItemMetadataExtractor.ExtractItemType(raw.Type, raw.Name),
                    Slot = ItemMetadataExtractor.ExtractSlot(raw.Type, raw.Name),
                    Classes = classes,
                    Tradable = raw.Tradable,
                    Rarity = raw.Rarity,
                    UnusualEffect = raw.UnusualEffect,
                    Paint = raw.Paint,
                    KillstreakTier = raw.KillstreakTier,
                    KillstreakSheen = raw.KillstreakSheen,
                    Killstreaker = raw.Killstreaker,
                    Spell = raw.Spell,
                    CraftNumber = raw.CraftNumber,
                    EquippedOn = raw.EquippedOn,
                    Description = raw.Description
                };

                _allItems.Add(item);
            }

            RebuildFilterChips();
            RepopulateView();

            CookieStatus = result.CookieStatus;
            StatusMessage = result.RateLimitedFallback
                ? "Rate limited — showing cached inventory."
                : $"Loaded {_allItems.Count} item{(_allItems.Count == 1 ? "" : "s")}.";

            OnPropertyChanged(nameof(HasItems));

            _ = HydrateImagesAsync(token);
        }
        catch (OperationCanceledException)
        {
            Logger.LogInfo("[Inventory] Refresh cancelled");
        }
        catch (Exception ex)
        {
            Logger.LogError("[Inventory] Refresh failed", ex);
            if (ex.Message.Contains("logged in", StringComparison.OrdinalIgnoreCase))
                StatusMessage = "Steam must be running and logged in.";
            else if (ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
                StatusMessage = "Rate limited — try again in a few minutes.";
            else
                StatusMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task HydrateImagesAsync(CancellationToken token)
    {
        try
        {
            const int batchSize = 12;
            for (int i = 0; i < _allItems.Count && !token.IsCancellationRequested; i += batchSize)
            {
                var batch = _allItems.Skip(i).Take(batchSize).ToArray();
                await Task.WhenAll(batch.Select(async item =>
                {
                    try
                    {
                        item.Image = await _imageCache.GetAsync(item.ImageUrl, token);
                    }
                    catch { /* per-item failure is non-fatal */ }
                }));
            }
        }
        catch (OperationCanceledException) { }
    }

    #endregion

    #region Selection / detail panel

    private async Task SelectAsync(BackpackGridItem? item)
    {
        if (item == null) return;

        if (ReferenceEquals(SelectedItem, item))
        {
            CloseDetail();
            return;
        }

        SelectedItem = item;
        IsDetailPanelOpen = true;

        await LoadStorePricesAsync(item);
    }

    private void CloseDetail()
    {
        IsDetailPanelOpen = false;
        SelectedItem = null;
        SelectedItemPrices.Clear();
    }

    private async Task LoadStorePricesAsync(BackpackGridItem item)
    {
        SelectedItemPrices.Clear();

        foreach (var name in InventoryPricingService.StoreOrder)
        {
            SelectedItemPrices.Add(new StorePriceRow
            {
                StoreName = name,
                Status = "Loading...",
                IsLoading = true,
                ListingUrl = InventoryPricingService.GetStoreSearchUrl(name, item.Name)
            });
        }

        try
        {
            var snapshot = _sessionPriceCache.TryGetValue(item.ItemKey, out var cached)
                ? cached
                : await _pricing.GetPriceSnapshotAsync(item.Name, item.QualityName, item.Tradable);

            _sessionPriceCache[item.ItemKey] = snapshot;
            item.PricePureLabel = $"≈ {snapshot.PureSummary}";

            foreach (var row in SelectedItemPrices)
            {
                if (snapshot.StoreResults.TryGetValue(row.StoreName, out var result))
                {
                    row.IsLoading = false;
                    row.Status = result.Status;
                    row.PriceKeys = result.PriceKeys;
                    row.PriceRef = result.PriceRef;
                    row.ListingUrl = !string.IsNullOrWhiteSpace(result.ListingUrl) ? result.ListingUrl : result.FallbackUrl;
                }
                else
                {
                    row.IsLoading = false;
                    row.Status = "Unavailable";
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[Inventory] Pricing failed for {item.Name}", ex);
            foreach (var row in SelectedItemPrices)
            {
                row.IsLoading = false;
                if (row.Status == "Loading...") row.Status = "Unavailable";
            }
        }
    }

    private static void OpenStoreLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.IsWellFormedUriString(url, UriKind.Absolute))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[Inventory] Failed to open store link: {url}", ex);
        }
    }

    #endregion

    #region Filtering / sorting

    private void OnFilterChanged(object? sender, EventArgs e)
    {
        _searchDebounce.Stop();
        RepopulateView();
    }

    private void OnSearchTextChanged(object? sender, EventArgs e)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _searchDebounce.Stop();
        RepopulateView();
    }

    private void RepopulateView()
    {
        var target = ApplySort(_allItems.Where(MatchesFilters)).ToList();
        SynchronizeItems(target);
    }

    private void SynchronizeItems(IList<BackpackGridItem> target)
    {
        var targetSet = new HashSet<BackpackGridItem>(target, ReferenceEqualityComparer.Instance);
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (!targetSet.Contains(Items[i]))
                Items.RemoveAt(i);
        }

        for (int i = 0; i < target.Count; i++)
        {
            if (i >= Items.Count)
            {
                Items.Add(target[i]);
                continue;
            }

            if (ReferenceEquals(Items[i], target[i])) continue;

            int existing = -1;
            for (int j = i + 1; j < Items.Count; j++)
            {
                if (ReferenceEquals(Items[j], target[i]))
                {
                    existing = j;
                    break;
                }
            }

            if (existing >= 0)
                Items.Move(existing, i);
            else
                Items.Insert(i, target[i]);
        }
    }

    private IEnumerable<BackpackGridItem> ApplySort(IEnumerable<BackpackGridItem> source) => Filters.SortMode switch
    {
        SortMode.NameDesc => source.OrderByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase),
        SortMode.RarityAsc => source.OrderBy(i => RarityRank(i.Rarity)).ThenBy(i => i.Name),
        SortMode.RarityDesc => source.OrderByDescending(i => RarityRank(i.Rarity)).ThenBy(i => i.Name),
        _ => source.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
    };

    private static int RarityRank(string? rarity)
    {
        if (string.IsNullOrWhiteSpace(rarity)) return -1;
        for (int i = 0; i < RarityKeywords.Length; i++)
        {
            if (rarity.Contains(RarityKeywords[i], StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return 99;
    }

    private bool MatchesFilters(BackpackGridItem item)
    {
        if (Filters.IsEmpty) return true;

        // ── Search text ──────────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(Filters.SearchText))
        {
            var q = Filters.SearchText;
            bool nameHit    = item.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
            bool qualityHit = item.QualityName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
            bool typeHit    = item.ItemType.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                           || item.TypeRaw.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
            if (!nameHit && !qualityHit && !typeHit)
                return false;
        }

        // ── Class chip ───────────────────────────────────────────────────────
        // Multi-class items match any single-class chip; "Multi-Class" chip shows only all-class items.
        if (!ChipMatch(Filters.Classes, c =>
        {
            if (item.Classes.Any(cls => string.Equals(cls, c, StringComparison.OrdinalIgnoreCase)))
                return true;
            if (!string.Equals(c, "Multi-Class", StringComparison.OrdinalIgnoreCase)
                && item.Classes.Contains("Multi-Class", StringComparer.OrdinalIgnoreCase))
                return true;
            return false;
        }))
            return false;

        // ── Quality, Item type, Rarity ───────────────────────────────────────
        if (!ChipMatch(Filters.Qualities, c => string.Equals(item.QualityName, c, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (!ChipMatch(Filters.ItemTypes, c => string.Equals(item.ItemType, c, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (!ChipMatch(Filters.Rarities, c => string.Equals(item.Rarity, c, StringComparison.OrdinalIgnoreCase)))
            return false;

        return true;
    }

    private static bool ChipMatch(IEnumerable<FilterChip> chips, Func<string, bool> match)
    {
        var selected = chips.Where(c => c.IsSelected).Select(c => c.Value).ToList();
        return selected.Count == 0 || selected.Any(match);
    }

    private void RebuildFilterChips()
    {
        BuildChips(Filters.Classes, ItemMetadataExtractor.AllClasses, _allItems.SelectMany(i => i.Classes));
        BuildChips(Filters.ItemTypes,
            new[] { "Primary", "Secondary", "Melee", "PDA", "Cosmetic", "Taunt", "Tool", "Action", "Misc", "Crate", "Package", "Ticket", "Consumable", "Other" },
            _allItems.Select(i => i.ItemType));
        BuildChips(Filters.Qualities, Array.Empty<string>(), _allItems.Select(i => i.QualityName));
        BuildChips(Filters.Rarities, Rarities7, _allItems.Select(i => i.Rarity));
    }

    private void BuildChips(ObservableCollection<FilterChip> target, IReadOnlyList<string> preferred, IEnumerable<string?> dynamicValues)
    {
        var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in dynamicValues)
            if (!string.IsNullOrWhiteSpace(v)) actual.Add(v);

        var ordered = preferred
            .Where(p => actual.Contains(p))
            .Concat(actual.Where(v => !preferred.Contains(v, StringComparer.OrdinalIgnoreCase)).OrderBy(v => v))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var oldChip in target)
            oldChip.SelectionChanged -= OnChipSelectionChanged;

        target.Clear();
        foreach (var v in ordered)
        {
            var chip = new FilterChip { Label = v, Value = v };
            chip.SelectionChanged += OnChipSelectionChanged;
            target.Add(chip);
        }
    }

    private void OnChipSelectionChanged(object? sender, EventArgs e) => Filters.NotifyChipChanged();

    private void CycleSort()
    {
        Filters.SortMode = Filters.SortMode switch
        {
            SortMode.NameAsc => SortMode.NameDesc,
            SortMode.NameDesc => SortMode.RarityDesc,
            SortMode.RarityDesc => SortMode.RarityAsc,
            _ => SortMode.NameAsc
        };
    }

    #endregion

    public void Dispose()
    {
        _searchDebounce.Stop();
        _searchDebounce.Tick -= OnDebounceTick;
        Filters.FilterChanged -= OnFilterChanged;
        Filters.SearchTextChanged -= OnSearchTextChanged;
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
    }
}

public class StorePriceRow : ViewModelBase
{
    private string _status = "Loading...";
    private string _priceKeys = string.Empty;
    private string _priceRef = string.Empty;
    private bool _isLoading;
    private string _listingUrl = string.Empty;

    public string StoreName { get; init; } = string.Empty;

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public string PriceKeys
    {
        get => _priceKeys;
        set => SetProperty(ref _priceKeys, value);
    }

    public string PriceRef
    {
        get => _priceRef;
        set => SetProperty(ref _priceRef, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public string ListingUrl
    {
        get => _listingUrl;
        set
        {
            if (SetProperty(ref _listingUrl, value))
                OnPropertyChanged(nameof(HasListingUrl));
        }
    }

    public bool HasListingUrl => !string.IsNullOrWhiteSpace(_listingUrl);
}
