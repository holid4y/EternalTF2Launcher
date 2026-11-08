using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Eternal.App.Services;
using Eternal.Core.Logging;
using Eternal.Core.Settings;
using Eternal.Core.Sounds;

namespace Eternal.App.ViewModels;

public sealed partial class SoundRowViewModel : ObservableObject
{
    private readonly HitKillSoundsViewModel _owner;
    private bool _loading = true;

    public SoundRowViewModel(CustomSound sound, HitKillSoundsViewModel owner)
    {
        _owner = owner;
        Sound = sound;
        _enabled = sound.Enabled;
        _volume = sound.Volume * 100;
        _loading = false;
    }

    public CustomSound Sound { get; }
    public string Name => Sound.Name;
    public string Length => Sound.DurationMs < 1000 ? $"{Sound.DurationMs} ms" : $"{Sound.DurationMs / 1000.0:0.0} s";
    public string VolumeText => $"{Volume:0}%";

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private double _volume;
    [ObservableProperty] private bool _playing;

    partial void OnEnabledChanged(bool value)
    {
        if (!_loading)
            _owner.Change(Sound, s => s.Enabled = value);
    }

    partial void OnVolumeChanged(double value)
    {
        OnPropertyChanged(nameof(VolumeText));
        if (!_loading)
            _owner.Change(Sound, s => s.Volume = value / 100);
    }
}

/// <summary>Config, Hit and Kill Sounds: the player's own audio files, converted and packed as a mod only this page can switch.</summary>
public sealed partial class HitKillSoundsViewModel : ObservableObject
{
    private static readonly TimeSpan RebuildDelay = TimeSpan.FromMilliseconds(500);
    private readonly AppServices _services;
    private readonly IDialogs _dialogs;
    private readonly Action<string, bool> _notify;
    private readonly SemaphoreSlim _rebuilding = new(1, 1);
    private CancellationTokenSource? _pending;

    public HitKillSoundsViewModel(AppServices services, IDialogs dialogs, Action<string, bool> notify)
    {
        _services = services;
        _dialogs = dialogs;
        _notify = notify;
        _enabled = services.CustomSounds.Enabled;
        Reload();
        PreviewPlayer.Changed += () => Dispatcher.UIThread.Post(RefreshPlaying);
    }

    public ObservableCollection<SoundRowViewModel> HitSounds { get; } = [];
    public ObservableCollection<SoundRowViewModel> KillSounds { get; } = [];

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _showingKill;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private double _busyProgress;

    public bool ShowingHit => !ShowingKill;
    public ObservableCollection<SoundRowViewModel> Shown => ShowingKill ? KillSounds : HitSounds;
    public bool IsEmpty => Shown.Count == 0;
    public string HitTabText => HitSounds.Count == 0 ? "Hit sounds" : $"Hit sounds · {HitSounds.Count}";
    public string KillTabText => KillSounds.Count == 0 ? "Kill sounds" : $"Kill sounds · {KillSounds.Count}";
    public string EmptyTitle => ShowingKill ? "No kill sounds yet" : "No hit sounds yet";
    public string ListNote => Shown.Count switch
    {
        0 => "",
        1 => "This one plays every time.",
        var n when n > 200 => $"TF2 picks one of these {n} at random. Long lists make maps take longer to load.",
        var n => $"TF2 picks one of these {n} at random.",
    };
    public string LimitNote => ShowingKill ? "Sounds longer than 10 seconds get cut." : "Sounds longer than 3 seconds get cut, since a flamethrower plays one on every tick.";
    public string StatusText => Enabled
        ? _services.CustomSounds.Sounds.Count == 0 ? "Add a sound and it goes into your mods by itself." : "On. Your sounds play in game, Casual included."
        : "Off. TF2 plays its own sounds and your list stays here for later.";

    partial void OnEnabledChanged(bool value)
    {
        _services.CustomSounds.Enabled = value;
        OnPropertyChanged(nameof(StatusText));
        if (value)
            MatchGameSettings();
        ScheduleRebuild();
    }

    partial void OnShowingKillChanged(bool value) => ListChanged();

    [RelayCommand]
    private void ShowHit() => ShowingKill = false;

    [RelayCommand]
    private void ShowKill() => ShowingKill = true;

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        var files = await Pickers.OpenFilesAsync(ShowingKill ? "Add kill sounds" : "Add hit sounds", true, "Audio", CustomSoundDecoder.FilePatterns);
        await AddAsync(files);
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (await Pickers.OpenFolderAsync(ShowingKill ? "Add a folder of kill sounds" : "Add a folder of hit sounds") is not { } folder)
            return;
        var files = await Task.Run(() => CustomSoundDecoder.FilePatterns
            .SelectMany(p => Directory.EnumerateFiles(folder, p, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive }))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase).ToArray());
        if (files.Length == 0)
        {
            _notify("There are no audio files in that folder.", true);
            return;
        }
        await AddAsync(files);
    }

    public async Task AddAsync(IReadOnlyList<string> files)
    {
        if (files.Count == 0 || Busy)
            return;
        var kind = ShowingKill ? SoundKind.Kill : SoundKind.Hit;
        Busy = true;
        BusyProgress = 0;
        var problems = new List<string>();
        var cut = 0;
        var added = 0;
        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                BusyText = $"Converting {Path.GetFileName(file)} ({i + 1} of {files.Count})";
                BusyProgress = 100.0 * i / files.Count;
                try
                {
                    var converted = await Task.Run(() => CustomSoundDecoder.Convert(file, kind));
                    await Task.Run(() => _services.CustomSounds.Add(kind, Path.GetFileName(file), converted));
                    added++;
                    cut += converted.WasCut ? 1 : 0;
                }
                catch (Exception ex) when (ex is SoundImportException or IOException or UnauthorizedAccessException)
                {
                    Log.Info($"Sound not added: {ex.Message}");
                    problems.Add(ex.Message);
                }
            }
        }
        finally
        {
            Busy = false;
        }

        Reload();
        if (added > 0)
        {
            MatchGameSettings();
            ScheduleRebuild();
            _notify($"Added {added} {(added == 1 ? "sound" : "sounds")}{(cut > 0 ? $", {cut} cut to length" : "")}.", false);
        }
        if (problems.Count > 0)
            await _dialogs.AlertAsync("Some files weren't added", string.Join("\n", problems.Take(8).Select(p => "• " + p))
                + (problems.Count > 8 ? $"\n…and {problems.Count - 8} more." : ""));
    }

    [RelayCommand]
    private void Play(SoundRowViewModel? row)
    {
        if (row is not null)
            PreviewPlayer.Toggle(_services.CustomSounds.WavPath(row.Sound));
    }

    [RelayCommand]
    private void Remove(SoundRowViewModel? row)
    {
        if (row is null)
            return;
        if (row.Playing)
            PreviewPlayer.Stop();
        _services.CustomSounds.Remove(row.Sound.Id);
        Reload();
        ScheduleRebuild();
    }

    [RelayCommand]
    private async Task RemoveAllAsync()
    {
        var list = Shown.ToList();
        if (list.Count == 0 || !await _dialogs.ConfirmAsync("Remove these sounds", $"Remove all {list.Count} from this list? Your original files aren't touched.", "Remove all", danger: true))
            return;
        PreviewPlayer.Stop();
        foreach (var row in list)
            _services.CustomSounds.Remove(row.Sound.Id);
        Reload();
        ScheduleRebuild();
    }

    internal void Change(CustomSound sound, Action<CustomSound> change)
    {
        _services.CustomSounds.Update(sound.Id, change);
        ScheduleRebuild();
    }

    private void Reload()
    {
        var sounds = _services.CustomSounds.Sounds;
        Fill(HitSounds, sounds.Where(s => s.Kind == SoundKind.Hit));
        Fill(KillSounds, sounds.Where(s => s.Kind == SoundKind.Kill));
        ListChanged();
    }

    private void Fill(ObservableCollection<SoundRowViewModel> rows, IEnumerable<CustomSound> sounds)
    {
        rows.Clear();
        foreach (var sound in sounds)
            rows.Add(new SoundRowViewModel(sound, this));
    }

    private void ListChanged()
    {
        foreach (var name in new[] { nameof(Shown), nameof(ShowingHit), nameof(IsEmpty), nameof(HitTabText), nameof(KillTabText), nameof(EmptyTitle), nameof(ListNote), nameof(LimitNote), nameof(StatusText) })
            OnPropertyChanged(name);
        RefreshPlaying();
    }

    private void RefreshPlaying()
    {
        var playing = PreviewPlayer.PlayingUrl;
        foreach (var row in Shown)
            row.Playing = playing is not null && playing == _services.CustomSounds.WavPath(row.Sound);
    }

    /// <summary>A custom sound only plays when TF2's own switch is on and its style is the default slot.</summary>
    private void MatchGameSettings()
    {
        var settings = _services.Game.Settings;
        void TurnOn(string toggle, string style)
        {
            settings.Set(SettingsCatalog.Get<ToggleSetting>(toggle), true);
            var choice = SettingsCatalog.Get<ChoiceSetting>(style);
            settings.Set(choice, choice.Options.First(o => o.Value == "0"));
        }
        if (!Enabled)
            return;
        if (HitSounds.Any(r => r.Enabled))
            TurnOn("HitSound", "HitSoundEffect");
        if (KillSounds.Any(r => r.Enabled))
            TurnOn("KillSound", "KillSoundEffect");
    }

    private void ScheduleRebuild()
    {
        _pending?.Cancel();
        var cts = _pending = new CancellationTokenSource();
        _ = RebuildAfterDelayAsync(cts.Token);
    }

    private async Task RebuildAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(RebuildDelay, token);
            await _rebuilding.WaitAsync(token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            await _services.RebuildCustomSoundsAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warn($"Couldn't update the hit and kill sounds mod: {ex.Message}");
            _notify($"Your sounds couldn't be packed into a mod: {ex.Message}", true);
        }
        finally
        {
            _rebuilding.Release();
        }
    }
}
