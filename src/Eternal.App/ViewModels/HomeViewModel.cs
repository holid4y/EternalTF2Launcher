using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Eternal.App.Services;
using Eternal.Core.Feed;
using Eternal.Core.GameBanana;
using Eternal.Core.Logging;
using Eternal.Core.Platform;
using Eternal.Core.Steam;

namespace Eternal.App.ViewModels;

public sealed record NewsPostRow(string Date, string Url);

public sealed record NewsRow(NewsItem Item)
{
    public string Title => Item.Posts.Count > 1 ? $"{Item.Posts.Count} Team Fortress 2 updates" : Item.Title;
    public string Summary => Item.Summary;
    public string ImageUrl => Item.Picture;
    public bool IsGroup => Item.Posts.Count > 1;
    public string When => $"{Item.Date.LocalDateTime:d MMM yyyy} · {Item.Source}";
    public IReadOnlyList<NewsPostRow> Posts { get; } =
        [.. Item.Posts.Select(p => new NewsPostRow(p.Date.LocalDateTime.ToString("d MMM", CultureInfo.InvariantCulture), p.Url))];
}

public sealed partial class HomeViewModel : PageViewModel
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;

    public HomeViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
        Account = SteamLocator.ActiveAccount()?.PersonaName ?? "";
    }

    public ObservableCollection<NewsRow> News { get; } = [];
    public ObservableCollection<SubmissionCard> FreshMods { get; } = [];
    public MainViewModel Main => _main;

    [ObservableProperty] private bool _loadingNews;
    [ObservableProperty] private string? _newsError;
    [ObservableProperty] private bool _loadingMods;
    [ObservableProperty] private string? _modsError;
    [ObservableProperty] private string _account;

    public string Greeting => string.IsNullOrWhiteSpace(Account) ? "Not signed in to Steam" : $"Welcome back, {Account}";
    public string Tf2Folder => _services.TfPath ?? "Not found";
    public bool Tf2Found => SteamLocator.IsTf2Folder(_services.TfPath);

    protected override void OnFirstActivation() => _ = RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await Task.WhenAll(LoadNewsAsync(), LoadModsAsync());
    }

    private async Task LoadNewsAsync()
    {
        LoadingNews = true;
        NewsError = null;
        try
        {
            var news = await FeedService.SteamNewsAsync(8);
            News.Clear();
            foreach (var item in news)
                News.Add(new NewsRow(item));
        }
        catch (Exception ex)
        {
            Log.Info($"News unavailable: {ex.Message}");
            NewsError = "Steam news couldn't be loaded. Check your connection and refresh.";
        }
        finally
        {
            LoadingNews = false;
        }
    }

    private async Task LoadModsAsync()
    {
        LoadingMods = true;
        ModsError = null;
        try
        {
            var page = await GameBananaClient.Shared.BrowseAsync(new BrowseQuery(Section.Mods, Sort: BrowseSort.RecentlyUpdated));
            var installed = _main.Mods.InstalledSources();
            FreshMods.Clear();
            foreach (var submission in page.Items.Take(8))
                FreshMods.Add(new SubmissionCard(submission, GameBananaViewModel.Installed(installed, submission)));
        }
        catch (GameBananaException ex)
        {
            Log.Info($"GameBanana feed unavailable: {ex.Message}");
            ModsError = "GameBanana couldn't be reached right now.";
        }
        finally
        {
            LoadingMods = false;
        }
    }

    [RelayCommand]
    private static void OpenNews(NewsRow? row) => Shell.OpenUrl(row?.Item.Url);

    [RelayCommand]
    private static void OpenDiscord() => Shell.OpenUrl(MainViewModel.DiscordInvite);

    [RelayCommand]
    private static void OpenPost(NewsPostRow? post) => Shell.OpenUrl(post?.Url);

    [RelayCommand]
    private void OpenMod(SubmissionCard? card)
    {
        if (card is null)
            return;
        _main.Navigate("mods");
        _main.Mods.Browse(card.Submission);
    }

    [RelayCommand]
    private void BrowseAll()
    {
        _main.Navigate("mods");
        _main.Mods.Browsing = true;
    }

    [RelayCommand]
    private void Go(string tab) => _main.Navigate(tab);
}
