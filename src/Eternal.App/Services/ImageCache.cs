using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;
using Eternal.Core;
using Eternal.Core.Logging;

namespace Eternal.App.Services;

/// <summary>
/// Remote pictures (item icons, news art, GameBanana previews) with a disk
/// cache and a small in-memory cache. Decoding is capped to the size the
/// picture is shown at, so a 300-item backpack stays light.
/// </summary>
public sealed class ImageCache(string directory)
{
    private const int MemoryLimit = 600;
    private readonly ConcurrentDictionary<string, Bitmap> _memory = new();
    private readonly ConcurrentQueue<string> _order = new();
    private readonly SemaphoreSlim _downloads = new(6, 6);
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _inflight = new();

    public Task<Bitmap?> GetAsync(string? url, int decodeWidth, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<Bitmap?>(null);
        var key = $"{decodeWidth}:{url}";
        if (_memory.TryGetValue(key, out var hit))
            return Task.FromResult<Bitmap?>(hit);
        return _inflight.GetOrAdd(key, _ => LoadAsync(url, key, decodeWidth, cancellation));
    }

    private async Task<Bitmap?> LoadAsync(string url, string key, int decodeWidth, CancellationToken cancellation)
    {
        try
        {
            var file = Path.Combine(directory, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..24] + ".img");
            if (!File.Exists(file))
            {
                await _downloads.WaitAsync(cancellation).ConfigureAwait(false);
                try
                {
                    var bytes = await Http.Client.GetByteArrayAsync(url, cancellation).ConfigureAwait(false);
                    Directory.CreateDirectory(directory);
                    // Moved into place whole, so an interrupted or parallel download never leaves half a picture.
                    var partial = $"{file}.{Guid.NewGuid():N}.part";
                    try
                    {
                        await File.WriteAllBytesAsync(partial, bytes, cancellation).ConfigureAwait(false);
                        File.Move(partial, file, overwrite: true);
                    }
                    finally
                    {
                        File.Delete(partial);
                    }
                }
                finally
                {
                    _downloads.Release();
                }
            }

            Bitmap bitmap;
            try
            {
                bitmap = await Task.Run(() =>
                {
                    using var stream = File.OpenRead(file);
                    return Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.MediumQuality);
                }, cancellation).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // Not a picture (an error page, or a file cut short by an older version): fetch it again next time.
                File.Delete(file);
                throw;
            }

            _memory[key] = bitmap;
            _order.Enqueue(key);
            while (_order.Count > MemoryLimit && _order.TryDequeue(out var old))
                _memory.TryRemove(old, out _);
            return bitmap;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or ArgumentException or InvalidOperationException)
        {
            Log.Debug($"Image {url} unavailable: {ex.Message}");
            return null;
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }
}
