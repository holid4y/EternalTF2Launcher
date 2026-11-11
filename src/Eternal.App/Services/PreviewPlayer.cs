using Eternal.Core;
using Eternal.Core.Logging;
using Eternal.Core.Sounds;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Components;
using SoundFlow.Enums;
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
    private static float _volume = 0.7f;
    private static float _gain = 1f;
    private const int MaxDownloadBytes = 32 * 1024 * 1024;

    public static string? PlayingUrl { get; private set; }

    /// <summary>From 0 to 1. Applies straight away to whatever is playing.</summary>
    public static double Volume
    {
        get => _volume;
        set
        {
            lock (Gate)
            {
                _volume = (float)Math.Clamp(value, 0, 1);
                _player?.Volume = _volume * _gain;
            }
        }
    }

    public static event Action? Changed;

    /// <summary>Starts <paramref name="source"/>, a URL or a file path, or stops it if it is already playing.</summary>
    /// <param name="gain">Applied on top of the preview volume, so a sound can be heard as loud as it will be in game.</param>
    public static void Toggle(string source, double gain = 1)
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
            _gain = (float)Math.Clamp(gain, 0, 8);
            generation = ++_generation;
        }
        Changed?.Invoke();
        _ = Task.Run(() => StartAsync(source, generation));
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

    /// <summary>SoundFlow mixes in 32-bit float whatever the source is, so that is what the device is opened with.</summary>
    public static AudioFormat OutputFormat(int channels, int sampleRate) => new()
    {
        Format = SampleFormat.F32,
        Channels = channels > 0 ? channels : 2,
        SampleRate = sampleRate > 0 ? sampleRate : 44100,
    };

    private static async Task<byte[]> DownloadAsync(string url)
    {
        using var response = await Http.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await Http.ReadCappedAsync(response.Content, MaxDownloadBytes, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task StartAsync(string source, long generation)
    {
        ISoundDataProvider? provider = null;
        try
        {
            var engine = CustomSoundDecoder.Engine;
            // Decoded before the device opens: an error inside the audio callback can't be caught and ends the process.
            provider = source.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? new AssetDataProvider(engine, await DownloadAsync(source).ConfigureAwait(false), new ReadOptions())
                : new AssetDataProvider(engine, source, new ReadOptions());
            var format = OutputFormat(provider.FormatInfo?.ChannelCount ?? 0, provider.SampleRate);
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
                player.Volume = _volume * _gain;
                device.MasterMixer.AddComponent(player);
                device.Start();
                player.Play();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't play the preview {source}", ex);
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
