using System.Globalization;

namespace SbStudio.Core;

public enum SimEventKind { HitSound, StateChange, HitObjectHit }
public enum SimSampleKind { Hit, SliderTick, SpinnerBonus }
public enum SimPart { None, Circle, SliderHead, SliderRepeat, SliderTail, SliderTick, SpinnerEnd,
    Note, HoldHead, HoldTail, Drum, DrumRoll, Fruit, Droplet, TinyDroplet, Banana }
public readonly record struct SimSample(string Name, TriggerSampleBank Bank, string? Suffix);
public sealed record SimEvent(double Time, SimEventKind Kind, IReadOnlyList<SimSample> Samples,
    bool Failing = false, int RuleIndex = -1, int ObjectIndex = -1, SimPart Part = SimPart.None)
{
    public bool Effective { get; init; } = true;
}

public sealed class TriggerEventStream
{
    public IReadOnlyList<SimEvent> Events { get; }
    public bool HasStateRules { get; }
    public bool Approximate { get; }
    private readonly SimEvent[] states;
    public static TriggerEventStream Empty { get; } = new(Array.Empty<SimEvent>());

    public TriggerEventStream(IEnumerable<SimEvent> events, bool hasStateRules = false, bool approximate = false)
    {
        bool failing = false;
        Events = events.OrderBy(e => e.Time).ThenBy(e => e.Kind).ThenBy(e => e.RuleIndex).ThenBy(e => e.ObjectIndex)
            .Select(e => {
                if (e.Kind != SimEventKind.StateChange) return e;
                bool effective = failing != e.Failing; failing = e.Failing;
                return e with { Effective = effective };
            }).ToArray();
        states = Events.Where(e => e.Kind == SimEventKind.StateChange && e.Effective).ToArray();
        HasStateRules = hasStateRules || Events.Any(e => e.Kind == SimEventKind.StateChange);
        Approximate = approximate;
    }

    public bool FailingAt(double time)
    {
        int lo = 0, hi = states.Length;
        while (lo < hi) { int m = (lo + hi) / 2; if (states[m].Time <= time) lo = m + 1; else hi = m; }
        return lo > 0 && states[lo - 1].Failing;
    }

    public static TriggerEventStream Build(IReadOnlyList<TriggerSimulationRule> rules, OsuBeatmap? beatmap)
    {
        var events = new List<SimEvent>();
        for (int i = 0; i < rules.Count; i++)
        {
            var r = rules[i].Clone(); r.Migrate();
            if (!r.Enabled || r.Problem() != null || r.Kind == TriggerRuleKind.Unknown) continue;
            if (r.Kind == TriggerRuleKind.Beatmap)
            {
                if (beatmap != null) events.AddRange(BeatmapTriggerEvents.Build(beatmap, r.BeatmapSounds, r.BeatmapHits,
                    r.From ?? double.NegativeInfinity, r.To ?? double.PositiveInfinity).Select(e => e with { RuleIndex = i }));
                continue;
            }
            var samples = r.Kind == TriggerRuleKind.HitSound ? SamplesOf(r) : Array.Empty<SimSample>();
            int count = r.Kind == TriggerRuleKind.State ? 1 : r.RepeatCount;
            for (int k = 0; k < count; k++) events.Add(new(r.Offset + k * r.RepeatInterval,
                r.Kind switch { TriggerRuleKind.HitObjectHit => SimEventKind.HitObjectHit,
                    TriggerRuleKind.State => SimEventKind.StateChange, _ => SimEventKind.HitSound }, samples, r.Failing, i));
        }
        return new(events, rules.Any(r => r.Enabled && r.Kind == TriggerRuleKind.State),
            beatmap?.Mode > 0 && rules.Any(r => r.Enabled && r.Kind == TriggerRuleKind.Beatmap));
    }

    public static SimSample[] SamplesOf(TriggerSimulationRule r)
    {
        string? suffix = r.Index >= 2 ? r.Index.ToString(CultureInfo.InvariantCulture) : null;
        if (r.SampleKind == SimSampleKind.SpinnerBonus) return new[] { new SimSample("spinnerbonus", r.NormalBank, suffix) };
        bool tick = r.SampleKind == SimSampleKind.SliderTick;
        var samples = new List<SimSample> { new(tick ? "slidertick" : "hitnormal",
            !tick && r.FileSample ? TriggerSampleBank.Normal : r.NormalBank, !tick && r.FileSample ? null : suffix) };
        if (r.Whistle) samples.Add(new(tick ? "slidertick" : "hitwhistle", r.AdditionBank, suffix));
        if (r.Finish) samples.Add(new(tick ? "slidertick" : "hitfinish", r.AdditionBank, suffix));
        if (r.Clap) samples.Add(new(tick ? "slidertick" : "hitclap", r.AdditionBank, suffix));
        return samples.ToArray();
    }
}
