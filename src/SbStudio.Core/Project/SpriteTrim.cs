namespace SbStudio.Core;

/// <summary>
/// Changes when a sprite appears and disappears the way a video editor trims a clip: what the sprite does
/// stays where it is in time, only the window it is shown in moves.
///
/// Shortening drops the commands outside the window and cuts the ones crossing its edge, with the value at
/// the cut worked out from the easing so the picture inside the window does not change. Lengthening lets the
/// constant commands touching the edge follow it; without any, an opacity hold fills the added time.
/// Loops are never cut: an edge cannot be moved into one.
/// </summary>
public static class SpriteTrim
{
    /// <summary>How far the edges may move inwards before they would cut into a loop.</summary>
    public static (int MaxStart, int MinEnd) Limits(SbSprite sprite)
    {
        int maxStart = int.MaxValue, minEnd = int.MinValue;
        foreach (var node in sprite.Nodes)
        {
            if (node is not SbLoop { Commands.Count: > 0 } loop) continue;
            maxStart = Math.Min(maxStart, loop.AbsoluteStart);
            minEnd = Math.Max(minEnd, loop.AbsoluteEnd);
        }
        return (maxStart, minEnd);
    }

    /// <summary>The nodes of <paramref name="original"/> trimmed to be shown from newStart to newEnd.</summary>
    public static List<SbNode> Apply(SbSprite original, int newStart, int newEnd)
    {
        var timeline = original.Timeline;
        if (!timeline.HasCommands) return original.Nodes.Select(n => n.Clone()).ToList();

        int oldStart = timeline.StartTime, oldEnd = timeline.EndTime;
        var (maxStart, minEnd) = Limits(original);
        newStart = Math.Min(newStart, maxStart);
        newEnd = Math.Max(Math.Max(newEnd, minEnd), newStart);

        var main = new List<SbNode>();
        // Surviving value commands with the command they came from, to find the ones that touched an edge.
        var kept = new List<(SbCommand Source, SbCommand Copy)>();
        // Per kind: the command whose end value is in force when the window opens, and the first one of all,
        // whose start value applies before anything of that kind has run.
        var before = new Dictionary<CommandKind, SbCommand>();
        var first = new Dictionary<CommandKind, SbCommand>();

        foreach (var node in original.Nodes)
        {
            if (node is not SbCommand c)
            {
                main.Add(node.Clone());
                continue;
            }

            bool instant = c.End <= c.Start;
            if (c.Kind == CommandKind.P)
            {
                var p = c.CloneCommand();
                if (instant)
                {
                    // Applies for the whole lifetime wherever it sits; keep it inside the window.
                    p.Start = p.End = Math.Clamp(c.Start, newStart, newEnd);
                    main.Add(p);
                }
                else if (c.End > newStart && c.Start < newEnd)
                {
                    p.Start = Math.Max(c.Start, newStart);
                    p.End = Math.Min(c.End, newEnd);
                    main.Add(p);
                }
                continue;
            }

            if (!first.TryGetValue(c.Kind, out var earliest) || c.Start < earliest.Start) first[c.Kind] = c;

            bool inside = instant ? c.Start >= newStart && c.Start <= newEnd : c.End > newStart && c.Start < newEnd;
            if (!inside)
            {
                if (c.End <= newStart && (!before.TryGetValue(c.Kind, out var last) || c.Start >= last.Start)) before[c.Kind] = c;
                continue;
            }

            var copy = c.CloneCommand();
            if (c.Start < newStart)
            {
                copy.From = ValueAt(c, newStart);
                copy.Start = newStart;
            }
            if (c.End > newEnd)
            {
                copy.To = ValueAt(c, newEnd);
                copy.End = newEnd;
            }
            main.Add(copy);
            kept.Add((c, copy));
        }

        // A value that used to come from a command now outside the window is pinned at the new start.
        var result = new List<SbNode>();
        foreach (var kind in SbInfo.AllKinds.Concat(SbInfo.DepthKinds))
        {
            if (!first.TryGetValue(kind, out var earliest)) continue;
            var survivors = kept.Where(k => k.Copy.Kind == kind).Select(k => k.Copy).OrderBy(k => k.Start).ToList();
            if (survivors.Count > 0 && survivors[0].Start <= newStart) continue;

            double[] held = before.TryGetValue(kind, out var last) ? last.To : earliest.From;
            double[] otherwise = survivors.Count > 0 ? survivors[0].From : DefaultValue(kind, original);
            if (!Same(held, otherwise)) result.Add(new SbCommand(kind, 0, newStart, newStart, (double[])held.Clone()));
        }
        result.AddRange(main);

        // Lengthening: constant commands that touched the old edge stretch to the new one.
        if (newStart < oldStart)
        {
            foreach (var (source, copy) in kept)
                if (source.Start == oldStart && source.End > source.Start && Same(source.From, source.To)) copy.Start = newStart;
        }
        if (newEnd > oldEnd)
        {
            foreach (var (source, copy) in kept)
                if (source.End == oldEnd && source.End > source.Start && Same(source.From, source.To)) copy.End = newEnd;
        }

        // Whatever gap is left at either edge is bridged by holding the opacity, which shows nothing new but
        // keeps the sprite alive for that time.
        int firstStart = int.MaxValue, lastEnd = int.MinValue;
        foreach (var node in result)
        {
            switch (node)
            {
                case SbCommand c:
                    firstStart = Math.Min(firstStart, c.Start);
                    lastEnd = Math.Max(lastEnd, Math.Max(c.Start, c.End));
                    break;
                case SbLoop { Commands.Count: > 0 } loop:
                    firstStart = Math.Min(firstStart, loop.AbsoluteStart);
                    lastEnd = Math.Max(lastEnd, loop.AbsoluteEnd);
                    break;
            }
        }
        if (firstStart == int.MaxValue)
        {
            result.Add(OpacityHold(original, newStart, newEnd, Math.Clamp(newStart, oldStart, oldEnd)));
        }
        else
        {
            if (firstStart > newStart) result.Insert(0, OpacityHold(original, newStart, firstStart, Math.Max(newStart, oldStart)));
            if (lastEnd < newEnd) result.Add(OpacityHold(original, lastEnd, newEnd, Math.Min(newEnd, oldEnd)));
        }
        return result;
    }

    public static List<SbCommand> Depth(SbSprite original,int start,int end)
    {
        if(original.DepthCommands.Count==0) return new();
        var proxy=new SbSprite();
        proxy.Nodes=original.DepthCommands.Select(c=>c.Clone()).ToList();
        proxy.Nodes.Add(new SbCommand(CommandKind.F,0,original.Timeline.StartTime,original.Timeline.EndTime,new[]{1.0}));
        return Apply(proxy,start,end).OfType<SbCommand>().Where(c=>SbInfo.DepthKinds.Contains(c.Kind)).ToList();
    }
    private static SbCommand OpacityHold(SbSprite original, int from, int to, int sampleAt) =>
        new(CommandKind.F, 0, from, to, new[] { Math.Round(original.Timeline.Evaluate(sampleAt, original).Opacity, 4) });

    private static double[] ValueAt(SbCommand c, int time)
    {
        double u=(time-c.Start)/(double)(c.End-c.Start);
        double t = SbInfo.IsEditorOnly(c.Kind) && c.Curve!=null ? c.Curve.Compile().At(u) : Easing.Apply(c.Easing,u);
        var value = new double[c.From.Length];
        // Colours stay whole numbers; everything else keeps a few decimals.
        int digits = c.Kind == CommandKind.C ? 0 : 4;
        for (int i = 0; i < value.Length; i++) value[i] = Math.Round(c.From[i] + (c.To[i] - c.From[i]) * t, digits);
        return value;
    }

    private static double[] DefaultValue(CommandKind kind, SbSprite sprite) => kind switch
    {
        CommandKind.M => new[] { sprite.X, sprite.Y },
        CommandKind.MX => new[] { sprite.X },
        CommandKind.MY => new[] { sprite.Y },
        CommandKind.V => new[] { 1.0, 1.0 },
        CommandKind.R or CommandKind.Z or CommandKind.RX or CommandKind.RY => new[] { 0.0 },
        CommandKind.C => new[] { 255.0, 255.0, 255.0 },
        _ => new[] { 1.0 },
    };

    private static bool Same(double[] a, double[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > 1e-6) return false;
        return true;
    }
}
