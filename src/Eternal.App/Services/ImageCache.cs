using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;
using Eternal.Core;
using Eternal.Core.Logging;

namespace Eternal.App.Services;

public sealed class ImageCache(string directory)
{
    public const long DiskBudget = 64L * 1024 * 1024;
    private const long MemoryBudget = 128L * 1024 * 1024;
    private const int MaxImageBytes = 16 * 1024 * 1024;
    private static readonly ConditionalWeakTable<Bitmap, NativePixels> Pixels = new();
    private readonly ConcurrentDictionary<string, Bitmap> _memory = new();
    private readonly Queue<string> _order = new();
    private long _memoryBytes;
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
            var file = await FileAsync(url, cancellation).ConfigureAwait(false);
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
                File.Delete(file);
                throw;
            }

            var bytes = PixelBytes(bitmap);
            Pixels.Add(bitmap, new NativePixels(bytes));
            lock (_order)
            {
                _memory[key] = bitmap;
                _order.Enqueue(key);
                _memoryBytes += bytes;
                while (_memoryBytes > MemoryBudget && _order.TryDequeue(out var old))
                {
                    if (_memory.TryRemove(old, out var dropped))
                        _memoryBytes -= PixelBytes(dropped);
                }
            }
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

    public async Task<string> FileAsync(string url, CancellationToken cancellation = default)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only https pictures are loaded.", nameof(url));
        var file = Path.Combine(directory, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..24] + ".img");
        if (File.Exists(file))
            return file;

        await _downloads.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var bytes = await DownloadAsync(url, cancellation).ConfigureAwait(false);
            Directory.CreateDirectory(directory);
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
            return file;
        }
        finally
        {
            _downloads.Release();
        }
    }

    /// <summary>Deletes the pictures downloaded longest ago until the folder fits <see cref="DiskBudget"/>.</summary>
    public void Trim()
    {
        if (!Directory.Exists(directory))
            return;
        var kept = 0L;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.img").OrderByDescending(f => f.LastWriteTimeUtc))
        {
            kept += file.Length;
            if (kept <= DiskBudget)
                continue;
            // Windows refuses to delete a picture another load still has open; it goes on a later trim.
            try
            {
                file.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Debug($"Cached picture {file.Name} kept: {ex.Message}");
            }
        }
    }

    private static long PixelBytes(Bitmap bitmap) => 4L * bitmap.PixelSize.Width * bitmap.PixelSize.Height;

    /// <summary>
    /// A decoded picture's pixels sit in native memory the GC doesn't see, so a bitmap the cache drops is only
    /// freed when a collection happens to run, and browsing allocates too little managed memory for one to run.
    /// Reporting the pixels as pressure makes the GC collect them.
    /// </summary>
    private sealed class NativePixels
    {
        private readonly long _bytes;

        public NativePixels(long bytes)
        {
            _bytes = bytes;
            GC.AddMemoryPressure(bytes);
        }

        ~NativePixels() => GC.RemoveMemoryPressure(_bytes);
    }

    private static async Task<byte[]> DownloadAsync(string url, CancellationToken cancellation)
    {
        using var response = await Http.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxImageBytes)
            throw new InvalidOperationException("Picture is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = await input.ReadAsync(chunk, cancellation).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + n > MaxImageBytes)
                throw new InvalidOperationException("Picture is too large.");
            buffer.Write(chunk, 0, n);
        }
        return buffer.ToArray();
    }
}
