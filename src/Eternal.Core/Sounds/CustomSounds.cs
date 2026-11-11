using System.Text.Json;
using System.Text.Json.Serialization;
using Eternal.Core.Persistence;
using Eternal.Core.Platform;

namespace Eternal.Core.Sounds;

public enum SoundKind { Hit, Kill }

public sealed class SoundImportException(string message) : Exception(message);

public sealed class CustomSound
{
    public string Id { get; set; } = "";
    public SoundKind Kind { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public double Volume { get; set; } = 1;
    public int DurationMs { get; set; }
}

public sealed class CustomSoundSet : IJsonOnDeserialized
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public List<CustomSound> Sounds { get; set; } = [];

    void IJsonOnDeserialized.OnDeserialized() => JsonChecks.NoEmptyEntries(Sounds, "sounds");
}

[JsonSourceGenerationOptions(RespectNullableAnnotations = true, WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(CustomSoundSet))]
internal sealed partial class CustomSoundJson : JsonSerializerContext;

/// <summary>
/// The player's converted hit and kill sounds, kept in the launcher's data folder.
/// Each list can hold many: TF2 plays one at random through a soundscript entry.
/// </summary>
public sealed class CustomSoundStore
{
    /// <summary>TF2's largest stock random list has 185 sounds and its scripts already precache about 11,000.</summary>
    public const int MaxSounds = 2000;
    private const long MaxLibraryBytes = 1L << 30;
    private const int MaxNameLength = 80;

    private readonly string _folder;
    private readonly string _listFile;
    private readonly CustomSoundSet _set;
    private readonly object _gate = new();

    public CustomSoundStore(AppPaths paths)
    {
        _folder = Path.Combine(paths.DataDir, "custom-sounds");
        _listFile = Path.Combine(_folder, "sounds.json");
        _set = JsonFile.Load(_listFile, CustomSoundJson.Default.CustomSoundSet, () => new CustomSoundSet(), out _);
    }

    public int Limit { get; init; } = MaxSounds;

    public IReadOnlyList<CustomSound> Sounds
    {
        get
        {
            lock (_gate)
                return [.. _set.Sounds];
        }
    }

    public bool Enabled
    {
        get
        {
            lock (_gate)
                return _set.Enabled;
        }
        set
        {
            lock (_gate)
            {
                _set.Enabled = value;
                Save();
            }
        }
    }

    public string WavPath(CustomSound sound) => Path.Combine(_folder, "wav", sound.Id + ".wav");

    public CustomSound Add(SoundKind kind, string sourceName, ConvertedSound converted)
    {
        lock (_gate)
        {
            if (_set.Sounds.Count >= Limit)
                throw new SoundImportException($"The list is full at {Limit} sounds. Remove some to add more.");
            if (LibraryBytes() + converted.Pcm.Length * 2L > MaxLibraryBytes)
                throw new SoundImportException("Your sounds already take up 1 GB. Remove some to add more.");

            var sound = new CustomSound
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = kind,
                Name = DisplayName(sourceName),
                DurationMs = converted.DurationMs,
            };
            WaveFile.Write(WavPath(sound), converted.Pcm, converted.Channels);
            _set.Sounds.Add(sound);
            Save();
            return sound;
        }
    }

    public void Update(string id, Action<CustomSound> change)
    {
        lock (_gate)
        {
            if (_set.Sounds.FirstOrDefault(s => s.Id == id) is not { } sound)
                return;
            change(sound);
            sound.Volume = Math.Clamp(sound.Volume, 0.1, 2);
            Save();
        }
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            if (_set.Sounds.FirstOrDefault(s => s.Id == id) is not { } sound)
                return;
            _set.Sounds.Remove(sound);
            File.Delete(WavPath(sound));
            Save();
        }
    }

    /// <summary>Lays out the mod the preloader picks up, or removes it when no sound is left. The old folder stays until the new one is complete.</summary>
    public void WriteMod(string addonsDir, string stagingDir)
    {
        var target = Path.Combine(addonsDir, CustomSoundMod.Folder);
        List<(SoundKind Kind, string Wav, double Volume)> active;
        lock (_gate)
            active = _set.Sounds.Where(s => s.Enabled).Select(s => (s.Kind, WavPath(s), s.Volume)).ToList();
        if (active.Count == 0)
        {
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
            return;
        }

        if (Directory.Exists(stagingDir))
            Directory.Delete(stagingDir, recursive: true);
        Directory.CreateDirectory(stagingDir);
        File.WriteAllText(Path.Combine(stagingDir, "mod.json"), CustomSoundMod.Manifest);
        foreach (var kind in Enum.GetValues<SoundKind>())
            WriteKind(stagingDir, active.Where(s => s.Kind == kind).ToList(), kind);

        Directory.CreateDirectory(addonsDir);
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
        Directory.Move(stagingDir, target);
    }

    private static void WriteKind(string root, List<(SoundKind Kind, string Wav, double Volume)> sounds, SoundKind kind)
    {
        for (var i = 0; i < sounds.Count; i++)
        {
            var destination = Path.Combine(root, CustomSoundMod.FileFor(kind, i, sounds.Count));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            WriteAtVolume(sounds[i].Wav, destination, sounds[i].Volume);
        }
    }

    /// <summary>TF2's own hit and kill sounds average about -17 dBFS with peaks at full scale.</summary>
    private const double TargetRms = 0.1429;
    private const double PeakCeiling = 0.966;

    private static void WriteAtVolume(string source, string destination, double volume)
    {
        var (pcm, channels) = WaveFile.Read(source);
        var gain = MatchedGain(pcm) * volume;
        for (var i = 0; i < pcm.Length; i++)
            pcm[i] = (short)Math.Clamp(Math.Round(pcm[i] * gain), short.MinValue, short.MaxValue);
        WaveFile.Write(destination, pcm, channels);
    }

    /// <summary>
    /// The gain that brings a sound to the loudness of TF2's own hit and kill sounds, as far as its
    /// peak allows. The sound's volume slider applies on top, so 100% means as loud as TF2's.
    /// </summary>
    public static double MatchedGain(short[] pcm)
    {
        double sum = 0;
        var peak = 0;
        foreach (var sample in pcm)
        {
            sum += (double)sample * sample;
            peak = Math.Max(peak, Math.Abs((int)sample));
        }
        if (peak == 0)
            return 1;
        var rms = Math.Sqrt(sum / pcm.Length) / 32768;
        return Math.Min(TargetRms / rms, PeakCeiling * 32768 / peak);
    }

    private long LibraryBytes()
    {
        var wav = Path.Combine(_folder, "wav");
        return Directory.Exists(wav) ? Directory.EnumerateFiles(wav).Sum(f => new FileInfo(f).Length) : 0;
    }

    private static string DisplayName(string sourceName)
    {
        var name = new string(Path.GetFileNameWithoutExtension(sourceName).Where(c => !char.IsControl(c)).ToArray()).Trim();
        return name.Length == 0 ? "Sound" : name.Length > MaxNameLength ? name[..MaxNameLength] : name;
    }

    private void Save()
    {
        Directory.CreateDirectory(_folder);
        AtomicFile.WriteAllBytes(_listFile, JsonSerializer.SerializeToUtf8Bytes(_set, CustomSoundJson.Default.CustomSoundSet));
    }
}

/// <summary>Where sounds go in the generated mod. File names never come from the player's files, so nothing odd reaches a soundscript.</summary>
public static class CustomSoundMod
{
    public const string Folder = "eternal-hit-kill-sounds";

    public const string Manifest = """
        {
          "addon_name": "Hit & kill sounds",
          "type": "Sound",
          "description": "Made in Config, Hit & Kill Sounds.",
          "contents": ["Sounds"]
        }

        """;

    /// <summary>A lone sound takes the stock file's place. Several go under sound/ui, which Casual still reads, for the engine to list in a soundscript.</summary>
    public static string FileFor(SoundKind kind, int index, int count)
    {
        var stem = kind == SoundKind.Hit ? "hit" : "kill";
        var relative = count == 1
            ? $"sound/ui/{stem}sound.wav"
            : $"sound/ui/eternal/{stem}_{(index + 1).ToString("D4", System.Globalization.CultureInfo.InvariantCulture)}.wav";
        return relative.Replace('/', Path.DirectorySeparatorChar);
    }
}
