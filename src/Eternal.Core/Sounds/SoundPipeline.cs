using System.Buffers.Binary;

namespace Eternal.Core.Sounds;

public sealed record ConvertedSound(short[] Pcm, int Channels, int DurationMs, bool WasCut);

/// <summary>
/// Turns decoded audio into what TF2's sound system reads: 16-bit PCM at 44.1 kHz,
/// mono or stereo. The Source engine only plays 44100, 22050 and 11025 Hz.
/// </summary>
public static class SoundPipeline
{
    public const int SampleRate = 44100;
    private const float SilenceLevel = 0.004f;
    private const int SincRadius = 16;
    private static readonly TimeSpan LeadIn = TimeSpan.FromMilliseconds(4);
    private static readonly TimeSpan Tail = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan CutFade = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan SearchWindow = TimeSpan.FromSeconds(60);

    /// <summary>A hit sound repeats on every tick of a flamethrower, so it has to be short.</summary>
    public static TimeSpan MaxLength(SoundKind kind) => kind == SoundKind.Hit ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(10);

    /// <summary>Enough to get past silence at the start without holding a whole album in memory.</summary>
    public static long FramesToDecode(SoundKind kind, int rate) => (long)((MaxLength(kind) + SearchWindow).TotalSeconds * rate);

    public static ConvertedSound Process(float[] samples, int channels, int rate, TimeSpan maxLength, bool sourceWasLonger)
    {
        if (channels < 1 || rate < 1 || samples.Length < channels)
            throw new SoundImportException("There's no sound in that file.");

        var (folded, outChannels) = FoldToStereo(samples, channels);
        var resampled = rate == SampleRate ? folded : Resample(folded, outChannels, rate);

        var frames = resampled.Length / outChannels;
        var first = FirstAudible(resampled, outChannels);
        if (first < 0)
            throw new SoundImportException("That file is silent.");
        var last = LastAudible(resampled, outChannels);

        var start = Math.Max(0, first - Frames(LeadIn));
        var end = Math.Min(frames, last + 1 + Frames(Tail));
        var limit = Frames(maxLength);
        var wasCut = sourceWasLonger || end - start > limit;
        var count = Math.Min(end - start, limit);

        var output = new float[count * outChannels];
        Array.Copy(resampled, start * outChannels, output, 0, output.Length);
        if (wasCut)
            FadeOut(output, outChannels);

        var peak = output.Length == 0 ? 0 : output.Max(MathF.Abs);
        var gain = peak > 1f ? 1f / peak : 1f;
        var pcm = new short[output.Length];
        for (var i = 0; i < pcm.Length; i++)
            pcm[i] = (short)Math.Clamp(MathF.Round(output[i] * gain * short.MaxValue), short.MinValue, short.MaxValue);
        return new ConvertedSound(pcm, outChannels, (int)(count * 1000L / SampleRate), wasCut);
    }

    private static int Frames(TimeSpan span) => (int)(span.TotalSeconds * SampleRate);

    private static (float[] Samples, int Channels) FoldToStereo(float[] samples, int channels)
    {
        if (channels <= 2)
            return (samples, channels);
        var frames = samples.Length / channels;
        var stereo = new float[frames * 2];
        var perSide = (channels + 1) / 2;
        for (var f = 0; f < frames; f++)
        {
            float left = 0, right = 0;
            for (var c = 0; c < channels; c++)
            {
                if (c % 2 == 0)
                    left += samples[f * channels + c];
                else
                    right += samples[f * channels + c];
            }
            stereo[f * 2] = left / perSide;
            stereo[f * 2 + 1] = right / Math.Max(1, channels / 2);
        }
        return (stereo, 2);
    }

    private static float[] Resample(float[] samples, int channels, int rate)
    {
        var frames = samples.Length / channels;
        var outFrames = (int)((long)frames * SampleRate / rate);
        var output = new float[outFrames * channels];
        var step = (double)rate / SampleRate;
        var cutoff = Math.Min(1.0, 1.0 / step);
        var window = (int)Math.Ceiling(SincRadius / cutoff);
        Parallel.For(0, outFrames, f =>
        {
            var position = f * step;
            var center = (int)Math.Floor(position);
            for (var c = 0; c < channels; c++)
            {
                double sum = 0, weightTotal = 0;
                for (var i = center - window + 1; i <= center + window; i++)
                {
                    if (i < 0 || i >= frames)
                        continue;
                    var offset = position - i;
                    var weight = Sinc(offset * cutoff) * Hann(offset / window);
                    sum += samples[i * channels + c] * weight;
                    weightTotal += weight;
                }
                output[f * channels + c] = weightTotal == 0 ? 0f : (float)(sum / weightTotal);
            }
        });
        return output;
    }

    private static double Sinc(double x) => Math.Abs(x) < 1e-9 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);

    private static double Hann(double x) => Math.Abs(x) >= 1 ? 0 : 0.5 + 0.5 * Math.Cos(Math.PI * x);

    private static int FirstAudible(float[] samples, int channels)
    {
        for (var i = 0; i < samples.Length; i++)
            if (MathF.Abs(samples[i]) > SilenceLevel)
                return i / channels;
        return -1;
    }

    private static int LastAudible(float[] samples, int channels)
    {
        for (var i = samples.Length - 1; i >= 0; i--)
            if (MathF.Abs(samples[i]) > SilenceLevel)
                return i / channels;
        return -1;
    }

    private static void FadeOut(float[] samples, int channels)
    {
        var fade = Math.Min(Frames(CutFade), samples.Length / channels);
        for (var i = 0; i < fade; i++)
        {
            var level = (float)i / fade;
            var frame = samples.Length / channels - 1 - i;
            for (var c = 0; c < channels; c++)
                samples[frame * channels + c] *= level;
        }
    }
}

/// <summary>Plain canonical WAV: only the fmt and data chunks. A cue chunk would make the Source engine loop the sound.</summary>
public static class WaveFile
{
    private const int HeaderSize = 44;

    public static void Write(string path, short[] pcm, int channels)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = pcm.Length * 2;
        var bytes = new byte[HeaderSize + data];
        var span = bytes.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + data);
        "WAVEfmt "u8.CopyTo(span[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], SoundPipeline.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], SoundPipeline.SampleRate * channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], (short)(channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], data);
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(pcm.AsSpan()).CopyTo(span[HeaderSize..]);
        File.WriteAllBytes(path, bytes);
    }

    public static (short[] Pcm, int Channels) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < HeaderSize || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 8).SequenceEqual("WAVEfmt "u8)
            || !bytes.AsSpan(36, 4).SequenceEqual("data"u8))
            throw new InvalidDataException($"{Path.GetFileName(path)} isn't a sound the launcher wrote.");
        var channels = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(22));
        var length = Math.Min(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40)), bytes.Length - HeaderSize);
        var pcm = new short[length / 2];
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(bytes.AsSpan(HeaderSize, pcm.Length * 2)).CopyTo(pcm);
        return (pcm, channels);
    }
}
