using SoundFlow.Backends.MiniAudio;
using SoundFlow.Codecs.FFMpeg;

namespace Eternal.Core.Sounds;

/// <summary>Reads any audio file the player has. miniaudio handles WAV, MP3 and FLAC; FFmpeg covers the rest (OGG, Opus, M4A, AAC, WMA and the audio in videos).</summary>
public static class CustomSoundDecoder
{
    private const long MaxFileBytes = 512L * 1024 * 1024;
    public static MiniAudioEngine Engine { get; } = CreateEngine();
    private static readonly object DecodeGate = new();

    public static readonly string[] FilePatterns =
        ["*.wav", "*.mp3", "*.flac", "*.ogg", "*.oga", "*.opus", "*.m4a", "*.aac", "*.wma", "*.aif", "*.aiff", "*.webm", "*.mp4", "*.mkv"];

    private static MiniAudioEngine CreateEngine()
    {
        var engine = new MiniAudioEngine();
        engine.RegisterCodecFactory(new FFmpegCodecFactory());
        return engine;
    }

    public static ConvertedSound Convert(string path, SoundKind kind)
    {
        var name = Path.GetFileName(path);
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new SoundImportException($"{name} isn't there anymore.");
        if (info.Length > MaxFileBytes)
            throw new SoundImportException($"{name} is over 512 MB. Trim it first.");

        float[] samples;
        int channels, rate;
        bool longer;
        try
        {
            using var stream = File.OpenRead(path);
            lock (DecodeGate)
            {
                using var decoder = Engine.CreateDecoder(stream, out _, null);
                channels = decoder.Channels;
                rate = decoder.SampleRate;
                (samples, longer) = ReadAll(decoder, channels, SoundPipeline.FramesToDecode(kind, Math.Max(1, rate)));
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or InvalidDataException or SoundFlow.Exceptions.BackendException or ArgumentException or EndOfStreamException)
        {
            throw new SoundImportException($"{name} couldn't be read as audio.");
        }
        catch (IOException ex)
        {
            throw new SoundImportException($"{name} couldn't be opened: {ex.Message}");
        }
        catch (TypeInitializationException)
        {
            throw new SoundImportException("The audio decoder couldn't start on this computer.");
        }

        try
        {
            return SoundPipeline.Process(samples, channels, rate, SoundPipeline.MaxLength(kind), longer);
        }
        catch (SoundImportException ex)
        {
            throw new SoundImportException($"{name}: {ex.Message}");
        }
    }

    private static (float[] Samples, bool Longer) ReadAll(SoundFlow.Interfaces.ISoundDecoder decoder, int channels, long maxFrames)
    {
        var limit = checked((int)Math.Min(maxFrames * Math.Max(1, channels), int.MaxValue / 2));
        var buffer = new float[Math.Min(limit, 1 << 20)];
        var filled = 0;
        var longer = false;
        while (true)
        {
            if (filled == buffer.Length)
            {
                if (buffer.Length >= limit)
                {
                    Span<float> probe = stackalloc float[16];
                    longer = decoder.Decode(probe) > 0;
                    break;
                }
                Array.Resize(ref buffer, Math.Min(buffer.Length * 2, limit));
            }
            var read = decoder.Decode(buffer.AsSpan(filled));
            if (read <= 0)
                break;
            filled += read;
        }
        Array.Resize(ref buffer, filled);
        return (buffer, longer);
    }
}
