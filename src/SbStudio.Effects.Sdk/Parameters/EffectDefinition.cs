using System.Text.Json;
using System.Text.RegularExpressions;

namespace SbStudio.Effects;

public enum ParameterType { Integer, Number, Toggle, Choice, Text, Multiline, Color, Point2, Image, Animation, File, Time }
public enum TimeMode { FixedContent, AdjustableDuration }
[Flags] public enum EffectInputs { None = 0, Timing = 1, HitObjects = 2, Metadata = 4, AbsoluteTime = 8 }
public sealed record ParameterKey<T>(string Id);
public sealed record VisibilityCondition(string? Parameter = null, JsonElement? EqualsValue = null,
    IReadOnlyList<VisibilityCondition>? All = null, IReadOnlyList<VisibilityCondition>? Any = null)
{
    public static VisibilityCondition Equal<T>(ParameterKey<T> key, T value) => new(key.Id, JsonSerializer.SerializeToElement(value, EffectJson.Options));
    public static VisibilityCondition And(params VisibilityCondition[] items) => new(All: Array.AsReadOnly(items.ToArray()));
    public static VisibilityCondition Or(params VisibilityCondition[] items) => new(Any: Array.AsReadOnly(items.ToArray()));
    public bool Evaluate(IReadOnlyDictionary<string, JsonElement> values) => Parameter != null
        ? values.TryGetValue(Parameter, out var value) && EqualsValue is {} expected && JsonElement.DeepEquals(value, expected)
        : All != null ? All.All(c => c != null && c.Evaluate(values)) : Any != null && Any.Any(c => c != null && c.Evaluate(values));
    internal IEnumerable<string> Dependencies() => Parameter != null ? [Parameter] : (All ?? Any ?? []).SelectMany(c => c.Dependencies());
}
public sealed record ParameterDefinition(string Id, ParameterType Type, string Label, string Description, JsonElement Default,
    double? Min = null, double? Max = null, double? SuggestedMin = null, double? SuggestedMax = null, double? Step = null,
    string Unit = "", string Section = "", IReadOnlyList<string>? Choices = null, VisibilityCondition? VisibleWhen = null);
public sealed record EffectDefinition(string EffectId, string Name, string Description, string Category, int SchemaVersion,
    TimeMode TimeMode, double DefaultDuration, double MinDuration, double MaxDuration, EffectInputs Inputs, bool Cacheable,
    IReadOnlyList<ParameterDefinition> Parameters);

public sealed class ParameterBuilder<T>
{
    private readonly EffectDefinitionBuilder owner;
    internal ParameterDefinition Definition;
    internal ParameterBuilder(EffectDefinitionBuilder owner, ParameterDefinition definition) { this.owner = owner; Definition = definition; }
    public ParameterKey<T> Key => new(Definition.Id);
    private ParameterBuilder<T> Change(Func<ParameterDefinition, ParameterDefinition> change) { owner.Check(); Definition = change(Definition); return this; }
    public ParameterBuilder<T> Label(string value) => Change(p => p with { Label = value });
    public ParameterBuilder<T> Description(string value) => Change(p => p with { Description = value });
    public ParameterBuilder<T> Default(T value) => Change(p => p with { Default = JsonSerializer.SerializeToElement(value, EffectJson.Options) });
    public ParameterBuilder<T> Range(double min, double max) => Change(p => p with { Min = Guard.Finite(min), Max = Guard.Finite(max) });
    public ParameterBuilder<T> SuggestedRange(double min, double max) => Change(p => p with { SuggestedMin = Guard.Finite(min), SuggestedMax = Guard.Finite(max) });
    public ParameterBuilder<T> Step(double value) => Change(p => p with { Step = Guard.Finite(value) });
    public ParameterBuilder<T> Unit(string value) => Change(p => p with { Unit = value });
    public ParameterBuilder<T> Section(string value) => Change(p => p with { Section = value });
    public ParameterBuilder<T> VisibleWhen(VisibilityCondition value) => Change(p => p with { VisibleWhen = value });
}

public sealed class EffectDefinitionBuilder
{
    private readonly List<Func<ParameterDefinition>> parameters = [];
    private readonly HashSet<string> ids = new(StringComparer.Ordinal);
    private bool complete;
    private string id, name, description = "", category = "";
    private int version = 1;
    private TimeMode mode;
    private double duration = 1000, min = 1, max = 3600000;
    private EffectInputs inputs;
    private bool cacheable = true;
    public EffectDefinitionBuilder(string effectId) { ValidateId(effectId); id = effectId; name = effectId; }
    public static void ValidateId(string value) { if (!Regex.IsMatch(value, "^[a-z][a-z0-9.-]{1,127}$")) throw new ArgumentException("Invalid effect/package ID."); }
    internal void Check() { if (complete) throw new InvalidOperationException("The definition has been sealed."); }
    public EffectDefinitionBuilder Name(string value) { Check(); name = value; return this; }
    public EffectDefinitionBuilder Description(string value) { Check(); description = value; return this; }
    public EffectDefinitionBuilder Category(string value) { Check(); category = value; return this; }
    public EffectDefinitionBuilder SchemaVersion(int value) { Check(); if (value < 1) throw new ArgumentOutOfRangeException(nameof(value)); version = value; return this; }
    public EffectDefinitionBuilder Uses(EffectInputs value) { Check(); if (((int)value & ~15) != 0) throw new ArgumentOutOfRangeException(nameof(value)); inputs = value; return this; }
    public EffectDefinitionBuilder Cacheable(bool value) { Check(); cacheable = value; return this; }
    public EffectDefinitionBuilder AdjustableDuration(double defaultMs, double minMs = 1, double maxMs = 3600000)
    {
        Check(); Guard.Time(minMs, maxMs); Guard.Finite(defaultMs);
        if (minMs <= 0 || defaultMs < minMs || defaultMs > maxMs) throw new ArgumentOutOfRangeException(nameof(defaultMs));
        mode = TimeMode.AdjustableDuration; duration = defaultMs; min = minMs; max = maxMs; return this;
    }
    private ParameterBuilder<T> Register<T>(string id, ParameterType type, T value, IReadOnlyList<string>? choices = null)
    {
        Check();
        if (!Regex.IsMatch(id, "^[A-Za-z][A-Za-z0-9_-]*$") || !ids.Add(id)) throw new ArgumentException("Invalid or duplicate parameter ID.");
        var result = new ParameterBuilder<T>(this, new(id, type, id, "", JsonSerializer.SerializeToElement(value, EffectJson.Options), Choices: choices));
        parameters.Add(() => result.Definition); return result;
    }
    public ParameterBuilder<int> Integer(string id) => Register(id, ParameterType.Integer, 0);
    public ParameterBuilder<double> Number(string id) => Register(id, ParameterType.Number, 0d);
    public ParameterBuilder<double> Time(string id) => Register(id, ParameterType.Time, 0d);
    public ParameterBuilder<bool> Toggle(string id) => Register(id, ParameterType.Toggle, false);
    public ParameterBuilder<string> Choice(string id, params string[] values)
    {
        if (values.Length == 0 || values.Distinct().Count() != values.Length || values.Any(string.IsNullOrEmpty)) throw new ArgumentException("Choice values must be unique and nonempty.");
        return Register(id, ParameterType.Choice, values[0], Array.AsReadOnly(values.ToArray()));
    }
    public ParameterBuilder<string> Text(string id) => Register(id, ParameterType.Text, "");
    public ParameterBuilder<string> Multiline(string id) => Register(id, ParameterType.Multiline, "");
    public ParameterBuilder<ColorValue> Color(string id) => Register(id, ParameterType.Color, ColorValue.White);
    public ParameterBuilder<Point2> Point2(string id) => Register(id, ParameterType.Point2, new Point2(0, 0));
    public ParameterBuilder<AssetReference> Image(string id) => Register(id, ParameterType.Image, AssetReference.Package("assets/default.png"));
    public ParameterBuilder<AssetReference> Animation(string id) => Register(id, ParameterType.Animation, AssetReference.Package("assets/default.png"));
    public ParameterBuilder<AssetReference> File(string id) => Register(id, ParameterType.File, AssetReference.Package("assets/default.png"));
    public EffectDefinition Build()
    {
        Check(); var result = parameters.Select(p => p()).ToArray();
        foreach (var p in result)
        {
            if (p.Min > p.Max || p.SuggestedMin > p.SuggestedMax || p.Step <= 0) throw new ArgumentException($"Invalid range for {p.Id}.");
            ParameterValues.Validate(p, p.Default);
            foreach (string dependency in p.VisibleWhen?.Dependencies() ?? [])
                if (!result.Any(d => d.Id == dependency && d.Type is ParameterType.Toggle or ParameterType.Choice)) throw new ArgumentException("Visibility requires a declared toggle or choice.");
            Visit(p.Id, new HashSet<string>());
        }
        void Visit(string key, HashSet<string> path)
        {
            if (!path.Add(key)) throw new ArgumentException("Cyclic parameter visibility.");
            foreach (var dep in result.Single(p => p.Id == key).VisibleWhen?.Dependencies() ?? []) Visit(dep, path);
            path.Remove(key);
        }
        complete = true;
        return new(id, name, description, category, version, mode, duration, min, max, inputs, cacheable, Array.AsReadOnly(result));
    }
}

public sealed class ParameterValues
{
    private readonly Dictionary<string, JsonElement> values;
    public ParameterValues(EffectDefinition definition, IReadOnlyDictionary<string, JsonElement> supplied)
    {
        values = new(StringComparer.Ordinal);
        foreach (var p in definition.Parameters)
        {
            var value = supplied.TryGetValue(p.Id, out var v) ? v : p.Default;
            Validate(p, value); values.Add(p.Id, value.Clone());
        }
    }
    public T Get<T>(ParameterKey<T> key) => values.TryGetValue(key.Id, out var value)
        ? value.Deserialize<T>(EffectJson.Options) ?? throw new ArgumentException($"Null parameter {key.Id}.")
        : throw new KeyNotFoundException(key.Id);
    public static void Validate(ParameterDefinition p, JsonElement value)
    {
        try
        {
            switch (p.Type)
            {
                case ParameterType.Integer: CheckRange(value.GetInt32()); break;
                case ParameterType.Number: case ParameterType.Time: CheckRange(value.GetDouble()); break;
                case ParameterType.Toggle: _ = value.GetBoolean(); break;
                case ParameterType.Choice:
                    if (p.Choices?.Contains(value.GetString()!) != true) throw new ArgumentException("Unknown choice."); break;
                case ParameterType.Text: case ParameterType.Multiline:
                    if (value.GetString() == null) throw new ArgumentException("Null text."); break;
                case ParameterType.Point2: Guard.Point(value.Deserialize<Point2>(EffectJson.Options)); break;
                case ParameterType.Color: _ = value.Deserialize<ColorValue>(EffectJson.Options); break;
                case ParameterType.Image: case ParameterType.Animation: case ParameterType.File:
                    (value.Deserialize<AssetReference>(EffectJson.Options) ?? throw new ArgumentException("Null asset.")).Validate(); break;
                default: throw new NotSupportedException($"Parameter type {p.Type}.");
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        { throw new ArgumentException($"Invalid parameter '{p.Id}': {ex.Message}", ex); }
        void CheckRange(double v) { Guard.Finite(v); if (v < p.Min || v > p.Max) throw new ArgumentOutOfRangeException(p.Id); }
    }
}
