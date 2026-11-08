using Eternal.Core.Logging;
using Eternal.Core.Sounds;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Components;
using SoundFlow.Interfaces;
using SoundFlow.Metadata.Models;
using SoundFlow.Providers;
using SoundFlow.Structs;

namespace Eternal.App.Services;

/// <summary>
/// Plays one sound at a time: a GameBanana preview or a local file. The output device
/// is only open while something plays.
/// </summary>
public static class PreviewPlayer
{
    private static readonly object Gate = new();
    private static AudioPlaybackDevice? _device;
    private static SoundPlayer? _player;
    private static ISoundDataProvider? _provider;
    private static long _generation;

    public static string? PlayingUrl { get; private set; }

    public static event Action? Changed;

    /// <summary>Starts <paramref name="source"/>, a URL or a file path, or stops it if it is already playing.</summary>
    public static void Toggle(string source)
    {
        long generation;
        lock (Gate)
        {
            var same = PlayingUrl == source;
            Release();
            if (same)
            {
                Changed?.Invoke();
                return;
            }
            PlayingUrl = source;
            generation = ++_generation;
        }
        Changed?.Invoke();
        _ = Task.Run(() => Start(source, generation));
    }

    public static void Stop()
    {
        lock (Gate)
        {
            if (PlayingUrl is null)
                return;
            Release();
        }
        Changed?.Invoke();
    }

    private static void Start(string source, long generation)
    {
        ISoundDataProvider? provider = null;
        try
        {
            var engine = CustomSoundDecoder.Engine;
            provider = source.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? new NetworkDataProvider(engine, source, new ReadOptions())
                : new AssetDataProvider(engine, source, new ReadOptions());
            var format = new AudioFormat { Format = provider.SampleFormat, Channels = provider.FormatInfo?.ChannelCount ?? 2, SampleRate = provider.SampleRate };
            lock (Gate)
            {
                if (generation != _generation || PlayingUrl != source)
                {
                    provider.Dispose();
                    return;
                }
                var device = _device = engine.InitializePlaybackDevice(null, format);
                var player = _player = new SoundPlayer(engine, format, provider);
                _provider = provider;
                player.PlaybackEnded += (_, _) => ThreadPool.QueueUserWorkItem(_ => Finished(generation));
                device.MasterMixer.AddComponent(player);
                device.Start();
                player.Play();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't play the preview: {ex.Message}");
            provider?.Dispose();
            lock (Gate)
            {
                if (generation != _generation)
                    return;
                Release();
            }
            Changed?.Invoke();
        }
    }

    private static void Finished(long generation)
    {
        lock (Gate)
        {
            if (generation != _generation)
                return;
            Release();
        }
        Changed?.Invoke();
    }

    /// <summary>Runs under the gate. The device goes first, so its audio thread can't read what is being disposed.</summary>
    private static void Release()
    {
        _generation++;
        var (device, player, provider) = (_device, _player, _provider);
        (_device, _player, _provider) = (null, null, null);
        PlayingUrl = null;
        Quietly(() => device?.Stop());
        Quietly(() => player?.Stop());
        Quietly(() => device?.Dispose());
        Quietly(() => player?.Dispose());
        Quietly(() => provider?.Dispose());
    }

    /// <summary>A sound that ended on its own can already be torn down; the rest still has to be let go.</summary>
    private static void Quietly(Action release)
    {
        try
        {
            release();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or SoundFlow.Exceptions.BackendException)
        {
            Log.Debug($"Stopping the preview: {ex.Message}");
        }
    }
}
