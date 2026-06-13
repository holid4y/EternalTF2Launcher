using System.ComponentModel;

namespace LauncherTF2.Models.Settings;

/// <summary>
/// Boolean toggle bound to a single <see cref="SettingsModel"/> property.
/// Maps to a <c>cvar 0/1</c> line (or a custom emitter when the cvar value
/// inverts the toggle, e.g. <c>mat_disable_bloom</c>).
/// </summary>
public sealed class ToggleSetting : SettingItem, IDisposable
{
    private readonly SettingsModel _model;
    private readonly string _modelProperty;
    private readonly Func<bool> _get;
    private readonly Action<bool> _set;

    /// <summary>
    /// When set, this delegate produces the cvar lines instead of the default
    /// <c>{Cvar} 0/1</c> format. Useful for cvars whose semantics invert the
    /// toggle (<c>mat_disable_bloom</c>) or that need to emit multiple lines.
    /// </summary>
    public Func<bool, IEnumerable<string>>? CustomEmitter { get; init; }

    /// <summary>When true, only emit a line when the toggle is on (default: always emit).</summary>
    public bool EmitOnlyWhenOn { get; init; }

    public ToggleSetting(SettingsModel model, string modelProperty, Func<bool> get, Action<bool> set)
    {
        _model = model;
        _modelProperty = modelProperty;
        PropertyName = modelProperty;
        _get = get;
        _set = set;
        _model.PropertyChanged += OnModelPropertyChanged;
    }

    public bool IsOn
    {
        get => _get();
        set
        {
            if (_get() == value) return;
            _set(value);
            OnPropertyChanged();
        }
    }

    public override IEnumerable<string> EmitCvarLines()
    {
        var on = _get();
        if (CustomEmitter != null) return CustomEmitter(on);
        if (EmitOnlyWhenOn && !on) return [];
        if (string.IsNullOrEmpty(Cvar)) return [];
        return [$"{Cvar} {(on ? "1" : "0")}"];
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == _modelProperty) OnPropertyChanged(nameof(IsOn));
        RaiseIfDependency(e.PropertyName);
    }

    public void Dispose() => _model.PropertyChanged -= OnModelPropertyChanged;
}
