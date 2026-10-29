using System.Collections.ObjectModel;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Eternal.App.Services;
using Eternal.Core.Engine;
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
                if (!row.Applies)
                    continue;
                vm.Rows.Add(row);
                _rows.Add(row);
            }
            Categories.Add(vm);
        }
        Categories.Add(new SettingCategoryViewModel("binds", "Binds", "Keys that run commands. Written after your settings, so they win."));
        Categories.Add(new SettingCategoryViewModel("launch", "Launch Options", "Your own launch options and the full line TF2 starts with."));
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
    public bool ShowsSettings => !IsSearching && SelectedCategory is { Id: not ("binds" or "launch") };
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
            return tokens.Count == 0 ? "(no options — TF2 starts with its own settings)" : LaunchOptions.Join(tokens);
        }
    }

    public string AutoexecPreview => string.Join(Environment.NewLine, ManagedBlock.Build(_services.Game.Settings));

    public string AutoexecPath => _services.TfPath is { } tf ? AutoexecFile.Locate(tf) : "TF2 folder not set";

    partial void OnSearchChanged(string value)
    {
        var query = value.Trim();
        SearchResults = query.Length == 0 ? [] : _rows.Where(r => r.Matches(query)).ToArray();
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(ShowsSettings));
        OnPropertyChanged(nameof(ShowsBinds));
        OnPropertyChanged(nameof(ShowsLaunch));
    }

    partial void OnSelectedCategoryChanged(SettingCategoryViewModel? value)
    {
        OnPropertyChanged(nameof(ShowsSettings));
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
            (true, AutoexecStatus.NoGameFolder) => "Saved. Set your TF2 folder in the Launcher tab to apply it to the game.",
            _ => $"Saved and written to {Path.GetFileName(Path.GetDirectoryName(report.Autoexec.Path ?? "") ?? "")}/autoexec.cfg",
        };
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

    // A bind edit comes back through Settings.Changed after it's saved. Rebuilding
    // the rows then would replace the box the player is typing in.
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

    /// <summary>Called by the view while a bind waits for a key. Returns true when handled.</summary>
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

        ProfileStore.Apply(profile, _services.Game.Settings);
        if (profile.UserAutoexec is { } text && await _main.ConfirmAsync("Autoexec lines",
                "This profile carries its own autoexec.cfg lines (outside the launcher's block). Replace yours with them?",
                "Replace", "Keep mine"))
            _services.Game.SetPendingUserContent(text);
        var missing = profile.Mods is { } mods ? _services.Mods.ApplySelection(mods.Addons, mods.Particles) : [];
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
    private async Task ImportProfileAsync()
    {
        var files = await Pickers.OpenFilesAsync("Import profile", false, "Profiles", "*.json");
        if (files.Count == 0)
            return;
        try
        {
            var result = _services.Profiles.Import(files[0]);
            RefreshProfiles();
            SelectedProfile = Profiles.FirstOrDefault(p => p.Id == result.Profile.Id);
            var note = result.UnknownSettings > 0 ? $" {result.UnknownSettings} setting(s) from another version were skipped." : "";
            _main.Notify($"Imported {result.Profile.Name}.{note}");
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or IOException)
        {
            await _main.AlertAsync("Not a profile", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ExportProfileAsync()
    {
        if (SelectedProfile is not { } profile)
            return;
        if (await Pickers.SaveFileAsync("Export profile", $"{profile.Name}.json", "json") is { } path)
            _services.Profiles.Export(profile, path);
    }

    [RelayCommand]
    private async Task ResetAllAsync()
    {
        if (!await _main.ConfirmAsync("Reset everything", "Put every setting back to TF2's default? Binds and your own launch options are kept.", "Reset", danger: true))
            return;
        _services.Game.Settings.Apply(new Dictionary<string, System.Text.Json.Nodes.JsonNode?>(), complete: true);
    }

    [RelayCommand]
    private async Task ImportAutoexecAsync()
    {
        if (_services.TfPath is null || AtomicFile.ReadAllTextOrNull(AutoexecFile.Locate(_services.TfPath)) is not { } text)
        {
            await _main.AlertAsync("Nothing to read", "No autoexec.cfg was found in your TF2 folder.");
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
        _services.TfPath is { } tf && AtomicFile.ReadAllTextOrNull(AutoexecFile.Locate(tf)) is { } text
            ? AutoexecDocument.Parse(text).UserContent
            : null;

    private ProfileMods CurrentMods()
    {
        var enabled = _services.Mods.State.Order.Where(e => e.Enabled).ToList();
        return new ProfileMods
        {
            Addons = enabled.Where(e => e.Kind == ModKind.Addon).Select(e => e.Name).ToList(),
            Particles = enabled.Where(e => e.Kind == ModKind.Particle).Select(e => e.Name).ToList(),
        };
    }
}

/// <summary>Avalonia keys to Source engine key names.</summary>
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
