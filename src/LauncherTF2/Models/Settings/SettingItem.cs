using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LauncherTF2.Models.Settings;

public abstract class SettingItem : INotifyPropertyChanged
{
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string PropertyName { get; protected set; } = "";
    public string Cvar { get; init; } = "";
    public bool IsAdvanced { get; init; }
    // True when the cvar is blocked by sv_pure on Casual/pub servers.
    public bool NotCasualCompatible { get; init; }
    public bool RequiresCheats { get; init; }
    public bool RestartRequired { get; init; }
    public string[]? SearchKeywords { get; init; }
    // Override for cvars whose semantics invert the model property (e.g. mat_disable_bloom).
    public Action<SettingsModel, string>? CvarParser { get; init; }
    // Property names on SettingsModel that affect IsEnabled for this item.
    public IReadOnlyList<string>? DependsOn { get; init; }
    public Func<bool>? IsEnabledPredicate { get; init; }

    public bool IsEnabled => IsEnabledPredicate?.Invoke() ?? true;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void RaiseIfDependency(string? propertyName)
    {
        if (DependsOn != null && propertyName != null && DependsOn.Contains(propertyName))
            OnPropertyChanged(nameof(IsEnabled));
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public virtual IEnumerable<string> EmitCvarLines() => [];

    public object? GetValue(SettingsModel source)
    {
        if (string.IsNullOrEmpty(PropertyName)) return null;
        var prop = typeof(SettingsModel).GetProperty(PropertyName);
        return prop?.GetValue(source);
    }

    public void SetValue(SettingsModel target, object? value)
    {
        if (string.IsNullOrEmpty(PropertyName)) return;
        var prop = typeof(SettingsModel).GetProperty(PropertyName);
        if (prop == null) return;

        if (value is System.Text.Json.JsonElement element)
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize(element.GetRawText(), prop.PropertyType);
            prop.SetValue(target, parsed);
        }
        else if (value != null)
        {
            var converted = Convert.ChangeType(value, prop.PropertyType, System.Globalization.CultureInfo.InvariantCulture);
            prop.SetValue(target, converted);
        }
    }
}
