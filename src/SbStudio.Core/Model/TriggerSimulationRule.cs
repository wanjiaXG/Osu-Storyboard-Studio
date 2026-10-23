using System.Globalization;
using System.Text.Json.Serialization;

namespace SbStudio.Core;

public enum TriggerRuleKind { HitSound, HitObjectHit, State, Beatmap, Unknown }

/// <summary>An editor-only gameplay event source, separate from storyboard commands.</summary>
public sealed class TriggerSimulationRule
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    public TriggerRuleKind Kind { get; set; }
    public int Offset { get; set; }
    public int RepeatCount { get; set; } = 1;
    public double RepeatInterval { get; set; }
    public SimSampleKind SampleKind { get; set; }
    public TriggerSampleBank NormalBank { get; set; } = TriggerSampleBank.Normal;
    public TriggerSampleBank AdditionBank { get; set; } = TriggerSampleBank.Normal;
    public bool Whistle { get; set; }
    public bool Finish { get; set; }
    public bool Clap { get; set; }
    public int Index { get; set; } = 1;
    public bool FileSample { get; set; }
    public bool Failing { get; set; }
    public int? From { get; set; }
    public int? To { get; set; }
    public bool BeatmapSounds { get; set; } = true;
    public bool BeatmapHits { get; set; } = true;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Condition { get; set; }

    public TriggerSimulationRule Clone() => (TriggerSimulationRule)MemberwiseClone();

    public void Migrate()
    {
        if (Condition == null) return;
        if (!TriggerCondition.TryParse(Condition, out var c)
            || c.Suffix.Length > 0 && !int.TryParse(c.Suffix, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        { Kind = TriggerRuleKind.Unknown; return; }
        Kind = c.Kind switch { TriggerKind.HitObjectHit => TriggerRuleKind.HitObjectHit,
            TriggerKind.Passing or TriggerKind.Failing => TriggerRuleKind.State, _ => TriggerRuleKind.HitSound };
        Failing = c.Kind == TriggerKind.Failing;
        NormalBank = c.NormalBank == TriggerSampleBank.Any ? TriggerSampleBank.Normal : c.NormalBank;
        AdditionBank = c.AdditionBank == TriggerSampleBank.Any ? TriggerSampleBank.Normal : c.AdditionBank;
        Whistle = c.Addition == TriggerAddition.Whistle; Finish = c.Addition == TriggerAddition.Finish; Clap = c.Addition == TriggerAddition.Clap;
        Index = c.Suffix.Length == 0 ? 1 : int.Parse(c.Suffix, CultureInfo.InvariantCulture);
        Condition = null;
    }

    public static TriggerSimulationRule Matching(TriggerCondition c, int time)
    {
        var r = new TriggerSimulationRule { Condition = c.ToName(), Offset = time };
        r.Migrate();
        if (c.Kind == TriggerKind.HitSound && c.AdditionBank != TriggerSampleBank.Any && c.Addition == TriggerAddition.Any) r.Clap = true;
        return r;
    }

    /// <summary>Returns a localisation key for an invalid value; no values are silently clamped.</summary>
    public string? Problem()
    {
        if (Kind == TriggerRuleKind.Unknown) return null;
        if (Kind is TriggerRuleKind.HitSound or TriggerRuleKind.HitObjectHit)
        {
            if (RepeatCount < 1) return "simulation.repeat.invalid";
            if (RepeatCount > 1 && (!double.IsFinite(RepeatInterval) || RepeatInterval <= 0
                || Math.Abs(RepeatInterval - Math.Round(RepeatInterval, 3)) > 1e-8)) return "simulation.interval.invalid";
        }
        if (Kind == TriggerRuleKind.HitSound && (Index < 0 || NormalBank is < TriggerSampleBank.Normal or > TriggerSampleBank.Drum
            || AdditionBank is < TriggerSampleBank.Normal or > TriggerSampleBank.Drum)) return "simulation.index.invalid";
        if (Kind == TriggerRuleKind.Beatmap && From > To) return "simulation.range.invalid";
        return null;
    }
}
