using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using LauncherTF2.Core;

namespace LauncherTF2.Services;

/// <summary>
/// Lazy/cached image loader for Steam Community item icons.
/// In-memory cache returns frozen BitmapImages cheaply; misses fall through to a
/// disk cache under {AppDir}/image_cache, then to the network.
/// </summary>
public class InventoryImageCache
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private readonly ConcurrentDictionary<string, BitmapImage> _memoryCache = new();
    private readonly ConcurrentDictionary<string, Task<BitmapImage?>> _inFlight = new();
    private readonly string _diskCacheDir;

    public InventoryImageCache()
    {
        _diskCacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "image_cache");
        try
        {
            if (!Directory.Exists(_diskCacheDir))
                Directory.CreateDirectory(_diskCacheDir);
        }
        catch (Exception ex)
        {
            Logger.LogWarning("[ImageCache] Failed to create disk cache directory", ex);
        }
    }

    /// <summary>
    /// Returns a frozen BitmapImage for the given URL. Hot-path memory hit is sync.
    /// Cold paths complete asynchronously.
    /// </summary>
    public Task<BitmapImage?> GetAsync(string? url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return Task.FromResult<BitmapImage?>(null);

        if (_memoryCache.TryGetValue(url, out var cached))
            return Task.FromResult<BitmapImage?>(cached);

        return _inFlight.GetOrAdd(url, u => LoadAsync(u, cancellationToken));
    }

    private async Task<BitmapImage?> LoadAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var diskPath = ResolveDiskPath(url);
            byte[]? bytes = null;

            if (File.Exists(diskPath))
            {
                try { bytes = await File.ReadAllBytesAsync(diskPath, cancellationToken); }
                catch { /* fall through to network */ }
            }

            if (bytes == null || bytes.Length == 0)
            {
                bytes = await _http.GetByteArrayAsync(url, cancellationToken);
                try { await File.WriteAllBytesAsync(diskPath, bytes, cancellationToken); }
                catch { /* best-effort */ }
            }

            var image = DecodeFrozen(bytes);
            if (image != null)
                _memoryCache[url] = image;

            return image;
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[ImageCache] Failed to load image: {url}", ex);
            return null;
        }
        finally
        {
            _inFlight.TryRemove(url, out _);
        }
    }

    private static BitmapImage? DecodeFrozen(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = ms;
            image.DecodePixelWidth = 96; // grid card icon size — keeps memory low
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private string ResolveDiskPath(string url)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(url), hash);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return Path.Combine(_diskCacheDir, sb.ToString());
    }
}
