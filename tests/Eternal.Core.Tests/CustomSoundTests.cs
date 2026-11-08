using Eternal.Core.Platform;
using Eternal.Core.Sounds;

namespace Eternal.Core.Tests;

public sealed class SoundPipelineTests
{
    private static float[] Tone(int rate, double seconds, int channels = 1, float level = 0.5f)
    {
        var frames = (int)(rate * seconds);
        var samples = new float[frames * channels];
        for (var i = 0; i < frames; i++)
            for (var c = 0; c < channels; c++)
                samples[i * channels + c] = level * MathF.Sin(2 * MathF.PI * 440 * i / rate);
        return samples;
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(22050)]
    [InlineData(8000)]
    [InlineData(96000)]
    public void Every_sample_rate_comes_out_as_the_one_tf2_reads(int rate)
    {
        var sound = SoundPipeline.Process(Tone(rate, 1), channels: 1, rate, SoundPipeline.MaxLength(SoundKind.Kill), false);

        Assert.InRange(sound.Pcm.Length, 44100 * 0.95, 44100 * 1.05);
        Assert.InRange(sound.DurationMs, 950, 1050);
    }

    [Fact]
    public void More_than_two_channels_fold_down_to_stereo()
    {
        var sound = SoundPipeline.Process(Tone(44100, 0.2, channels: 6), channels: 6, 44100, SoundPipeline.MaxLength(SoundKind.Hit), false);

        Assert.Equal(2, sound.Channels);
    }

    [Fact]
    public void Mono_stays_mono()
    {
        Assert.Equal(1, SoundPipeline.Process(Tone(44100, 0.2), 1, 44100, SoundPipeline.MaxLength(SoundKind.Hit), false).Channels);
    }

    [Fact]
    public void Silence_at_the_start_is_cut_so_a_hit_sound_plays_on_the_hit()
    {
        var silence = new float[44100];
        var samples = silence.Concat(Tone(44100, 0.2)).ToArray();

        var sound = SoundPipeline.Process(samples, 1, 44100, SoundPipeline.MaxLength(SoundKind.Hit), false);

        Assert.InRange(sound.DurationMs, 180, 260);
    }

    [Fact]
    public void A_file_with_nothing_audible_is_refused()
    {
        Assert.Throws<SoundImportException>(() => SoundPipeline.Process(new float[44100], 1, 44100, SoundPipeline.MaxLength(SoundKind.Hit), false));
    }

    [Fact]
    public void A_sound_longer_than_allowed_is_cut_and_says_so()
    {
        var sound = SoundPipeline.Process(Tone(44100, 8), 1, 44100, SoundPipeline.MaxLength(SoundKind.Hit), false);

        Assert.True(sound.WasCut);
        Assert.Equal((int)SoundPipeline.MaxLength(SoundKind.Hit).TotalMilliseconds, sound.DurationMs);
    }

    [Fact]
    public void Clipping_input_is_scaled_back_instead_of_wrapping_around()
    {
        var sound = SoundPipeline.Process(Tone(44100, 0.2, level: 3f), 1, 44100, SoundPipeline.MaxLength(SoundKind.Hit), false);

        Assert.True(sound.Pcm.Max() > 25000);
        Assert.True(sound.Pcm.Min() < -25000);
    }

    [Fact]
    public void Nothing_longer_than_the_decode_cap_is_kept_in_memory()
    {
        var limit = SoundPipeline.FramesToDecode(SoundKind.Kill, 44100);

        Assert.InRange(limit, 44100, 44100 * 120);
    }
}

public sealed class WaveFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("eternal-wave-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void The_file_has_the_exact_layout_the_game_ships()
    {
        var path = Path.Combine(_dir, "a.wav");

        WaveFile.Write(path, [1, -1, 300, -300], channels: 2);

        var bytes = File.ReadAllBytes(path);
        Assert.Equal("RIFF"u8.ToArray(), bytes[..4]);
        Assert.Equal("WAVEfmt "u8.ToArray(), bytes[8..16]);
        Assert.Equal(1, BitConverter.ToInt16(bytes, 20));
        Assert.Equal(2, BitConverter.ToInt16(bytes, 22));
        Assert.Equal(44100, BitConverter.ToInt32(bytes, 24));
        Assert.Equal(16, BitConverter.ToInt16(bytes, 34));
        Assert.Equal("data"u8.ToArray(), bytes[36..40]);
        Assert.Equal(44 + 8, bytes.Length);
    }

    [Fact]
    public void What_was_written_reads_back()
    {
        var path = Path.Combine(_dir, "a.wav");
        WaveFile.Write(path, [5, -5, 1000], channels: 1);

        var (pcm, channels) = WaveFile.Read(path);

        Assert.Equal(1, channels);
        Assert.Equal(new short[] { 5, -5, 1000 }, pcm);
    }

    [Fact]
    public void Something_else_is_refused()
    {
        var path = Path.Combine(_dir, "a.wav");
        File.WriteAllText(path, "not a wave file at all, sorry about that");

        Assert.Throws<InvalidDataException>(() => WaveFile.Read(path));
    }
}

public sealed class CustomSoundStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("eternal-sounds-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private CustomSoundStore Store() => new(new AppPaths(Path.Combine(_dir, "c"), Path.Combine(_dir, "d"), Path.Combine(_dir, "t")));

    private static ConvertedSound Sound() => new(Enumerable.Repeat((short)8000, 4410).ToArray(), 1, 100, false);

    private string Addon => Path.Combine(_dir, "addons", CustomSoundMod.Folder);

    private void Build(CustomSoundStore store) => store.WriteMod(Path.Combine(_dir, "addons"), Path.Combine(_dir, "staging"));

    [Fact]
    public void One_sound_of_a_kind_replaces_the_stock_file()
    {
        var store = Store();
        store.Add(SoundKind.Hit, "bonk.mp3", Sound());

        Build(store);

        Assert.True(File.Exists(Path.Combine(Addon, "sound", "ui", "hitsound.wav")));
        Assert.False(Directory.Exists(Path.Combine(Addon, "sound", "ui", "eternal")));
    }

    [Fact]
    public void Several_sounds_of_a_kind_get_numbered_names_nothing_from_the_source_file()
    {
        var store = Store();
        store.Add(SoundKind.Kill, "\"; quit ../../evil name.mp3", Sound());
        store.Add(SoundKind.Kill, "espaço e acentuação.ogg", Sound());

        Build(store);

        var names = Directory.GetFiles(Path.Combine(Addon, "sound", "ui", "eternal")).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(["kill_0001.wav", "kill_0002.wav"], names);
        Assert.False(File.Exists(Path.Combine(Addon, "sound", "ui", "killsound.wav")));
    }

    [Fact]
    public void Sounds_switched_off_are_left_out()
    {
        var store = Store();
        var a = store.Add(SoundKind.Hit, "a.wav", Sound());
        store.Add(SoundKind.Hit, "b.wav", Sound());
        store.Update(a.Id, s => s.Enabled = false);

        Build(store);

        Assert.True(File.Exists(Path.Combine(Addon, "sound", "ui", "hitsound.wav")));
    }

    [Fact]
    public void The_mod_goes_away_when_no_sound_is_left()
    {
        var store = Store();
        var a = store.Add(SoundKind.Hit, "a.wav", Sound());
        Build(store);
        store.Remove(a.Id);

        Build(store);

        Assert.False(Directory.Exists(Addon));
    }

    [Fact]
    public void The_volume_is_applied_to_the_built_file_only()
    {
        var store = Store();
        var a = store.Add(SoundKind.Hit, "a.wav", Sound());
        store.Update(a.Id, s => s.Volume = 0.5);

        Build(store);

        var built = WaveFile.Read(Path.Combine(Addon, "sound", "ui", "hitsound.wav")).Pcm;
        var kept = WaveFile.Read(store.WavPath(a)).Pcm;
        Assert.Equal(4000, built[0]);
        Assert.Equal(8000, kept[0]);
    }

    [Fact]
    public void A_loud_volume_clips_instead_of_wrapping_around()
    {
        var store = Store();
        var a = store.Add(SoundKind.Hit, "a.wav", new ConvertedSound(Enumerable.Repeat((short)30000, 10).ToArray(), 1, 1, false));
        store.Update(a.Id, s => s.Volume = 2);

        Build(store);

        Assert.Equal(short.MaxValue, WaveFile.Read(Path.Combine(Addon, "sound", "ui", "hitsound.wav")).Pcm[0]);
    }

    [Fact]
    public void The_list_survives_a_restart()
    {
        var store = Store();
        var a = store.Add(SoundKind.Kill, "a.wav", Sound());
        store.Update(a.Id, s => s.Volume = 1.5);

        var again = Store();

        var loaded = Assert.Single(again.Sounds);
        Assert.Equal("a", loaded.Name);
        Assert.Equal(SoundKind.Kill, loaded.Kind);
        Assert.Equal(1.5, loaded.Volume);
        Assert.True(File.Exists(again.WavPath(loaded)));
    }

    [Fact]
    public void Adding_past_the_limit_is_refused()
    {
        var store = new CustomSoundStore(new AppPaths(Path.Combine(_dir, "c"), Path.Combine(_dir, "d"), Path.Combine(_dir, "t"))) { Limit = 1 };
        store.Add(SoundKind.Hit, "a.wav", Sound());

        Assert.Throws<SoundImportException>(() => store.Add(SoundKind.Hit, "b.wav", Sound()));
    }

    [Fact]
    public void Changing_sounds_from_several_threads_keeps_the_list_whole()
    {
        var store = Store();
        var first = store.Add(SoundKind.Hit, "a.wav", Sound());

        Parallel.For(0, 40, i =>
        {
            if (i % 2 == 0)
                store.Add(SoundKind.Kill, $"{i}.wav", Sound());
            else
                store.Update(first.Id, s => s.Volume = 0.5 + i / 100.0);
        });

        Assert.Equal(21, Store().Sounds.Count);
    }
}

public sealed class CustomSoundDecoderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("eternal-decode-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static void RequireAudio()
    {
        try
        {
            _ = CustomSoundDecoder.Engine;
        }
        catch (TypeInitializationException)
        {
            Assert.Skip("No native audio library on this machine.");
        }
    }

    [Fact]
    public void A_real_wave_file_converts()
    {
        RequireAudio();
        var path = Path.Combine(_dir, "tone.wav");
        WaveFile.Write(path, Enumerable.Range(0, 22050).Select(i => (short)(12000 * Math.Sin(i * 0.06))).ToArray(), channels: 1);

        var sound = CustomSoundDecoder.Convert(path, SoundKind.Hit);

        Assert.Equal(1, sound.Channels);
        Assert.InRange(sound.DurationMs, 450, 550);
    }

    [Fact]
    public void Something_that_is_not_audio_is_refused_by_name()
    {
        RequireAudio();
        var path = Path.Combine(_dir, "notes.mp3");
        File.WriteAllText(path, "this is not audio");

        var ex = Assert.Throws<SoundImportException>(() => CustomSoundDecoder.Convert(path, SoundKind.Hit));

        Assert.Contains("notes.mp3", ex.Message, StringComparison.Ordinal);
    }
}
