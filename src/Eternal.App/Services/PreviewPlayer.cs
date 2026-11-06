using Eternal.Core.Logging;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Components;
using SoundFlow.Metadata.Models;
using SoundFlow.Providers;
using SoundFlow.Structs;

namespace Eternal.App.Services;

public static class PreviewPlayer
{
    private static MiniAudioEngine? _engine;
    private static SoundPlayer? _player;

    public static string? PlayingUrl { get; private set; }

    public static event Action? Changed;

    public static void Toggle(string url)
    {
        if (PlayingUrl == url)
        {
            Stop();
            return;
        }
        Stop();
        try
        {
            _engine ??= new MiniAudioEngine([]);
            var provider = new NetworkDataProvider(_engine, url, new ReadOptions());
            var format = new AudioFormat { Format = provider.SampleFormat, Channels = provider.FormatInfo?.ChannelCount ?? 2, SampleRate = provider.SampleRate };
            var player = _player = new SoundPlayer(_engine, format, provider);
            player.PlaybackEnded += (_, _) => ThreadPool.QueueUserWorkItem(_ =>
            {
                if (ReferenceEquals(_player, player))
                    Stop();
            });
            player.Play();
            PlayingUrl = url;
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn($"The preview of {url} couldn't be played: {ex.Message}");
            Stop();
        }
    }

    public static void Stop()
    {
        if (PlayingUrl is null && _player is null)
            return;
        try
        {
            _player?.Stop();
            _player?.Dispose();
        }
        catch (InvalidOperationException)
        {
        }
        _player = null;
        PlayingUrl = null;
        Changed?.Invoke();
    }
}
