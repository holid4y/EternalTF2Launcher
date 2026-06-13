using System.ComponentModel;

namespace LauncherTF2.Models.Settings;

/// <summary>
/// String-valued setting rendered as a text box. Used for cvars that take
/// a quoted path or name, e.g. <c>ds_dir "demos"</c>.
/// </summary>
public sealed class TextSetting : SettingItem, IDisposable
{
    private readonly SettingsModel _model;
    private readonly string _modelProperty;
    private readonly Func<string> _get;
    private readonly Action<string> _set;

    public TextSetting(SettingsModel model, string modelProperty, Func<string> get, Action<string> set)
    {
        _model = model;
        _modelProperty = modelProperty;
        PropertyName = modelProperty;
        _get = get;
        _set = set;
        _model.PropertyChanged += OnModelPropertyChanged;
    }

    public string Text
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
        if (string.IsNullOrEmpty(Cvar)) return [];
        var v = _get();
        if (string.IsNullOrEmpty(v)) return [];
        return [$"{Cvar} \"{v}\""];
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == _modelProperty) OnPropertyChanged(nameof(Text));
        RaiseIfDependency(e.PropertyName);
    }

    public void Dispose() => _model.PropertyChanged -= OnModelPropertyChanged;
}
