using System.Collections.ObjectModel;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Eternal.App.Services;
using Eternal.Core.Engine;
using Eternal.Core.Logging;
using Eternal.Core.Mods;
using Eternal.Core.Persistence;
using Eternal.Core.Platform;
using Eternal.Core.Profiles;
using Eternal.Core.Settings;

namespace Eternal.App.ViewModels;

public sealed partial class BindRowViewModel : ObservableObject
{
    private readonly Action _changed;

    public BindRowViewModel(KeyBind bind, Action changed)
    {
        _changed = changed;
        _key = bind.Key;
        _command = bind.Command;
        _label = bind.Label ?? "";
    }

    [ObservableProperty] private string _key;
    [ObservableProperty] private string _command;
    [ObservableProperty] private string _label;
    [ObservableProperty] private bool _capturing;

    public bool IsValid => ManagedBlock.IsValidBind(ToBind());
    public string KeyText => Capturing ? "Press a key…" : string.IsNullOrEmpty(Key) ? "Set key" : Key.ToUpperInvariant();

    partial void OnKeyChanged(string value) => Changed();
    partial void OnCommandChanged(string value) => Changed();
    partial void OnLabelChanged(string value) => Changed();
    partial void OnCapturingChanged(bool value) => OnPropertyChanged(nameof(KeyText));

    private void Changed()
    {
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(KeyText));
        _changed();
    }

    public KeyBind ToBind() => new() { Key = Key, Command = Command, Label = string.IsNullOrWhiteSpace(Label) ? null : Label };
}

public sealed partial class ConfigViewModel : PageViewModel
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;
    private readonly List<SettingRowViewModel> _rows = [];

    public ConfigViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
        _showAdvanced = services.Launcher.Settings.ShowAdvancedSettings;

        foreach (var category in SettingsCatalog.Categories)
        {
            var vm = new SettingCategoryViewModel(category.Id, category.Title, category.Summary);
            foreach (var definition in category.Settings)
            {
                SettingRowViewModel row = definition switch
                {
                    ToggleSetting t => new ToggleRowViewModel(t, services.Game.Settings),
                    SliderSetting s => new SliderRowViewModel(s, services.Game.Settings),
                    ChoiceSetting c => new ChoiceRowViewModel(c, services.Game.Settings),
                    TextSetting x => new TextRowViewModel(x, services.Game.Settings),
                    _ => throw new InvalidOperationException(definition.Id),
                };
                if (!row.Applies || !services.GameInfo.Supports(definition))
                    continue;
                vm.Rows.Add(row);
                _rows.Add(row);
            }
            Categories.Add(vm);
        }
        Sounds = new HitKillSoundsViewModel(services, main, (message, isError) => main.Notify(message, isError));
        if (services.GameInfo.Has(Eternal.Core.Games.GameFeatures.HitKillSounds))
        {
            Sounds.Changed += ShowOwnSounds;
            ShowOwnSounds();
            Categories.Add(new SettingCategoryViewModel("sounds", "Hit & Kill Sounds", "Your own audio files as hit and kill sounds."));
        }
        Categories.Add(new SettingCategoryViewModel("binds", "Binds", "Keys that run commands. Written after your settings, so they win."));
        Categories.Add(new SettingCategoryViewModel("launch", "Launch Options", $"Your own launch options and the full line {services.GameInfo.ShortName} starts with."));
        SelectedCategory = Categories[0];

        services.Game.Settings.Changed += _ => Dispatcher.UIThread.Post(OnSettingsChanged);
        services.Game.Saved += report => Dispatcher.UIThread.Post(() => ShowSave(report));
        services.Preload.StatusChanged += _ => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(LaunchLine)));
        LoadBinds();
        RefreshProfiles();
        ApplyVisibility();
    }

    public ObservableCollection<SettingCategoryViewModel> Categories { get; } = [];
    public ObservableCollection<BindRowViewModel> Binds { get; } = [];
    public ObservableCollection<Profile> Profiles { get; } = [];
    public HitKillSoundsViewModel Sounds { get; }

    private void ShowOwnSounds()
    {
        foreach (var (id, kind) in new[] { ("HitSoundEffect", Eternal.Core.Sounds.SoundKind.Hit), ("KillSoundEffect", Eternal.Core.Sounds.SoundKind.Kill) })
        {
            if (_rows.OfType<ChoiceRowViewModel>().FirstOrDefault(r => r.Definition.Id == id) is { } row)
                row.ShowOwnDefault(Sounds.OwnSoundLabel(kind));
        }
    }

    public override void Close() => Sounds.Close();

    [ObservableProperty] private SettingCategoryViewModel? _selectedCategory;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private IReadOnlyList<SettingRowViewModel> _searchResults = [];
    [ObservableProperty] private bool _showAdvanced;
    [ObservableProperty] private Profile? _selectedProfile;
    [ObservableProperty] private string _profileMatch = "";
    [ObservableProperty] private string _saveStatus = "";
    [ObservableProperty] private bool _saveProblem;
    [ObservableProperty] private bool _showPreview;

    public bool IsSearching => Search.Trim().Length > 0;
    public bool ShowsSettings => !IsSearching && SelectedCategory is { Id: not ("binds" or "launch" or "sounds") };
    public bool ShowsSounds => !IsSearching && SelectedCategory?.Id == "sounds";
    public bool ShowsBinds => !IsSearching && SelectedCategory?.Id == "binds";
    public bool ShowsLaunch => !IsSearching && SelectedCategory?.Id == "launch";
    public bool CanEditProfile => SelectedProfile is { BuiltIn: false };

    public string ExtraArgs
    {
        get => _services.Game.Settings.ExtraLaunchArgs;
        set
        {
            _services.Game.Settings.ExtraLaunchArgs = value ?? "";
            OnPropertyChanged(nameof(ExtraArgsProblem));
            OnPropertyChanged(nameof(LaunchLine));
        }
    }

    public string? ExtraArgsProblem
    {
        get
        {
            if (LaunchOptions.HasUnbalancedQuotes(ExtraArgs))
                return "A quote is never closed, so everything after it would become one argument.";
            var managed = LaunchOptions.ManagedFlagsIn(ExtraArgs);
            return managed.Count == 0
                ? null
                : $"{string.Join(", ", managed)} {(managed.Count == 1 ? "is" : "are")} set by the launcher, so {(managed.Count == 1 ? "it is" : "they are")} ignored here. Use the settings instead.";
        }
    }

    public string LaunchLine
    {
        get
        {
            var tokens = LaunchOptions.Build(_services.Game.Settings, _services.Preload.Record.Installed);
            return tokens.Count == 0 ? $"(no options, {Short} starts with its own settings)" : LaunchOptions.Join(tokens);
        }
    }

    public string AutoexecPreview => string.Join(Environment.NewLine, ManagedBlock.Build(_services.Game.Settings));

    public string AutoexecPath => _services.TfPath is { } tf ? AutoexecFile.Locate(tf) : $"{Short} folder not set";

    private string Short => _services.GameInfo.ShortName;
    public string LaunchTitle => $"{Short} starts with";
    public string LaunchNote => (_services.GameInfo == Eternal.Core.Games.GameCatalog.Tf2
            ? "+exec w/config.cfg is added while mods are applied; it runs the preloader's setup when TF2 boots. "
            : "")
        + $"If you start {Short} from Steam instead of here, copy this line into {Short}'s launch options in Steam.";

    partial void OnSearchChanged(string value)
    {
        var query = value.Trim();
        SearchResults = query.Length == 0 ? [] : _rows.Where(r => r.Matches(query)).ToArray();
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(ShowsSettings));
        OnPropertyChanged(nameof(ShowsSounds));
        OnPropertyChanged(nameof(ShowsBinds));
        OnPropertyChanged(nameof(ShowsLaunch));
    }

    public void ShowSounds() => SelectedCategory = Categories.First(c => c.Id == "sounds");

    partial void OnSelectedCategoryChanged(SettingCategoryViewModel? value)
    {
        OnPropertyChanged(nameof(ShowsSettings));
        OnPropertyChanged(nameof(ShowsSounds));
        OnPropertyChanged(nameof(ShowsBinds));
        OnPropertyChanged(nameof(ShowsLaunch));
        if (value?.Id == "launch")
            OnPropertyChanged(nameof(LaunchLine));
    }

    partial void OnShowAdvancedChanged(bool value)
    {
        _services.Launcher.Update(s => s.ShowAdvancedSettings = value);
        ApplyVisibility();
    }

    partial void OnSelectedProfileChanged(Profile? value) => OnPropertyChanged(nameof(CanEditProfile));

    partial void OnShowPreviewChanged(bool value) => OnPropertyChanged(nameof(AutoexecPreview));

    private void ApplyVisibility()
    {
        foreach (var category in Categories)
        {
            category.VisibleRows = category.Rows.Where(r => ShowAdvanced || !r.IsAdvanced || r.IsTouched).ToArray();
            category.ChangedCount = category.Rows.Count(r => r.IsTouched);
            category.AdvancedHidden = category.Rows.Count(r => r.IsAdvanced && !r.IsTouched && !ShowAdvanced);
        }
    }

    private void OnSettingsChanged()
    {
        foreach (var row in _rows)
            row.Refresh();
        foreach (var category in Categories)
            category.ChangedCount = category.Rows.Count(r => r.IsTouched);
        OnPropertyChanged(nameof(LaunchLine));
        if (ShowPreview)
            OnPropertyChanged(nameof(AutoexecPreview));
        UpdateProfileMatch();
        if (!RowsShowSavedBinds())
            LoadBinds();
    }

    private void ShowSave(SaveReport report)
    {
        SaveProblem = !report.ConfigSaved || report.Autoexec.Status == AutoexecStatus.Failed;
        SaveStatus = (report.ConfigSaved, report.Autoexec.Status) switch
        {
            (false, _) => $"Settings not saved: {report.ConfigError}",
            (true, AutoexecStatus.Failed) => $"Saved, but autoexec.cfg wasn't updated: {report.Autoexec.Error}",
            (true, AutoexecStatus.NoGameFolder) => $"Saved. Set your {Short} folder in the Launcher tab to apply it to the game.",
            (true, AutoexecStatus.LeftAlone) => "Saved here only. Turn on writing to autoexec.cfg in the Launcher tab to apply it to the game.",
            _ => $"Saved and written to {Path.GetFileName(Path.GetDirectoryName(report.Autoexec.Path ?? "") ?? "")}/autoexec.cfg",
        };
        if (report.Viewmodels is { Status: ViewmodelStatus.Failed or ViewmodelStatus.NoLayout } vm)
        {
            SaveProblem = true;
            SaveStatus += $". See-through weapons couldn't be set up: {vm.Error}";
        }
        else if (report.Viewmodels is { Status: ViewmodelStatus.Applied, From: { } from })
        {
            SaveStatus += $". See-through weapons ride on {from}.";
        }
    }

    [RelayCommand]
    private async Task RetrySaveAsync() => ShowSave(await _services.Game.ReapplyAsync());

    private void LoadBinds()
    {
        Binds.Clear();
        foreach (var bind in _services.Game.Settings.Binds)
            Binds.Add(new BindRowViewModel(bind, SaveBinds));
    }

    private void SaveBinds() => _services.Game.Settings.SetBinds(RowsToSave());

    private IEnumerable<KeyBind> RowsToSave() => Binds.Select(b => b.ToBind()).Where(b => b.Key.Length > 0 || b.Command.Length > 0);

    private bool RowsShowSavedBinds()
    {
        var saved = _services.Game.Settings.Binds;
        var shown = RowsToSave().ToList();
        return saved.Count == shown.Count
            && saved.Zip(shown).All(p => p.First.Key == p.Second.Key && p.First.Command == p.Second.Command && p.First.Label == p.Second.Label);
    }

    [RelayCommand]
    private void AddBind()
    {
        var row = new BindRowViewModel(new KeyBind(), SaveBinds);
        Binds.Add(row);
        row.Capturing = true;
    }

    [RelayCommand]
    private void RemoveBind(BindRowViewModel? row)
    {
        if (row is null)
            return;
        Binds.Remove(row);
        SaveBinds();
    }

    [RelayCommand]
    private void CaptureKey(BindRowViewModel? row)
    {
        foreach (var other in Binds)
            other.Capturing = false;
        if (row is not null)
            row.Capturing = true;
    }

    public bool HandleCapture(Key key, MouseButton? mouse = null, int wheel = 0)
    {
        var row = Binds.FirstOrDefault(b => b.Capturing);
        if (row is null)
            return false;
        if (key == Key.Escape && mouse is null && wheel == 0)
        {
            row.Capturing = false;
            return true;
        }
        var name = wheel != 0 ? (wheel > 0 ? "mwheelup" : "mwheeldown") : mouse is { } m ? SourceKeys.FromMouse(m) : SourceKeys.FromKey(key);
        if (name is null)
            return true;
        row.Key = name;
        row.Capturing = false;
        return true;
    }

    private void RefreshProfiles()
    {
        var selected = SelectedProfile?.Id;
        Profiles.Clear();
        foreach (var profile in _services.Profiles.All)
            Profiles.Add(profile);
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == selected) ?? _services.Profiles.FindMatch(_services.Game.Settings) ?? Profiles.FirstOrDefault();
        UpdateProfileMatch();
    }

    private void UpdateProfileMatch()
    {
        var match = _services.Profiles.FindMatch(_services.Game.Settings);
        ProfileMatch = match is null ? "Custom settings" : $"Matches {match.Name}";
    }

    [RelayCommand]
    private async Task ApplyProfileAsync()
    {
        if (SelectedProfile is not { } profile)
            return;
        var message = profile.Complete
            ? $"Replace your current settings, binds and launch options with \"{profile.Name}\"?"
            : $"Apply the {profile.Values.Count} settings in \"{profile.Name}\"? Everything else stays as it is.";
        if (!await _main.ConfirmAsync("Apply profile", message, "Apply"))
            return;

        Log.Info($"Applying profile {profile.Name}");
        ProfileStore.Apply(profile, _services.Game.Settings);
        if (profile.UserAutoexec is { } text && await ConfirmAutoexecAsync(text))
            _services.Game.SetPendingUserContent(text);
        var missing = profile.Mods is { } mods ? _services.Mods.ApplySelection(mods) : [];
        _main.Notify(missing.Count == 0
            ? $"Applied {profile.Name}."
            : $"Applied {profile.Name}. Not in your mod library, so left out: {string.Join(", ", missing)}.");
    }

    [RelayCommand]
    private async Task SaveAsProfileAsync()
    {
        var name = await _main.PromptAsync("Save profile", "Name this setup. It stores every setting, your binds and launch options.");
        if (string.IsNullOrEmpty(name))
            return;
        var includeMods = _services.Mods.Loaded && await _main.ConfirmAsync("Include mods?", "Also remember which mods are enabled and in what order?", "Include mods", "Settings only");
        var profile = ProfileStore.Capture(_services.Game.Settings, name, null, ReadUserAutoexec(), includeMods ? CurrentMods() : null);
        _services.Profiles.Save(profile);
        RefreshProfiles();
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == profile.Id);
    }

    [RelayCommand]
    private async Task UpdateProfileAsync()
    {
        if (SelectedProfile is not { BuiltIn: false } existing)
            return;
        if (!await _main.ConfirmAsync("Update profile", $"Overwrite \"{existing.Name}\" with your current setup?", "Overwrite"))
            return;
        var updated = ProfileStore.Capture(_services.Game.Settings, existing.Name, existing.Description, ReadUserAutoexec(), existing.Mods is null ? null : CurrentMods());
        updated.Id = existing.Id;
        _services.Profiles.Save(updated);
        RefreshProfiles();
    }

    [RelayCommand]
    private async Task RenameProfileAsync()
    {
        if (SelectedProfile is not { BuiltIn: false } profile)
            return;
        var name = await _main.PromptAsync("Rename profile", "New name:", profile.Name, "Rename");
        if (string.IsNullOrEmpty(name))
            return;
        profile.Name = name;
        _services.Profiles.Save(profile);
        RefreshProfiles();
    }

    [RelayCommand]
    private async Task DeleteProfileAsync()
    {
        if (SelectedProfile is not { BuiltIn: false } profile)
            return;
        if (!await _main.ConfirmAsync("Delete profile", $"Delete \"{profile.Name}\"?", "Delete", danger: true))
            return;
        _services.Profiles.Delete(profile.Id);
        RefreshProfiles();
    }

    [RelayCommand]
    private async Task CopySetupCodeAsync()
    {
        if (await NameSetupAsync() is not { } name)
            return;
        var profile = ProfileStore.Capture(_services.Game.Settings, name, null, await AutoexecToShareAsync(), _services.Mods.Loaded ? CurrentMods() : null);
        var code = ShareCode.Encode(profile);
        await Pickers.CopyAsync(code);
        var handMade = HandMadeMods().Count;
        _main.Notify(handMade == 0
            ? "Setup code copied. Paste it anywhere; importing it downloads the GameBanana mods too."
            : $"Setup code copied. {handMade} of your mods aren't from GameBanana, so they only come along in a bundle (Share, Save as a file).");
    }

    [RelayCommand]
    private async Task SaveSetupFileAsync()
    {
        if (await NameSetupAsync() is not { } name)
            return;
        if (await Pickers.SaveFileAsync("Save setup", $"{name}.{ProfileBundle.Extension}", ProfileBundle.Extension) is not { } path)
            return;
        var profile = ProfileStore.Capture(_services.Game.Settings, name, null, await AutoexecToShareAsync(), _services.Mods.Loaded ? CurrentMods() : null);
        var mods = HandMadeMods();
        try
        {
            await Task.Run(() => ProfileBundle.Write(path, profile, mods));
            _main.Notify(mods.Count == 0
                ? $"Saved {Path.GetFileName(path)}."
                : $"Saved {Path.GetFileName(path)} with the files of {mods.Count} mod{(mods.Count == 1 ? "" : "s")} that aren't on GameBanana.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _main.AlertAsync("Couldn't save the setup", ex.Message);
        }
    }

    private Task<string?> NameSetupAsync() =>
        _main.PromptAsync("Share your setup", "Give it a name the other person will see. It carries your settings, binds and launch options.", "My setup", "Continue");

    /// <summary>The player's own autoexec lines, only if they agree to hand them over: people keep passwords in there.</summary>
    private async Task<string?> AutoexecToShareAsync()
    {
        if (ReadUserAutoexec() is not { } text || string.IsNullOrWhiteSpace(text))
            return null;
        return await _main.ConfirmAsync("Share your autoexec lines?",
            "Your autoexec.cfg has lines you wrote yourself. Whoever gets this setup can read them, so leave them out if they hold a password (rcon_password, a server password) or anything else private.",
            "Share them", "Leave them out")
            ? text
            : null;
    }

    private Task<bool> ConfirmAutoexecAsync(string text) => _main.ConfirmAsync("Autoexec lines",
        $"This setup carries its own autoexec.cfg lines. {Short} runs them as console commands every time it starts, so only take them from someone you trust.\n\n"
        + Shown(text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList(), 12, "\n")
        + "\n\nReplace the lines you wrote with these?",
        "Replace mine", "Keep mine");

    /// <summary>The first few entries of text that came from someone else, without the characters that could hide or reorder what is shown.</summary>
    private static string Shown(IReadOnlyList<string> entries, int most, string separator)
    {
        static string Plain(string text)
        {
            var plain = new string(text.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray());
            return plain.Length > 100 ? plain[..100] + "…" : plain;
        }
        var shown = string.Join(separator, entries.Take(most).Select(Plain));
        return entries.Count > most ? $"{shown}{separator}… and {entries.Count - most} more" : shown;
    }

    [RelayCommand]
    private async Task PasteSetupCodeAsync()
    {
        var code = await _main.PromptAsync("Import a setup code", "Paste the code someone shared. It starts with " + ShareCode.Prefix, confirm: "Next");
        if (string.IsNullOrWhiteSpace(code))
            return;
        try
        {
            await ImportSetupAsync(ShareCode.Decode(code), [], null);
        }
        catch (InvalidDataException ex)
        {
            await _main.AlertAsync("That code can't be used", ex.Message);
        }
    }

    [RelayCommand]
    private async Task OpenSetupFileAsync()
    {
        var files = await Pickers.OpenFilesAsync("Import a setup", false, "Setups and profiles", "*." + ProfileBundle.Extension, "*.json");
        if (files.Count == 0)
            return;
        try
        {
            if (files[0].EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                await ImportSetupAsync(ProfileStore.Read(files[0]), [], null);
                return;
            }
            var contents = await Task.Run(() => ProfileBundle.Open(files[0], _services.Paths.ImportStagingDir));
            await ImportSetupAsync(contents.Import, contents.Mods, contents.Staging);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            await _main.AlertAsync("That file can't be used", ex.Message);
        }
    }

    private async Task ImportSetupAsync(ProfileImport import, IReadOnlyList<BundledMod> bundled, string? staging)
    {
        try
        {
            var profile = import.Profile;
            if (profile.MadeFor != _services.GameInfo)
                profile.Mods = null;
            var wanted = profile.Mods is { } m
                ? m.Addons.Select(n => (Name: n, Kind: ModKind.Addon)).Concat(m.Particles.Select(n => (Name: n, Kind: ModKind.Particle))).ToList()
                : [];
            var plan = new List<SharedModPlan>();
            var have = 0;
            var missing = new List<string>();
            foreach (var (name, kind) in wanted)
            {
                var source = profile.Mods!.Sources?.GetValueOrDefault(name);
                if (_services.Mods.Has(name, kind, source))
                    have++;
                else if (bundled.FirstOrDefault(b => b.Name == name && b.Kind == kind) is { } packed)
                    plan.Add(new SharedModPlan(name, kind, null, packed));
                else if (source is not null)
                    plan.Add(new SharedModPlan(name, kind, source, null));
                else
                    missing.Add(name);
            }

            if (!await _main.ConfirmAsync($"Import \"{profile.Name}\"", Describe(import, have, plan, missing), "Import and apply"))
                return;
            profile.Game = _services.GameInfo == Eternal.Core.Games.GameCatalog.Tf2 ? null : _services.GameInfo.Id;

            _services.Profiles.Save(profile);
            RefreshProfiles();
            SelectedProfile = Profiles.FirstOrDefault(p => p.Id == profile.Id);
            ProfileStore.Apply(profile, _services.Game.Settings);
            if (profile.UserAutoexec is { } text && await ConfirmAutoexecAsync(text))
                _services.Game.SetPendingUserContent(text);

            var problems = await _main.Mods.InstallSharedAsync(plan, CancellationToken.None);
            var left = profile.Mods is { } mods ? _services.Mods.ApplySelection(mods) : [];
            if (problems.Count > 0)
                await _main.AlertAsync("Imported, with some mods left out", string.Join("\n", problems.Select(p => "• " + p)));
            _main.Notify(left.Count == 0
                ? $"{profile.Name} is in place. Press Play to try it."
                : $"{profile.Name} is in place, without {string.Join(", ", left)}.");
        }
        finally
        {
            if (staging is not null)
                ArchiveIngest.TryDelete(staging);
        }
    }

    private string Describe(ProfileImport import, int have, IReadOnlyList<SharedModPlan> plan, IReadOnlyList<string> missing)
    {
        var p = import.Profile;
        var lines = new List<string>();
        if (p.MadeFor != _services.GameInfo)
            lines.Add($"This setup was made for {p.MadeFor.Name}. Only the settings {_services.GameInfo.Name} also has are applied, and its mods are left out.");
        lines.Add($"Replaces your settings with its {p.Values.Count} settings" + (p.Binds is { Count: > 0 } b ? $", {b.Count} binds" : "") + (string.IsNullOrWhiteSpace(p.ExtraLaunchArgs) ? "." : " and its launch options."));
        if (!string.IsNullOrWhiteSpace(p.ExtraLaunchArgs))
            lines.Add($"Launch options: {Shown([p.ExtraLaunchArgs], 1, "")}");
        if (p.Binds is { Count: > 0 } binds)
            lines.Add($"Binds: {Shown(binds.Select(k => $"{k.Key} = {k.Command}").ToList(), 8, "; ")}");
        if (p.UserAutoexec is not null)
            lines.Add("It also carries autoexec.cfg lines. You're shown those next and can keep your own.");
        if (import.UnknownSettings > 0)
            lines.Add($"{import.UnknownSettings} setting(s) from another version are skipped.");
        if (p.Mods is not null)
        {
            var downloads = plan.Count(x => x.Source is not null);
            var packed = plan.Count(x => x.Bundled is not null);
            var parts = new List<string>();
            if (have > 0)
                parts.Add($"{have} already in your library");
            if (downloads > 0)
                parts.Add($"{downloads} to download from GameBanana");
            if (packed > 0)
                parts.Add($"{packed} packed in the file");
            lines.Add(parts.Count == 0 ? "Turns all your mods off." : $"Mods: {string.Join(", ", parts)}. Everything else gets switched off.");
            if (missing.Count > 0)
                lines.Add($"Not included, so left out: {string.Join(", ", missing.Take(6))}{(missing.Count > 6 ? $" and {missing.Count - 6} more" : "")}.");
        }
        return string.Join("\n\n", lines);
    }

    private List<BundledMod> HandMadeMods()
    {
        var sources = _services.Mods.EnabledSources();
        return _services.Mods.State.Order
            .Where(e => e.Enabled && !sources.ContainsKey(e.Name) && e.Name != Eternal.Core.Sounds.CustomSoundMod.Folder)
            .Select(e => new BundledMod(e.Name, e.Kind, Path.Combine(_services.Paths.EngineDataDir, "mods", e.Kind == ModKind.Particle ? "particles" : "addons", e.Name)))
            .Where(m => Directory.Exists(m.Folder))
            .ToList();
    }

    [RelayCommand]
    private async Task ResetAllAsync()
    {
        if (!await _main.ConfirmAsync("Reset everything", $"Put every setting back to {Short}'s default? Binds and your own launch options are kept.", "Reset", danger: true))
            return;
        _services.Game.Settings.Apply(new Dictionary<string, System.Text.Json.Nodes.JsonNode?>(), complete: true);
    }

    [RelayCommand]
    private async Task ImportAutoexecAsync()
    {
        if (_services.TfPath is null || AtomicFile.ReadAllTextOrNull(AutoexecFile.Locate(_services.TfPath)) is not { } text)
        {
            await _main.AlertAsync("Nothing to read", $"No autoexec.cfg was found in your {Short} folder.");
            return;
        }
        var values = ObservedSettings.ImportFromAutoexec(text);
        _services.Game.Settings.Apply(values.ToDictionary(kv => kv.Key, kv => (System.Text.Json.Nodes.JsonNode?)kv.Value));
        _main.Notify($"Read {values.Count} settings from autoexec.cfg.");
    }

    [RelayCommand]
    private async Task CopyLaunchLineAsync() => await Pickers.CopyAsync(LaunchLine);

    [RelayCommand]
    private void OpenCfgFolder()
    {
        if (_services.TfPath is { } tf)
            Shell.OpenFolder(Path.GetDirectoryName(AutoexecFile.Locate(tf)));
    }

    private string? ReadUserAutoexec() =>
        _services.TfPath is { } tf && AtomicFile.ReadAllBytesOrNull(AutoexecFile.Locate(tf)) is { } bytes
            ? AutoexecDocument.Parse(AutoexecFile.Decode(bytes)).UserContent
            : null;

    private ProfileMods CurrentMods()
    {
        var enabled = _services.Mods.State.Order.Where(e => e.Enabled && e.Name != Eternal.Core.Sounds.CustomSoundMod.Folder).ToList();
        var sources = _services.Mods.EnabledSources();
        return new ProfileMods
        {
            Addons = enabled.Where(e => e.Kind == ModKind.Addon).Select(e => e.Name).ToList(),
            Particles = enabled.Where(e => e.Kind == ModKind.Particle).Select(e => e.Name).ToList(),
            Sources = sources.Count > 0 ? sources : null,
        };
    }
}

public static class SourceKeys
{
    public static string? FromMouse(MouseButton button) => button switch
    {
        MouseButton.Left => "mouse1",
        MouseButton.Right => "mouse2",
        MouseButton.Middle => "mouse3",
        MouseButton.XButton1 => "mouse4",
        MouseButton.XButton2 => "mouse5",
        _ => null,
    };

    public static string? FromKey(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString().ToLowerInvariant(),
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        >= Key.F1 and <= Key.F12 => key.ToString().ToLowerInvariant(),
        Key.NumPad0 => "kp_ins",
        Key.NumPad1 => "kp_end",
        Key.NumPad2 => "kp_downarrow",
        Key.NumPad3 => "kp_pgdn",
        Key.NumPad4 => "kp_leftarrow",
        Key.NumPad5 => "kp_5",
        Key.NumPad6 => "kp_rightarrow",
        Key.NumPad7 => "kp_home",
        Key.NumPad8 => "kp_uparrow",
        Key.NumPad9 => "kp_pgup",
        Key.Multiply => "kp_multiply",
        Key.Add => "kp_plus",
        Key.Subtract => "kp_minus",
        Key.Divide => "kp_slash",
        Key.Decimal => "kp_del",
        Key.Enter => "enter",
        Key.Space => "space",
        Key.Back => "backspace",
        Key.Tab => "tab",
        Key.LeftShift => "shift",
        Key.RightShift => "rshift",
        Key.LeftCtrl => "ctrl",
        Key.RightCtrl => "rctrl",
        Key.LeftAlt => "alt",
        Key.RightAlt => "ralt",
        Key.Insert => "ins",
        Key.Delete => "del",
        Key.Home => "home",
        Key.End => "end",
        Key.PageUp => "pgup",
        Key.PageDown => "pgdn",
        Key.Up => "uparrow",
        Key.Down => "downarrow",
        Key.Left => "leftarrow",
        Key.Right => "rightarrow",
        Key.OemSemicolon => "semicolon",
        Key.OemQuotes => "'",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemTilde => "`",
        Key.OemBackslash or Key.OemPipe => "\\",
        _ => null,
    };
}
