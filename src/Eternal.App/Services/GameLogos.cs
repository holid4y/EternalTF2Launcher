using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Eternal.Core.Games;
using Eternal.Core.Logging;

namespace Eternal.App.Services;

/// <summary>
/// Each game's real icon: the game.ico it ships in its own folder, or, for a game
/// that isn't installed, the icon Steam shows for it. Loaded once per run.
/// </summary>
public static class GameLogos
{
    private const int DecodeWidth = 96;
    private const long MaxIconBytes = 1 << 20;
    private static readonly ConcurrentDictionary<string, Bitmap> Loaded = new();

    public static async Task<Bitmap?> GetAsync(GameDefinition game, string? gameFolder, ImageCache images)
    {
        if (Loaded.TryGetValue(game.Id, out var known))
            return known;
        var logo = (gameFolder is null ? null : await Task.Run(() => FromGame(game.IconFile(gameFolder))))
            ?? await images.GetAsync(game.SteamIcon, DecodeWidth);
        return logo is null ? null : Loaded.GetOrAdd(game.Id, logo);
    }

    private static Bitmap? FromGame(string icon)
    {
        try
        {
            if (!File.Exists(icon) || new FileInfo(icon).Length > MaxIconBytes)
                return null;
            using var stream = File.OpenRead(icon);
            return Bitmap.DecodeToWidth(stream, DecodeWidth, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Log.Debug($"Couldn't read {icon}: {ex.Message}");
            return null;
        }
    }
}
