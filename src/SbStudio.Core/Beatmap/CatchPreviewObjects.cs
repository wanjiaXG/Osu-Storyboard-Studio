namespace SbStudio.Core;

public enum CatchObjectKind { Fruit, Droplet, TinyDroplet, Banana }
public readonly record struct CatchPreviewObject(double Time, float X, CatchObjectKind Kind, int Index, int Combo, int HitSound, HitSample Sample, bool HyperDash = false);

/// <summary>
/// Catch's native .osu representation uses circles, sliders and spinners. Build their falling objects
/// following lazer's JuiceStream, BananaShower and CatchBeatmapProcessor (no gameplay mods).
/// </summary>
public static class CatchPreviewObjects
{
    public static List<CatchPreviewObject> Build(OsuBeatmap map)
    {
        var result = new List<CatchPreviewObject>();
        var random = new CatchRandom();
        int index = 0;
        foreach (var obj in map.Objects)
        {
            if (obj is Spinner spinner)
            {
                double spacing = spinner.Duration;
                while (spacing > 100) spacing /= 2;
                if (spacing > 0)
                    for (double time = (int)spinner.StartTime; time <= (int)spinner.EndTime && result.Count < 200000; time += spacing)
                    {
                        Add(time, (float)(random.NextDouble() * 512), CatchObjectKind.Banana);
                        random.NextDouble(); random.NextDouble(); random.NextDouble();
                    }
            }
            else if (obj is Slider slider)
            {
                var events = new List<(double Time, double Progress, CatchObjectKind? Kind, int Edge)>();
                for (int edge = 0; edge <= slider.SpanCount; edge++)
                    events.Add((slider.StartTime + edge * slider.SpanDuration, edge % 2, CatchObjectKind.Fruit, edge));
                foreach (var tick in slider.Ticks)
                    events.Add((tick.Time, tick.PathProgress, CatchObjectKind.Droplet, -1));
                // LastTick affects only the spacing of tiny droplets, not the fruit tail.
                double lastTick = Math.Max(slider.StartTime + slider.Duration / 2, slider.EndTime - 36);
                events.Add((lastTick, slider.ProgressAt(slider.Duration <= 0 ? 0 : (lastTick - slider.StartTime) / slider.Duration), null, -1));
                events = events.OrderBy(e => e.Time).ToList();
                (double Time, double Progress)? previous = null;
                foreach (var e in events)
                {
                    if (previous is { } last)
                    {
                        double gap = (int)e.Time - (int)last.Time, spacing = gap;
                        if (gap > 80)
                        {
                            while (spacing > 100) spacing /= 2;
                            for (double t = spacing; t < gap && result.Count < 200000; t += spacing)
                            {
                                double progress = last.Progress + t / gap * (e.Progress - last.Progress);
                                float x = slider.Position.X + slider.Path.PositionAt(progress).X;
                                Add(last.Time + t, x + (int)(-20 + random.NextDouble() * 40), CatchObjectKind.TinyDroplet);
                            }
                        }
                    }
                    if (e.Kind is { } kind)
                    {
                        int sound = e.Edge >= 0 && e.Edge < slider.EdgeSounds.Count ? slider.EdgeSounds[e.Edge] : slider.HitSound;
                        HitSample sample = e.Edge >= 0 && e.Edge < slider.EdgeSamples.Count ? slider.EdgeSamples[e.Edge] : slider.Sample;
                        Add(e.Time, slider.Position.X + slider.Path.PositionAt(e.Progress).X, kind, sound, sample);
                        if (kind == CatchObjectKind.Droplet) random.NextDouble();
                    }
                    previous = (e.Time, e.Progress);
                }
            }
            else Add(obj.StartTime, obj.Position.X, CatchObjectKind.Fruit);
            index++;

            void Add(double time, float x, CatchObjectKind kind, int? sound = null, HitSample? sample = null) =>
                result.Add(new(time, Math.Clamp(x, 0, 512), kind, index, obj.ComboIndexWithOffsets, sound ?? obj.HitSound, sample ?? obj.Sample));
        }
        result = result.OrderBy(o => o.Time).ToList();
        var palpable = Enumerable.Range(0, result.Count).Where(i => result[i].Kind is CatchObjectKind.Fruit or CatchObjectKind.Droplet).ToArray();
        double halfWidth = 106.75 * map.Scale;
        double excess = halfWidth;
        int direction = 0;
        for (int i = 0; i + 1 < palpable.Length; i++)
        {
            var current = result[palpable[i]];
            var next = result[palpable[i + 1]];
            int nextDirection = next.X > current.X ? 1 : -1;
            double delta = (int)next.Time - (int)current.Time - 1000f / 60 / 4;
            double distance = Math.Abs(next.X - current.X) - (direction == nextDirection ? excess : halfWidth);
            double margin = delta - distance; // Catcher.BASE_DASH_SPEED = 1
            if (margin < 0)
            {
                result[palpable[i]] = current with { HyperDash = true };
                excess = halfWidth;
            }
            else excess = Math.Clamp(margin, 0, halfWidth);
            direction = nextDirection;
        }
        return result;
    }

    // Xorshift128 with the legacy initial state used by lazer's LegacyRandom. Catch consumes the
    // same random values for banana properties and droplet rotations, even when only X is needed.
    private sealed class CatchRandom
    {
        private uint x = 1337, y = 842502087, z = 3579807591, w = 273326509;
        public double NextDouble()
        {
            uint t = x ^ (x << 11);
            x = y; y = z; z = w;
            w = w ^ (w >> 19) ^ t ^ (t >> 8);
            return (w & 0x7FFFFFFF) / 2147483648.0;
        }
    }
}
