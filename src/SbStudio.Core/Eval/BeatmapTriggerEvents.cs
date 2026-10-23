namespace SbStudio.Core;

public static class BeatmapTriggerEvents
{
    public static List<SimEvent> Build(OsuBeatmap map, bool sounds, bool hits, double from, double to)
    {
        var result = new List<SimEvent>();
        void Add(double time, int index, SimPart part, HitSample sample, int flags = 0, SimSampleKind kind = SimSampleKind.Hit, bool sound = true, bool hit = true, double? resolveAt = null)
        {
            if (time < from || time > to) return;
            if (sounds && sound)
            {
                var s = HitSampleResolver.Resolve(map, resolveAt ?? time, sample);
                var r = new TriggerSimulationRule { NormalBank = (TriggerSampleBank)s.Normal, AdditionBank = (TriggerSampleBank)s.Addition,
                    Index = s.Index, FileSample = sample.Filename.Length > 0, SampleKind = kind,
                    Whistle = (flags & 2) != 0, Finish = (flags & 4) != 0, Clap = (flags & 8) != 0 };
                // A banana plays a copy of its spinner's hitnormal sample (BananaShower.CreateHitSampleInfo).
                var samples = TriggerEventStream.SamplesOf(r);
                if (part == SimPart.Banana) samples = new[] { samples[0] };
                if (kind == SimSampleKind.SliderTick && r.FileSample) samples[0] = new("hitnormal", TriggerSampleBank.Normal, null);
                result.Add(new(time, SimEventKind.HitSound, samples, ObjectIndex: index, Part: part));
            }
            if (hits && hit) result.Add(new(time, SimEventKind.HitObjectHit, Array.Empty<SimSample>(), ObjectIndex: index, Part: part));
        }
        HitSample Body(Slider slider) => slider.Sample with { Volume = 0, CustomIndex = 0, Filename = "" };
        HitSample Edge(Slider slider, int edge) => edge < slider.TriggerEdgeSamples.Count ? slider.TriggerEdgeSamples[edge]
            : edge < slider.EdgeSamples.Count ? slider.EdgeSamples[edge] : Body(slider);
        if (map.Mode == 2)
        {
            foreach (var o in CatchPreviewObjects.Build(map))
            {
                var sample = o.Sample;
                double? resolveAt = null;
                var parent = map.Objects[o.Index];
                if (parent is Slider slider)
                {
                    bool node = o.Kind == CatchObjectKind.Fruit && slider.SpanDuration > 0;
                    sample = node ? Edge(slider, (int)Math.Round((o.Time - slider.StartTime) / slider.SpanDuration)) : Body(slider);
                    // Droplets copy the juice stream's own samples, which take the green line at its start + 6 ms.
                    if (!node) resolveAt = slider.StartTime + 1;
                }
                else if (o.Kind == CatchObjectKind.Banana)
                {
                    // Spinner samples take the green line at the spinner's end + 5 ms.
                    sample = parent.Sample; resolveAt = parent.EndTime;
                }
                Add(o.Time, o.Index, o.Kind switch { CatchObjectKind.Droplet => SimPart.Droplet,
                    CatchObjectKind.TinyDroplet => SimPart.TinyDroplet, CatchObjectKind.Banana => SimPart.Banana, _ => SimPart.Fruit },
                    sample, o.HitSound, o.Kind == CatchObjectKind.Droplet ? SimSampleKind.SliderTick : SimSampleKind.Hit,
                    sound: o.Kind != CatchObjectKind.TinyDroplet, resolveAt: resolveAt);
            }
        }
        else for (int i = 0; i < map.Objects.Count; i++)
        {
            var o = map.Objects[i];
            if (map.Mode == 1)
            {
                if (o is Slider roll)
                {
                    double spacing = map.ScrollAt(o.StartTime).BeatLength / (map.SliderTickRate == 3 ? 3 : 4);
                    if (spacing > 0) for (double t = o.StartTime; t < roll.EndTime + spacing / 2; t += spacing) Add(t, i, SimPart.DrumRoll, Body(roll), o.HitSound);
                }
                else if (o is Spinner) { for (double t = o.StartTime; t <= o.EndTime; t += 100) Add(t, i, SimPart.DrumRoll, o.Sample); }
                else Add(o.StartTime, i, SimPart.Drum, o.Sample, o.HitSound);
            }
            else if (map.Mode == 3)
            {
                Add(o.StartTime, i, o is HoldNote ? SimPart.HoldHead : SimPart.Note, o.Sample, o.HitSound);
                if (o is HoldNote) Add(o.EndTime, i, SimPart.HoldTail, o.Sample, sound: false);
            }
            else if (o is Slider slider)
            {
                for (int edge = 0; edge <= slider.SpanCount; edge++)
                {
                    bool tail = edge == slider.SpanCount;
                    double time = slider.StartTime + edge * slider.SpanDuration;
                    var part = edge == 0 ? SimPart.SliderHead : tail ? SimPart.SliderTail : SimPart.SliderRepeat;
                    Add(time, i, part, Edge(slider, edge),
                        edge < slider.EdgeSounds.Count ? slider.EdgeSounds[edge] : slider.HitSound, hit: !tail);
                    if (tail)
                    {
                        // SliderInputManager allows the tail 36ms early, after the head and all ticks/repeats.
                        double lastNested = Math.Max(slider.StartTime, slider.StartTime + Math.Max(0, slider.SpanCount - 1) * slider.SpanDuration);
                        if (slider.Ticks.Count > 0) lastNested = Math.Max(lastNested, slider.Ticks.Max(t => t.Time));
                        Add(Math.Max(lastNested, time - 36), i, part, slider.Sample, sound: false);
                    }
                }
                // Ticks copy the slider's own hitnormal, resolved at its start + 6 ms (Slider.UpdateNestedSamples).
                foreach (var tick in slider.Ticks) Add(tick.Time, i, SimPart.SliderTick, Body(slider), kind: SimSampleKind.SliderTick, resolveAt: slider.StartTime + 1);
            }
            else if (o is Spinner) Add(o.EndTime, i, SimPart.SpinnerEnd, o.Sample, o.HitSound);
            else Add(o.StartTime, i, SimPart.Circle, o.Sample, o.HitSound);
        }
        return result.OrderBy(e => e.Time).ThenBy(e => e.Kind).ThenBy(e => e.ObjectIndex).ToList();
    }
}
