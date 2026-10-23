using System.Text.Json;
using System.Text.Json.Serialization;
using SbStudio.Effects;

namespace SbStudio.Core;

public sealed class EffectBinding
{
    public int FormatVersion { get; set; } = 1;
    public Guid InstanceGuid { get; set; } = Guid.NewGuid();
    public string PackageId { get; set; } = "";
    public string EffectId { get; set; } = "";
    public string PackageVersion { get; set; } = "";
    public string PackageHash { get; set; } = "";
    public int SdkMajor { get; set; } = 1;
    public int SchemaVersion { get; set; } = 1;
    public string SourceKind { get; set; } = "package";
    public string SourceLocation { get; set; } = "";
    public string CodeRevision { get; set; } = "";
    public double? DurationMs { get; set; }
    public double TimeScale { get; set; } = 1;
    public double TimeOffset { get; set; }
    public string? OutputDifficulty { get; set; }
    public string InputDifficulty { get; set; } = "";
    public string DesiredInputKey { get; set; } = "";
    public string LastGoodOutputKey { get; set; } = "";
    public string LastGoodInputKey { get; set; } = "";
    public string LastGoodCodeRevision { get; set; } = "";
    public Dictionary<string, JsonElement> LastGoodParameters { get; set; } = new();
    public EffectDefinition? Definition { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; set; }
    [JsonIgnore] public bool Supported => (FormatVersion is 1 or 2) && double.IsFinite(TimeScale) && TimeScale != 0 && double.IsFinite(TimeOffset) && SdkMajor == 1 && InputDifficulty != null &&
        SourceLocation != null && SourceKind is "package" or "development" &&
        Definition is { Parameters: not null, Name: not null } definition && Enum.IsDefined(definition.TimeMode) &&
        definition.Parameters.All(p => p is { Id: not null, Label: not null, Unit: not null, Section: not null } && Enum.IsDefined(p.Type));
    public EffectBinding Clone() => EffectJson.Deserialize<EffectBinding>(EffectJson.Serialize(this));
    public static Dictionary<string, JsonElement> Parameters(PluginLink link)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (string line in link.Parameters)
        {
            int eq = line.IndexOf('='); if (eq < 1) continue;
            using var doc = JsonDocument.Parse(line[(eq + 1)..]); result[line[..eq]] = doc.RootElement.Clone();
        }
        return result;
    }
    public static void SetParameter(PluginLink link, string id, JsonElement value)
    { link.Parameters.RemoveAll(p => p.StartsWith(id + "=", StringComparison.Ordinal)); link.Parameters.Add(id + "=" + value.GetRawText()); }
}
