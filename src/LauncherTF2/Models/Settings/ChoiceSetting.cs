using System.ComponentModel;

namespace LauncherTF2.Models.Settings;

/// <summary>
/// One named option in a <see cref="ChoiceSetting"/>. <see cref="Value"/> is
/// boxed so the same option type can hold int / string / double cvar values.
///
/// ToString is explicitly the <see cref="Label"/> so when a control falls
/// back to the default ContentPresenter (e.g. a ComboBox's selection box
/// inside a custom template, where DisplayMemberPath isn't honoured), the
/// user-facing label is what renders — not the auto-generated record string.
/// </summary>
public sealed record ChoiceOption(string Label, object Value, string? Description = null)
{
    public override string ToString() => Label;
}

/// <summary>
/// Pick-one setting rendered as a dropdown or segmented buttons. The
/// underlying model property can be any primitive; the selected
/// <see cref="ChoiceOption.Value"/> is forwarded through the setter delegate.
/// </summary>
public sealed class ChoiceSetting : SettingItem, IDisposable
{
    private readonly SettingsModel _model;
    private readonly string _modelProperty;
    private readonly Func<object?> _get;
    private readonly Action<object?> _set;

    public IReadOnlyList<ChoiceOption> Options { get; init; } = [];

    /// <summary>Optional formatter for the cvar line. Defaults to "{Cvar} {value}".</summary>
    public Func<object?, IEnumerable<string>>? CustomEmitter { get; init; }

    public ChoiceSetting(SettingsModel model, string modelProperty, Func<object?> get, Action<object?> set)
    {
        _model = model;
        _modelProperty = modelProperty;
        PropertyName = modelProperty;
        _get = get;
        _set = set;
        _model.PropertyChanged += OnModelPropertyChanged;
    }

    public ChoiceOption? Selected
    {
        get
        {
            var current = _get();
            return Options.FirstOrDefault(o => Equals(o.Value, current));
        }
        set
        {
            if (value == null) return;
            if (Equals(_get(), value.Value)) return;
            _set(value.Value);
            OnPropertyChanged();
        }
    }

    public override IEnumerable<string> EmitCvarLines()
    {
        if (string.IsNullOrEmpty(Cvar)) return [];
        var v = _get();
        if (CustomEmitter != null) return CustomEmitter(v);
        if (v == null) return [];
        return [$"{Cvar} {Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)}"];
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == _modelProperty) OnPropertyChanged(nameof(Selected));
        RaiseIfDependency(e.PropertyName);
    }

    public void Dispose() => _model.PropertyChanged -= OnModelPropertyChanged;
}
