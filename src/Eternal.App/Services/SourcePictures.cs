using System.Globalization;
using Eternal.Core.GameBanana;
using Eternal.Core.Logging;
using Eternal.Core.Mods;

namespace Eternal.App.Services;

public sealed class SourcePictures(ModLibrary mods, ImageCache images, GameBananaClient client)
{
    private readonly object _gate = new();
    private readonly Queue<string> _pending = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private bool _running;

    public void Refresh()
    {
        if (!mods.Loaded)
            return;
        lock (_gate)
        {
            foreach (var source in mods.Items.Select(i => i.Source).OfType<string>().Distinct())
            {
                if (ThumbnailStore.SourceKey(source) is not { } key || !_seen.Add(key) || mods.Thumbnails.KnowsSource(key))
                    continue;
                _pending.Enqueue(source);
            }
            if (_running || _pending.Count == 0)
                return;
            _running = true;
        }
        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        var changed = false;
        while (true)
        {
            string source;
            lock (_gate)
            {
                if (!_pending.TryDequeue(out source!))
                {
                    _running = false;
                    break;
                }
            }
            changed |= await FetchAsync(source).ConfigureAwait(false);
        }
        mods.Thumbnails.PruneSources(mods.State.Order.Select(e => e.Source));
        if (changed)
            mods.ThumbnailChanged();
    }

    private async Task<bool> FetchAsync(string source)
    {
        var key = ThumbnailStore.SourceKey(source)!;
        var parts = source.Split(':');
        var section = parts[0] == "gamebanana-sound" ? Section.Sounds : Section.Mods;
        var id = long.Parse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var details = await client.DetailsAsync(section, id, timeout.Token).ConfigureAwait(false);
            if ((details.Summary.Thumbnail ?? details.Screenshots.FirstOrDefault()) is not { } url)
            {
                mods.Thumbnails.MarkSourceMissing(key);
                return false;
            }
            var file = await images.FileAsync(url, timeout.Token).ConfigureAwait(false);
            if (await Task.Run(() => ThumbnailMaker.FromFile(file)).ConfigureAwait(false) is not { } png)
            {
                mods.Thumbnails.MarkSourceMissing(key);
                return false;
            }
            mods.Thumbnails.SaveForSource(key, png);
            return true;
        }
        catch (Exception ex) when (ex is GameBananaException or HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Log.Debug($"No picture for {source} this time: {ex.Message}");
            return false;
        }
    }
}
