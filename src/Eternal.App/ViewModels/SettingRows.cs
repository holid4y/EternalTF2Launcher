using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Eternal.Core.Settings;

namespace Eternal.App.ViewModels;

/// <summary>One row in Config: plain title and explanation, impact tags, a control and a reset.</summary>
public abstract partial class SettingRowViewModel : ObservableObject
{
    protected SettingRowViewModel(SettingDefinition definition, GameSettings settings)
    {
        Definition = definition;
        Settings = settings;
        Tags = BuildTags(definition);
    }

    public SettingDefinition Definition { get; }
    protected GameSettings Settings { get; }

    public string Title => Definition.Title;
    public string Summary => Definition.Summary;
    public string? Cvar => Definition.Cvar;
    public IReadOnlyList<string> Tags { get; }
    public bool IsTouched => Settings.IsTouched(Definition);
    public bool IsEnabled => Settings.IsEnabled(Definition);
    public bool IsAdvanced => Definition.IsAdvanced;
    public bool Applies => !Definition.Tags.HasFlag(SettingTags.WindowsOnly) || OperatingSystem.IsWindows();

    public string Origin => IsTouched ? "Set by Eternal" : Settings.Observed.ContainsKey(Definition.Id) ? "Read from TF2" : "TF2 default";

    private static List<string> BuildTags(SettingDefinition d)
    {
        var tags = new List<string>();
        if (d.Tags.HasFlag(SettingTags.LaunchOption))
            tags.Add("Launch option");
        if (d.Tags.HasFlag(SettingTags.Performance))
            tags.Add("FPS");
        if (d.Tags.HasFlag(SettingTags.Competitive))
            tags.Add("Competitive");
        if (d.Tags.HasFlag(SettingTags.BlockedOnValveServers))
            tags.Add("Blocked on Valve servers");
        return tags;
    }

    [RelayCommand]
    private void Reset() => Settings.ResetToDefault(Definition);

    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
    }

    public bool Matches(string query) =>
        Title.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Summary.Contains(query, StringComparison.OrdinalIgnoreCase)
        || (Cvar?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
        || Definition.Keywords.Any(k => k.Contains(query, StringComparison.OrdinalIgnoreCase));
}

public sealed class ToggleRowViewModel(ToggleSetting definition, GameSettings settings) : SettingRowViewModel(definition, settings)
{
    private ToggleSetting Toggle => (ToggleSetting)Definition;

    /// <summary>What the switch shows: always "feature on", even for inverted legacy ids.</summary>
    public bool Value
    {
        get => Settings.Get(Toggle) ^ Toggle.Inverted;
        set
        {
            if (value != Value)
                Settings.Set(Toggle, value ^ Toggle.Inverted);
        }
    }
}

public sealed class SliderRowViewModel(SliderSetting definition, GameSettings settings) : SettingRowViewModel(definition, settings)
{
    private SliderSetting Slider => (SliderSetting)Definition;

    public double Minimum => Slider.Min;
    public double Maximum => Slider.Max;
    public double Step => Slider.Step;
    public bool UsesBox => Slider.Unit is "px" or "Hz" || Slider.Max - Slider.Min > 2000;
    public string Display => Slider.Display(Value);
    public decimal Increment => (decimal)Slider.Step;
    public string FormatString => Slider.Decimals == 0 ? "0" : "0." + new string('#', Slider.Decimals);

    public double Value
    {
        get => Settings.Get(Slider);
        set
        {
            var clamped = Slider.Clamp(value);
            if (Math.Abs(clamped - Value) > 1e-9)
                Settings.Set(Slider, clamped);
        }
    }

    public decimal? BoxValue
    {
        get => (decimal)Value;
        set
        {
            if (value is { } v)
                Value = (double)v;
        }
    }
}

public sealed class ChoiceRowViewModel(ChoiceSetting definition, GameSettings settings) : SettingRowViewModel(definition, settings)
{
    private ChoiceSetting Choice => (ChoiceSetting)Definition;

    public IReadOnlyList<ChoiceOption> Options => Choice.Options;

    public ChoiceOption? Selected
    {
        get => Settings.Get(Choice);
        set
        {
            if (value is not null && value.Value != Selected?.Value)
                Settings.Set(Choice, value);
        }
    }

    public string? Hint => Selected?.Hint;
}

public sealed class TextRowViewModel(TextSetting definition, GameSettings settings) : SettingRowViewModel(definition, settings)
{
    private TextSetting Text => (TextSetting)Definition;

    public string Value
    {
        get => Settings.Get(Text);
        set
        {
            if (value != Value)
                Settings.Set(Text, value);
        }
    }
}

public sealed partial class SettingCategoryViewModel(string id, string title, string summary) : ObservableObject
{
    public string Id { get; } = id;
    public string Title { get; } = title;
    public string Summary { get; } = summary;
    public List<SettingRowViewModel> Rows { get; } = [];

    [ObservableProperty] private IReadOnlyList<SettingRowViewModel> _visibleRows = [];
    [ObservableProperty] private int _changedCount;

    public override string ToString() => Title;
}
