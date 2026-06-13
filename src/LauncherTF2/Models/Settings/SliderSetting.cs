using System.ComponentModel;
using System.Globalization;

namespace LauncherTF2.Models.Settings;

/// <summary>
/// Numeric setting rendered as a slider (with optional readout). Internally
/// stores the value as <see cref="double"/> for slider compatibility — at
/// autoexec-write time it formats the value with invariant culture so the
/// cfg never picks up the user's decimal separator.
/// </summary>
public sealed class SliderSetting : SettingItem, IDisposable
{
    private readonly SettingsModel _model;
    private readonly string _modelProperty;
    private readonly Func<double> _get;
    private readonly Action<double> _set;

    public double Min { get; init; }
    public double Max { get; init; } = 100;
    public double Step { get; init; } = 1;
    /// <summary>StringFormat for the readout label (e.g. "N0", "N2", "F6").</summary>
    public string DisplayFormat { get; init; } = "N0";
    /// <summary>When true, slider emits integers and DisplayFormat defaults to "N0".</summary>
    public bool IsInteger { get; init; } = true;
    /// <summary>When >0, only emit the cvar when the value differs from this default.</summary>
    public double? DefaultValue { get; init; }

    public SliderSetting(SettingsModel model, string modelProperty, Func<double> get, Action<double> set)
    {
        _model = model;
        _modelProperty = modelProperty;
        PropertyName = modelProperty;
        _get = get;
        _set = set;
        _model.PropertyChanged += OnModelPropertyChanged;
    }

    public double Value
    {
        get => _get();
        set
        {
            var clamped = Math.Max(Min, Math.Min(Max, value));
            if (IsInteger) clamped = Math.Round(clamped);
            if (Math.Abs(_get() - clamped) < 1e-9) return;
            _set(clamped);
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayValue));
        }
    }

    /// <summary>
    /// User-facing readout. Always formatted with InvariantCulture so the
    /// decimal separator is consistent regardless of the user's regional
    /// settings — keeps the UI looking professional on pt-BR / de-DE / etc.
    /// where the default culture uses a comma.
    /// </summary>
    public string DisplayValue => Value.ToString(DisplayFormat, CultureInfo.InvariantCulture);

    public override IEnumerable<string> EmitCvarLines()
    {
        if (string.IsNullOrEmpty(Cvar)) return [];
        var v = _get();
        if (DefaultValue.HasValue && Math.Abs(v - DefaultValue.Value) < 1e-9) return [];
        var formatted = IsInteger
            ? ((int)v).ToString(CultureInfo.InvariantCulture)
            : v.ToString(CultureInfo.InvariantCulture);
        return [$"{Cvar} {formatted}"];
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == _modelProperty)
        {
            OnPropertyChanged(nameof(Value));
            OnPropertyChanged(nameof(DisplayValue));
        }
        RaiseIfDependency(e.PropertyName);
    }

    public void Dispose() => _model.PropertyChanged -= OnModelPropertyChanged;
}
