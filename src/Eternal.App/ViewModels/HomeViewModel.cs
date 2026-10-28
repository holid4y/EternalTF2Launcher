using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Eternal.App.Services;
using Eternal.Core.Feed;
using Eternal.Core.Logging;
using Eternal.Core.Platform;
using Eternal.Core.Steam;

namespace Eternal.App.ViewModels;

public sealed record NewsRow(NewsItem Item)
{
    public string Title => Item.Title;
    public string Summary => Item.Summary;
    public string? ImageUrl => Item.ImageUrl;
    public bool HasImage => Item.ImageUrl is not null;
    public string When => $"{Item.Date.LocalDateTime:d MMM yyyy} · {Item.Source}";
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
    public ObservableCollection<GameBananaMod> FreshMods { get; } = [];
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
            var mods = await FeedService.GameBananaLatestAsync(10);
            FreshMods.Clear();
            foreach (var mod in mods.Take(8))
                FreshMods.Add(mod);
        }
        catch (Exception ex)
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
    private static void Open(string? url) => Shell.OpenUrl(url);

    [RelayCommand]
    private static void OpenNews(NewsRow? row) => Shell.OpenUrl(row?.Item.Url);

    [RelayCommand]
    private Task ImportMod(GameBananaMod? mod) => mod is null ? Task.CompletedTask : _main.Mods.ImportFromGameBananaAsync(mod);

    [RelayCommand]
    private void Go(string tab) => _main.Navigate(tab);
}
