using System.Text.Json.Nodes;

namespace Eternal.Core.Settings;

public sealed class KeyBind
{
    public string Key { get; set; } = "";
    public string Command { get; set; } = "";
    public string? Label { get; set; }

    public KeyBind Clone() => new() { Key = Key, Command = Command, Label = Label };
}

/// <summary>
/// Persisted TF2 configuration. A setting is "touched" when it has an entry in
/// <see cref="Values"/>: only touched settings are written to autoexec.cfg or
/// the launch line, so anything the player never changed stays under TF2's control.
/// </summary>
public sealed class GameConfig
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public Dictionary<string, JsonNode?> Values { get; set; } = new(StringComparer.Ordinal);
    public List<KeyBind> Binds { get; set; } = [];

    /// <summary>The player's own launch options, minus anything the launcher manages.</summary>
    public string ExtraLaunchArgs { get; set; } = "";

    public GameConfig Clone() => new()
    {
        Version = Version,
        Values = Values.ToDictionary(kv => kv.Key, kv => kv.Value?.DeepClone(), StringComparer.Ordinal),
        Binds = Binds.Select(b => b.Clone()).ToList(),
        ExtraLaunchArgs = ExtraLaunchArgs,
    };
}

/// <summary>
/// Effective value of each setting: the stored value when touched, else what
/// TF2's own config files say, else the default. Thread-safe; every change goes
/// through here so a save can take a consistent snapshot.
/// </summary>
public sealed class GameSettings
{
    public const string AllChanged = "*";

    private readonly object _gate = new();
    private readonly GameConfig _config;
    private readonly Dictionary<string, JsonNode> _observed = new(StringComparer.Ordinal);

    public GameSettings(GameConfig config, IReadOnlyDictionary<string, JsonNode>? observed = null)
    {
        _config = config;
        if (observed is not null)
            UpdateObserved(observed);
    }

    /// <summary>Raised with the setting id, or <see cref="AllChanged"/> for bulk changes.</summary>
    public event Action<string>? Changed;

    public GameConfig Snapshot()
    {
        lock (_gate)
            return _config.Clone();
    }

    public bool IsTouched(SettingDefinition setting)
    {
        lock (_gate)
            return _config.Values.TryGetValue(setting.Id, out var v) && v is not null;
    }

    public JsonNode Get(SettingDefinition setting)
    {
        lock (_gate)
        {
            if (_config.Values.TryGetValue(setting.Id, out var stored) && stored is not null)
                return setting.Coerce(stored);
            return _observed.TryGetValue(setting.Id, out var observed) ? setting.Coerce(observed) : setting.DefaultNode;
        }
    }

    public bool Get(ToggleSetting setting) => setting.Read(Get((SettingDefinition)setting));
    public double Get(SliderSetting setting) => setting.Read(Get((SettingDefinition)setting));
    public ChoiceOption Get(ChoiceSetting setting) => setting.Read(Get((SettingDefinition)setting));
    public string Get(TextSetting setting) => setting.Read(Get((SettingDefinition)setting));

    public bool IsEnabled(SettingDefinition setting) =>
        setting.DependsOn is null || SettingsCatalog.Find(setting.DependsOn) switch
        {
            ToggleSetting toggle => Get(toggle),
            ChoiceSetting choice => Get(choice).Value != "0",
            _ => true,
        };

    public bool IsDefault(SettingDefinition setting) => setting.ValueEquals(Get(setting), setting.DefaultNode);

    public void Set(SettingDefinition setting, JsonNode? value)
    {
        var coerced = setting.Coerce(value);
        lock (_gate)
        {
            if (_config.Values.TryGetValue(setting.Id, out var current) && current is not null && JsonNode.DeepEquals(current, coerced))
                return;
            _config.Values[setting.Id] = coerced;
        }
        Changed?.Invoke(setting.Id);
    }

    public void Set(ToggleSetting setting, bool value) => Set(setting, JsonValue.Create(value));
    public void Set(SliderSetting setting, double value) => Set(setting, JsonValue.Create(value));
    public void Set(ChoiceSetting setting, ChoiceOption option) => Set(setting, JsonValue.Create(option.Value));
    public void Set(TextSetting setting, string value) => Set(setting, JsonValue.Create(value));

    public void ResetToDefault(SettingDefinition setting) => Set(setting, setting.DefaultNode);

    public IReadOnlyList<KeyBind> Binds
    {
        get
        {
            lock (_gate)
                return _config.Binds.Select(b => b.Clone()).ToList();
        }
    }

    public void SetBinds(IEnumerable<KeyBind> binds)
    {
        lock (_gate)
            _config.Binds = binds.Select(b => b.Clone()).ToList();
        Changed?.Invoke(AllChanged);
    }

    public string ExtraLaunchArgs
    {
        get
        {
            lock (_gate)
                return _config.ExtraLaunchArgs;
        }
        set
        {
            lock (_gate)
            {
                if (_config.ExtraLaunchArgs == value)
                    return;
                _config.ExtraLaunchArgs = value ?? "";
            }
            Changed?.Invoke(AllChanged);
        }
    }

    /// <summary>
    /// Applies many values with one change notification. With
    /// <paramref name="complete"/>, settings missing from <paramref name="values"/>
    /// are reset too: cvars to their default (still written, so values TF2
    /// archived in config.cfg are overridden) and launch options to untouched.
    /// </summary>
    public void Apply(IReadOnlyDictionary<string, JsonNode?> values, bool complete = false)
    {
        lock (_gate)
        {
            if (complete)
            {
                foreach (var setting in SettingsCatalog.All)
                {
                    if (setting.IsLaunchOption)
                        _config.Values.Remove(setting.Id);
                    else
                        _config.Values[setting.Id] = setting.DefaultNode;
                }
            }
            foreach (var (id, value) in values)
            {
                if (SettingsCatalog.Find(id) is { } setting)
                    _config.Values[id] = setting.Coerce(value);
            }
        }
        Changed?.Invoke(AllChanged);
    }

    /// <summary>
    /// Everything needed to restore the current state later: the effective
    /// value of every cvar plus the launch options the player actually set.
    /// </summary>
    public Dictionary<string, JsonNode?> CaptureValues()
    {
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var setting in SettingsCatalog.All)
        {
            if (!setting.IsLaunchOption || IsTouched(setting))
                values[setting.Id] = Get(setting).DeepClone();
        }
        return values;
    }

    public void Replace(GameConfig config)
    {
        lock (_gate)
        {
            _config.Values = config.Values.ToDictionary(kv => kv.Key, kv => kv.Value?.DeepClone(), StringComparer.Ordinal);
            _config.Binds = config.Binds.Select(b => b.Clone()).ToList();
            _config.ExtraLaunchArgs = config.ExtraLaunchArgs;
        }
        Changed?.Invoke(AllChanged);
    }

    public IReadOnlyDictionary<string, JsonNode> Observed
    {
        get
        {
            lock (_gate)
                return new Dictionary<string, JsonNode>(_observed, StringComparer.Ordinal);
        }
    }

    public void UpdateObserved(IReadOnlyDictionary<string, JsonNode> observed)
    {
        lock (_gate)
        {
            _observed.Clear();
            foreach (var (key, value) in observed)
                _observed[key] = value;
        }
    }
}
