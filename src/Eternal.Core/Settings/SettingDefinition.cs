using System.Globalization;
using System.Text.Json.Nodes;

namespace Eternal.Core.Settings;

[Flags]
public enum SettingTags
{
    None = 0,
    Performance = 1 << 0,
    Visuals = 1 << 1,
    Competitive = 1 << 2,
    BlockedOnValveServers = 1 << 3,
    LaunchOption = 1 << 4,
    Advanced = 1 << 5,
    WindowsOnly = 1 << 6,
}

public enum SettingKind { Toggle, Slider, Choice, Text }

/// <summary>
/// How one TF2 setting is shown, validated, written and read back. Holds no
/// state; values live in <see cref="GameConfig"/>.
/// </summary>
public abstract class SettingDefinition
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public string Category { get; internal set; } = "";
    public string? Cvar { get; init; }
    public SettingTags Tags { get; init; }
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>Id of a toggle that has to be on for this setting to matter.</summary>
    public string? DependsOn { get; init; }

    public abstract SettingKind Kind { get; }
    public bool IsLaunchOption => Tags.HasFlag(SettingTags.LaunchOption);
    public bool IsAdvanced => Tags.HasFlag(SettingTags.Advanced);

    public abstract JsonNode DefaultNode { get; }

    /// <summary>Canonical, validated form of a stored value; falls back to the default.</summary>
    public abstract JsonNode Coerce(JsonNode? node);

    /// <summary>Autoexec lines, or launch-line tokens for launch options.</summary>
    public abstract IEnumerable<string> Emit(JsonNode value);

    /// <summary>Value from the text a cfg file assigns to <see cref="Cvar"/>, or null if not understood.</summary>
    public abstract JsonNode? Parse(string raw);

    public bool ValueEquals(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(Coerce(a), Coerce(b));

    private protected static bool TryNumber(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue v)
            return false;
        if (v.TryGetValue(out double d)) { value = d; return true; }
        if (v.TryGetValue(out long l)) { value = l; return true; }
        if (v.TryGetValue(out int i)) { value = i; return true; }
        if (v.TryGetValue(out bool b)) { value = b ? 1 : 0; return true; }
        return v.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}

public sealed class ToggleSetting : SettingDefinition
{
    public bool Default { get; init; }

    /// <summary>
    /// Stored value is the opposite of what the switch shows. Lets every switch
    /// read as "feature on" while keeping legacy ids such as RemoveGibs.
    /// </summary>
    public bool Inverted { get; init; }

    public Func<bool, IEnumerable<string>>? Emitter { get; init; }
    public Func<string, bool?>? Parser { get; init; }

    public override SettingKind Kind => SettingKind.Toggle;
    public override JsonNode DefaultNode => JsonValue.Create(Default);

    public bool Read(JsonNode? node)
    {
        if (node is JsonValue v && v.TryGetValue(out bool b))
            return b;
        return TryNumber(node, out var d) ? d != 0 : Default;
    }

    public override JsonNode Coerce(JsonNode? node) => JsonValue.Create(Read(node));

    public override IEnumerable<string> Emit(JsonNode value)
    {
        var on = Read(value);
        if (Emitter is not null)
            return Emitter(on);
        if (IsLaunchOption)
            return on && Cvar is not null ? [Cvar] : [];
        return Cvar is null ? [] : [$"{Cvar} {(on ? 1 : 0)}"];
    }

    public override JsonNode? Parse(string raw)
    {
        if (Parser is not null)
            return Parser(raw) is { } parsed ? JsonValue.Create(parsed) : null;
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? JsonValue.Create(d != 0)
            : null;
    }
}

public sealed class SliderSetting : SettingDefinition
{
    public double Default { get; init; }
    public double Min { get; init; }
    public double Max { get; init; } = 100;
    public double Step { get; init; } = 1;
    public int Decimals { get; init; }
    public string? Unit { get; init; }

    /// <summary>Optional label for specific values, e.g. 0 → "Unlimited".</summary>
    public IReadOnlyDictionary<double, string>? SpecialValues { get; init; }

    public Func<double, IEnumerable<string>>? Emitter { get; init; }

    public override SettingKind Kind => SettingKind.Slider;
    public override JsonNode DefaultNode => ToNode(Default);

    public double Read(JsonNode? node) => TryNumber(node, out var d) && double.IsFinite(d) ? Clamp(d) : Default;

    public double Clamp(double value)
    {
        var clamped = Math.Clamp(value, Min, Max);
        if (Step > 0)
            clamped = Min + Math.Round((clamped - Min) / Step) * Step;
        return Math.Round(Math.Clamp(clamped, Min, Max), Decimals);
    }

    public string Format(double value) => value.ToString(Decimals == 0 ? "0" : "0." + new string('#', Decimals), CultureInfo.InvariantCulture);

    public string Display(double value)
    {
        if (SpecialValues is not null && SpecialValues.TryGetValue(value, out var label))
            return label;
        return Unit is null ? Format(value) : $"{Format(value)} {Unit}";
    }

    private JsonNode ToNode(double value) => Decimals == 0 ? JsonValue.Create((long)value) : JsonValue.Create(value);

    public override JsonNode Coerce(JsonNode? node) => ToNode(Read(node));

    public override IEnumerable<string> Emit(JsonNode value)
    {
        var v = Read(value);
        if (Emitter is not null)
            return Emitter(v);
        if (Cvar is null)
            return [];
        return IsLaunchOption ? [Cvar, Format(v)] : [$"{Cvar} {Format(v)}"];
    }

    public override JsonNode? Parse(string raw) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? ToNode(Clamp(d)) : null;
}

public sealed record ChoiceOption(string Label, string Value, string? Hint = null);

public sealed class ChoiceSetting : SettingDefinition
{
    public required IReadOnlyList<ChoiceOption> Options { get; init; }
    public required string Default { get; init; }
    public Func<ChoiceOption, IEnumerable<string>>? Emitter { get; init; }

    public override SettingKind Kind => SettingKind.Choice;
    public override JsonNode DefaultNode => ToNode(Default);

    public ChoiceOption Read(JsonNode? node)
    {
        string? key = null;
        if (node is JsonValue v)
        {
            if (v.TryGetValue(out string? s))
                key = s;
            else if (TryNumber(v, out var d))
                key = d.ToString(CultureInfo.InvariantCulture);
        }
        return Find(key) ?? Find(Default) ?? Options[0];
    }

    public ChoiceOption? Find(string? value) =>
        value is null ? null : Options.FirstOrDefault(o => string.Equals(o.Value, value, StringComparison.OrdinalIgnoreCase));

    private static JsonNode ToNode(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? JsonValue.Create(n) : JsonValue.Create(value);

    public override JsonNode Coerce(JsonNode? node) => ToNode(Read(node).Value);

    public override IEnumerable<string> Emit(JsonNode value)
    {
        var option = Read(value);
        if (Emitter is not null)
            return Emitter(option);
        if (Cvar is null)
            return [];
        return IsLaunchOption ? [Cvar, option.Value] : [$"{Cvar} {option.Value}"];
    }

    public override JsonNode? Parse(string raw)
    {
        if (Find(raw) is { } exact)
            return ToNode(exact.Value);
        // "2.000000" style values from config.cfg
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            && Find(d.ToString(CultureInfo.InvariantCulture)) is { } numeric
                ? ToNode(numeric.Value)
                : null;
    }
}

public sealed class TextSetting : SettingDefinition
{
    public string Default { get; init; } = "";
    public int MaxLength { get; init; } = 64;

    public override SettingKind Kind => SettingKind.Text;
    public override JsonNode DefaultNode => JsonValue.Create(Default);

    /// <summary>
    /// Strips characters that would let a value escape its cfg line
    /// (quotes, separators, line breaks) and enforces the length limit.
    /// </summary>
    public string Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Default;
        var cleaned = new string(text.Where(c => c is not ('"' or ';' or '\r' or '\n') && !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length > MaxLength)
            cleaned = cleaned[..MaxLength];
        return cleaned.Length == 0 ? Default : cleaned;
    }

    public string Read(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out string? s) ? Sanitize(s) : Default;

    public override JsonNode Coerce(JsonNode? node) => JsonValue.Create(Read(node));

    public override IEnumerable<string> Emit(JsonNode value) =>
        Cvar is null ? [] : [$"{Cvar} \"{Read(value)}\""];

    public override JsonNode? Parse(string raw) => JsonValue.Create(Sanitize(raw.Trim().Trim('"')));
}
