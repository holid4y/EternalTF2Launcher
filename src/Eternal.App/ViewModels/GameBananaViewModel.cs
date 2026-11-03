using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Eternal.App.Services;
using Eternal.Core.GameBanana;
using Eternal.Core.Logging;

namespace Eternal.App.ViewModels;

public sealed partial class SubmissionCard(Submission submission, bool installed) : ObservableObject
{
    public Submission Submission { get; } = submission;
    public string Name => Submission.Name;
    public string Author => Submission.Author.Length > 0 ? $"by {Submission.Author}" : "";
    public string? Thumbnail => Submission.Thumbnail;
    public bool HasThumbnail => Submission.Thumbnail is not null;
    public string Category => Submission.Category;
    public string Likes => Format.Count(Submission.Likes);
    public string Views => Format.Count(Submission.Views);
    public string Updated => MainViewModel.Ago(Submission.Updated);
    public bool CasualReady => Submission.CasualReady;
    public bool Obsolete => Submission.Obsolete;
    public bool IsSound => Submission.Section == Section.Sounds;

    [ObservableProperty] private bool _installed = installed;
}

public sealed record CategoryRow(Category? Category, long? ParentId, bool Expanded)
{
    public string Name => Category?.Name ?? "Everything";
    public string Count => Category is { ItemCount: > 0 } c ? Format.Count(c.ItemCount) : "";
    public bool IsChild => ParentId is not null;
    public Thickness Indent => new(IsChild ? 14 : 0, 0, 0, 0);
}

public sealed record PageButton(int Number, bool Current)
{
    public string Label => Number > 0 ? Number.ToString(System.Globalization.CultureInfo.InvariantCulture) : "…";
    public bool IsGap => Number == 0;
}

public sealed record SortChoice(BrowseSort Sort, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class GameBananaViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly ModsViewModel _mods;
    private readonly GameBananaClient _client = GameBananaClient.Shared;
    private CancellationTokenSource? _typing;
    private readonly Dictionary<(Section, long?), IReadOnlyList<Category>> _categories = [];
    private CancellationTokenSource? _query;
    private bool _started;
    private bool _quiet;

    public GameBananaViewModel(AppServices services, ModsViewModel mods)
    {
        _services = services;
        _mods = mods;
        _sort = Sorts[0];
        View = new ViewOptions(services.Launcher, s => s.GameBanana, textHeight: 114, thumbRatio: 140.0 / 216);
        services.Mods.Changed += () => Dispatcher.UIThread.Post(RefreshInstalled);
    }

    public ViewOptions View { get; }
    public ObservableCollection<CategoryRow> Categories { get; } = [];
    public ObservableCollection<SubmissionCard> Items { get; } = [];
    public ObservableCollection<PageButton> Pages { get; } = [];

    public SortChoice[] Sorts { get; } =
    [
        new(BrowseSort.MostDownloaded, "Most downloaded"),
        new(BrowseSort.MostLiked, "Most liked"),
        new(BrowseSort.MostViewed, "Most viewed"),
        new(BrowseSort.RecentlyUpdated, "Recently updated"),
        new(BrowseSort.Newest, "Newest"),
        new(BrowseSort.Name, "Name"),
    ];

    [ObservableProperty] private Section _section = Section.Mods;
    [ObservableProperty] private CategoryRow? _selectedCategory;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private SortChoice _sort;
    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private int _pageCount = 1;
    [ObservableProperty] private int _total;
    [ObservableProperty] private bool _loading;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private SubmissionViewModel? _open;

    public bool ShowingMods => Section == Section.Mods;
    public bool HasResults => Items.Count > 0;
    public bool IsEmpty => !Loading && Error is null && Items.Count == 0;
    public bool CanGoBack => Page > 1 && !Loading;
    public bool CanGoForward => Page < PageCount && !Loading;
    public string ResultText => Total == 0 ? "" : Format.Plural(Total, Section == Section.Mods ? "mod" : "sound", Section == Section.Mods ? "mods" : "sounds");

    public string EmptyText => GameBananaClient.SearchText(Search).Length >= 2
        ? $"Nothing named like \"{GameBananaClient.SearchText(Search)}\" in {SelectedCategory?.Name ?? "Everything"}."
        : "Nothing here yet.";

    public void Activate()
    {
        if (_started)
            return;
        _started = true;
        _ = LoadCategoriesAsync();
        Reload(firstPage: true);
    }

    public void Show(Submission submission)
    {
        Activate();
        Open = new SubmissionViewModel(submission, _services, _mods, _client);
    }

    [RelayCommand]
    private void ShowMods() => Section = Section.Mods;

    [RelayCommand]
    private void ShowSounds() => Section = Section.Sounds;

    partial void OnSectionChanged(Section value)
    {
        OnPropertyChanged(nameof(ShowingMods));
        _quiet = true;
        SelectedCategory = null;
        _quiet = false;
        _ = LoadCategoriesAsync();
        Reload(firstPage: true);
    }

    partial void OnSelectedCategoryChanged(CategoryRow? value)
    {
        if (_quiet)
            return;
        _ = LoadCategoriesAsync();
        Reload(firstPage: true);
    }

    partial void OnSearchChanged(string value)
    {
        _typing?.Cancel();
        _ = SearchSoonAsync((_typing = new CancellationTokenSource()).Token);
    }

    private async Task SearchSoonAsync(CancellationToken typing)
    {
        try
        {
            await Task.Delay(450, typing);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        Reload(firstPage: true);
    }

    partial void OnSortChanged(SortChoice value) => Reload(firstPage: true);

    partial void OnPageChanged(int value)
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        if (!_quiet)
            Reload(firstPage: false);
    }

    partial void OnLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(IsEmpty));

    [RelayCommand]
    private void PreviousPage()
    {
        if (CanGoBack)
            Page--;
    }

    [RelayCommand]
    private void NextPage()
    {
        if (CanGoForward)
            Page++;
    }

    [RelayCommand]
    private void GoToPage(PageButton? button)
    {
        if (button is { Number: > 0 } && button.Number <= PageCount && !Loading)
            Page = button.Number;
    }

    [RelayCommand]
    private void Retry() => Reload(firstPage: false);

    [RelayCommand]
    private void OpenCard(SubmissionCard? card)
    {
        if (card is not null)
            Open = new SubmissionViewModel(card.Submission, _services, _mods, _client);
    }

    [RelayCommand]
    private void Back()
    {
        Open?.Close();
        Open = null;
    }

    private void Reload(bool firstPage)
    {
        if (firstPage && Page != 1)
        {
            _quiet = true;
            Page = 1;
            _quiet = false;
        }
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        _query?.Cancel();
        var query = _query = new CancellationTokenSource();
        Loading = true;
        Error = null;
        try
        {
            var page = await _client.BrowseAsync(
                new BrowseQuery(Section, SelectedCategory?.Category?.Id, Search, Sort.Sort, Page), query.Token);
            if (query.IsCancellationRequested)
                return;
            var installed = _mods.InstalledSources();
            Items.Clear();
            foreach (var submission in page.Items)
                Items.Add(new SubmissionCard(submission, Installed(installed, submission)));
            Total = page.Total;
            PageCount = page.PageCount;
            _quiet = true;
            Page = page.Page;
            _quiet = false;
            BuildPages();
        }
        catch (OperationCanceledException) when (query.IsCancellationRequested)
        {
        }
        catch (GameBananaException ex)
        {
            if (query.IsCancellationRequested)
                return;
            Log.Info($"GameBanana browse failed: {ex.Message}");
            Items.Clear();
            Total = 0;
            Error = ex.Message;
        }
        finally
        {
            if (_query == query)
                Loading = false;
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(EmptyText));
            OnPropertyChanged(nameof(ResultText));
        }
    }

    private void BuildPages()
    {
        Pages.Clear();
        var shown = new SortedSet<int> { 1, PageCount };
        for (var n = Page - 1; n <= Page + 1; n++)
        {
            if (n >= 1 && n <= PageCount)
                shown.Add(n);
        }
        var last = 0;
        foreach (var n in shown)
        {
            if (n - last > 1)
                Pages.Add(new PageButton(0, false));
            Pages.Add(new PageButton(n, n == Page));
            last = n;
        }
    }

    private async Task LoadCategoriesAsync()
    {
        var section = Section;
        var selected = SelectedCategory;
        try
        {
            var roots = await CategoriesAsync(section, null);
            var openRoot = selected?.ParentId ?? (selected?.Category is { ChildCount: > 0 } c ? c.Id : null);
            var children = openRoot is { } parent ? await CategoriesAsync(section, parent) : [];
            if (section != Section)
                return;

            var rows = new List<CategoryRow> { new(null, null, false) };
            foreach (var root in roots.Where(r => r.ItemCount > 0))
            {
                rows.Add(new CategoryRow(root, null, root.Id == openRoot));
                if (root.Id == openRoot)
                    rows.AddRange(children.Where(ch => ch.ItemCount > 0).Select(ch => new CategoryRow(ch, root.Id, false)));
            }
            _quiet = true;
            Categories.Clear();
            foreach (var row in rows)
                Categories.Add(row);
            SelectedCategory = rows.FirstOrDefault(r => r.Category?.Id == selected?.Category?.Id) ?? rows[0];
            _quiet = false;
        }
        catch (GameBananaException ex)
        {
            Log.Info($"GameBanana categories unavailable: {ex.Message}");
            if (Categories.Count == 0)
                Categories.Add(new CategoryRow(null, null, false));
        }
    }

    private async Task<IReadOnlyList<Category>> CategoriesAsync(Section section, long? parent)
    {
        if (_categories.TryGetValue((section, parent), out var cached))
            return cached;
        var loaded = await _client.CategoriesAsync(section, parent);
        _categories[(section, parent)] = loaded;
        return loaded;
    }

    private void RefreshInstalled()
    {
        var installed = _mods.InstalledSources();
        foreach (var card in Items)
            card.Installed = Installed(installed, card.Submission);
        Open?.RefreshInstalled(installed);
    }

    internal static bool Installed(IReadOnlySet<string> sources, Submission submission) =>
        sources.Any(s => s.StartsWith(submission.SourcePrefix, StringComparison.Ordinal));
}
