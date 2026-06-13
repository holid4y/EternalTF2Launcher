using LauncherTF2.Services;

namespace LauncherTF2.Core;

/// <summary>
/// Lightweight composition root for shared service instances.
/// Prevents the anti-pattern of each ViewModel creating its own service copies.
/// Initialized once in App.xaml.cs. Accessing any property before
/// <see cref="Initialize"/> has been called throws a clear
/// <see cref="InvalidOperationException"/> rather than a bare NullReferenceException.
/// </summary>
public static class ServiceLocator
{
    private static SettingsService?          _settings;
    private static GameService?              _game;
    private static SteamDetectionService?    _steamDetection;
    private static ModManagerService?        _modManager;
    private static GameBananaEnrichmentService? _enrichment;
    private static HomeFeedService?          _homeFeed;
    private static SteamInventoryService?    _steamInventory;
    private static InventoryPricingService?  _pricing;
    private static InventoryImageCache?      _imageCache;
    private static ThemeManagerService?      _theme;
    private static ProfileService?           _profile;
    private static UpdateCheckerService?     _updateChecker;
    private static PreloaderService?         _preloader;

    public static SettingsService          Settings       => _settings       ?? Throw<SettingsService>();
    public static GameService              Game           => _game           ?? Throw<GameService>();
    public static SteamDetectionService    SteamDetection => _steamDetection ?? Throw<SteamDetectionService>();
    public static ModManagerService        ModManager     => _modManager     ?? Throw<ModManagerService>();
    public static GameBananaEnrichmentService Enrichment  => _enrichment     ?? Throw<GameBananaEnrichmentService>();
    public static HomeFeedService          HomeFeed       => _homeFeed       ?? Throw<HomeFeedService>();
    public static SteamInventoryService    SteamInventory => _steamInventory ?? Throw<SteamInventoryService>();
    public static InventoryPricingService  Pricing        => _pricing        ?? Throw<InventoryPricingService>();
    public static InventoryImageCache      ImageCache     => _imageCache     ?? Throw<InventoryImageCache>();
    public static ThemeManagerService      Theme          => _theme          ?? Throw<ThemeManagerService>();
    public static ProfileService           Profile        => _profile        ?? Throw<ProfileService>();
    public static UpdateCheckerService     UpdateChecker  => _updateChecker  ?? Throw<UpdateCheckerService>();
    public static PreloaderService         Preloader      => _preloader      ?? Throw<PreloaderService>();

    /// <summary>
    /// Wires up all shared services. Must be called once during app startup.
    /// </summary>
    public static void Initialize()
    {
        _settings       = new SettingsService();
        _steamDetection = new SteamDetectionService();
        _preloader      = new PreloaderService(_settings);
        _game           = new GameService(_settings, _preloader);
        _modManager     = new ModManagerService(_settings);
        _enrichment     = new GameBananaEnrichmentService();
        _homeFeed       = new HomeFeedService();
        _steamInventory = new SteamInventoryService();
        _pricing        = new InventoryPricingService();
        _imageCache     = new InventoryImageCache();
        _theme          = new ThemeManagerService(_settings);
        _profile        = new ProfileService();
        _updateChecker  = new UpdateCheckerService();

        Logger.LogInfo("[ServiceLocator] All services initialized successfully");
    }

    private static T Throw<T>() =>
        throw new InvalidOperationException(
            $"ServiceLocator.Initialize() has not been called — {typeof(T).Name} is not available.");
}
