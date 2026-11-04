using SoundFlow.Backends.MiniAudio;
using SoundFlow.Components;
using SoundFlow.Metadata.Models;
using SoundFlow.Providers;
using SoundFlow.Structs;

namespace Eternal.App.Services;

/// <summary>
/// Plays one GameBanana preview at a time, in-app, through miniaudio (bundled natively by SoundFlow).
/// Cards and the details page follow <see cref="Changed"/>.
/// </summary>
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
        _engine ??= new MiniAudioEngine([]);
        var provider = new NetworkDataProvider(_engine, url, new ReadOptions());
        var format = new AudioFormat { Format = provider.SampleFormat, Channels = provider.FormatInfo?.ChannelCount ?? 2, SampleRate = provider.SampleRate };
        _player = new SoundPlayer(_engine, format, provider);
        _player.Play();
        PlayingUrl = url;
        Changed?.Invoke();
    }

    public static void Stop()
    {
        if (PlayingUrl is null)
            return;
        try
        {
            _player?.Stop();
            _player?.Dispose();
        }
        catch (InvalidOperationException)
        {
            // engine already torn down
        }
        _player = null;
        PlayingUrl = null;
        Changed?.Invoke();
    }
}
