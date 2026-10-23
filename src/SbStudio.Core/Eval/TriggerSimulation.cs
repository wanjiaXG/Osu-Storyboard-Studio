using System.Runtime.CompilerServices;

namespace SbStudio.Core;

/// <summary>
/// Replays explicit gameplay events without changing commands or extending sprite lifetimes.
/// Stages are selected by event time, so future events cannot affect a frame and seeking is deterministic.
/// Dynamic transforms replace following transforms of the same property and interrupt earlier ones
/// when they start (osu-framework TargetGroupingTransformTracker), rather than adding keyframes to OSB.
/// </summary>
public sealed class TriggerSimulation
{
    private TriggerEventStream stream = TriggerEventStream.Empty;
    private ConditionalWeakTable<SbSprite, Prepared> cache = new();
    private readonly Dictionary<string, int[]> matches = new(StringComparer.Ordinal);
    public int Revision { get; private set; }
    public TriggerEventStream Stream => stream;

    private sealed record Compiled(SbTrigger Trigger, TriggerCondition? Condition, TriggerFire[] Fires, (int Channel, Seg Segment)[] Segments);
    private sealed class Prepared
    {
        public required int Revision;
        public required Compiled[] Triggers;
        public Replay? Replay;
        public Dictionary<SbTrigger, List<(int Channel, double Start, double Time)>>? Cancelled;
    }

    public void Configure(TriggerEventStream stream)
    {
        this.stream = stream; cache = new(); matches.Clear(); Revision++;
    }

    public bool FailStateAt(double time) => stream.FailingAt(time);

    private int[] MatchesOf(string name, TriggerCondition? condition)
    {
        if (matches.TryGetValue(name, out var result)) return result;
        result = condition == null ? Array.Empty<int>() : Enumerable.Range(0, stream.Events.Count)
            .Where(i => stream.Events[i].Effective && TriggerMatcher.Matches(condition, stream.Events[i])).ToArray();
        matches[name] = result;
        return result;
    }

    private Prepared Prepare(SbSprite sprite)
    {
        if (cache.TryGetValue(sprite, out var prepared) && prepared.Revision == sprite.Revision) return prepared;
        var list = new List<Compiled>();
        foreach (var t in sprite.Nodes.OfType<SbTrigger>())
        {
            TriggerCondition.TryParse(t.Name, out var condition);
            var indices = MatchesOf(t.Name, condition);
            int lo = 0, hi = indices.Length;
            while (lo < hi) { int m = (lo + hi) / 2; if (stream.Events[indices[m]].Time < t.Start) lo = m + 1; else hi = m; }
            var fires = new List<TriggerFire>();
            for (int k = lo; k < indices.Length && stream.Events[indices[k]].Time <= t.End; k++)
            {
                int i = indices[k]; double time = stream.Events[i].Time;
                fires.Add(new(i, time, sprite.Timeline.IsActive(time)));
            }
            var segments = new List<(int, Seg)>();
            foreach (var command in t.Commands.OrderBy(c => c.Start))
            {
                if (command.Kind == CommandKind.P)
                {
                    int i = char.ToUpperInvariant(command.Param) switch { 'H' => 10, 'V' => 11, _ => 12 };
                    segments.Add((i, new ValueSeg(command.Start, command.Start, 0, 1, 1, null)));
                    if (command.End > command.Start) segments.Add((i, new ValueSeg(command.End, command.End, 0, 0, 0, null)));
                }
                else
                {
                    var components = new ChannelSet(); components.Add(command); components.Seal();
                    for (int i = 0; i < 10; i++) foreach (var segment in components[i].Segments) segments.Add((i, segment));
                }
            }
            list.Add(new(t, condition, fires.ToArray(), segments.ToArray()));
        }
        prepared = new() { Revision = sprite.Revision, Triggers = list.ToArray() };
        cache.Remove(sprite); cache.Add(sprite, prepared);
        return prepared;
    }

    public SpriteState Evaluate(SbSprite sprite, double time)
    {
        var shown = sprite.Exported();
        if (!shown.Timeline.HasTriggers) return shown.Timeline.Evaluate(time, shown);
        var prepared = Prepare(shown);
        prepared.Replay ??= Build(shown, prepared);
        return prepared.Replay.Evaluate(shown, time);
    }

    /// <summary>
    /// The firing that stands for a moment: the last one at or before it, or the first one while the moment
    /// is still before that; -1 when there are none. A trigger's commands are edited at that firing.
    /// </summary>
    public static int FiringAt(IReadOnlyList<TriggerFire> fires, double time)
    {
        if (fires.Count == 0) return -1;
        int low = 0, high = fires.Count;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (fires[mid].Time <= time) low = mid + 1; else high = mid;
        }
        return Math.Max(0, low - 1);
    }

    public IReadOnlyList<TriggerFire> FiresOf(SbSprite sprite, SbTrigger trigger)
    {
        int index = sprite.Nodes.OfType<SbTrigger>().ToList().IndexOf(trigger);
        var prepared = Prepare(sprite.Exported());
        return index >= 0 && index < prepared.Triggers.Length ? prepared.Triggers[index].Fires : Array.Empty<TriggerFire>();
    }

    public IReadOnlyList<TriggerReport> Reports(Storyboard storyboard)
    {
        var reports = new List<TriggerReport>();
        foreach (var sprite in storyboard.Sprites)
        {
            var shown = sprite.Exported();
            var prepared = Prepare(shown);
            var originals = sprite.Nodes.OfType<SbTrigger>().ToArray();
            prepared.Cancelled ??= FindCancellations(shown, prepared);
            for (int n = 0; n < originals.Length && n < prepared.Triggers.Length; n++)
            {
                var t = prepared.Triggers[n];
                var notes = new List<(TriggerNote, string)>();
                var candidates = MatchesOf(t.Trigger.Name, t.Condition);
                if (t.Condition == null) notes.Add((TriggerNote.InvalidName, ""));
                else if (candidates.Length == 0) notes.Add((TriggerNote.NoMatchingEvent, t.Trigger.Name));
                else if (t.Fires.Length == 0)
                {
                    double near = candidates.Select(i => stream.Events[i].Time).MinBy(time => time < t.Trigger.Start ? t.Trigger.Start - time : time - t.Trigger.End);
                    notes.Add((TriggerNote.OutsideWindow, near.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                }
                int dead = t.Fires.Count(f => !f.Alive);
                if (dead > 0) notes.Add((TriggerNote.FiresOutsideLifetime, dead.ToString()));
                if (!shown.Timeline.HasCommands) notes.Add((TriggerNote.TriggerOnlySprite, ""));
                if (t.Trigger.Group != 0) notes.Add((TriggerNote.GroupNumberUnused, t.Trigger.Group.ToString()));
                if (t.Trigger.Start == 0 && t.Trigger.End == 0) notes.Add((TriggerNote.ZeroWindow, ""));
                if (t.Fires.Length > 0 && sprite.Layer is SbLayer.Fail or SbLayer.Pass
                    && t.Fires.All(f => stream.FailingAt(f.Time) != (sprite.Layer == SbLayer.Fail)))
                    notes.Add((TriggerNote.HiddenLayer, t.Fires.Length.ToString()));
                if (prepared.Cancelled.TryGetValue(t.Trigger, out var cancelled))
                    foreach (var c in cancelled)
                        notes.Add((TriggerNote.CancelsNormalCommands, FormattableString.Invariant($"{c.Channel}|{c.Start}|{c.Time}")));
                if (stream.Approximate) notes.Add((TriggerNote.ApproximateMode, ""));
                var status = t.Condition == null ? TriggerStatus.Invalid : t.Fires.Length == 0 ? TriggerStatus.NeverFires
                    : !shown.Timeline.HasCommands || t.Fires.All(f => !f.Alive) || notes.Any(n => n.Item1 == TriggerNote.HiddenLayer) ? TriggerStatus.FiresHidden : TriggerStatus.Fires;
                reports.Add(new(sprite, originals[n], string.IsNullOrEmpty(sprite.Difficulty), status, t.Fires, notes));
            }
        }
        return reports;
    }

    private Dictionary<SbTrigger, List<(int Channel, double Start, double Time)>> FindCancellations(SbSprite sprite, Prepared prepared)
    {
        var result = new Dictionary<SbTrigger, List<(int, double, double)>>();
        var channels = Enumerable.Range(0, 13).Select(i => new ReplayChannel(i < 10
            ? sprite.Timeline.Channels[i].Segments : NormalParameters(sprite, "HVA"[i - 10]))).ToArray();
        foreach (var (trigger, fire) in OrderedFires(prepared))
            foreach (var (index, segment) in trigger.Segments)
            {
                channels[index] = channels[index].Copy(fire.Time);
                channels[index].Insert(new ShiftedSeg(segment, fire.Time), fire.EventIndex, normal =>
                {
                    if (!result.TryGetValue(trigger.Trigger, out var list)) result[trigger.Trigger] = list = new();
                    list.Add((index, normal.Start, fire.Time));
                });
            }
        return result;
    }

    private static IEnumerable<Seg> NormalParameters(SbSprite sprite, char parameter)
    {
        var spans = new List<Seg>();
        foreach (var node in sprite.Nodes)
        {
            if (node is SbCommand { Kind: CommandKind.P } c && char.ToUpperInvariant(c.Param) == parameter)
                spans.Add(new ValueSeg(c.Start, Math.Max(c.Start, c.End), 0, 1, 1, null));
            else if (node is SbLoop loop)
                foreach (var p in loop.Commands.Where(p => p.Kind == CommandKind.P && char.ToUpperInvariant(p.Param) == parameter))
                    spans.Add(new ValueSeg((double)loop.Start + p.Start,
                        (double)loop.Start + (long)(loop.Iterations - 1) * loop.IterationLength + Math.Max(p.Start, p.End), 0, 1, 1, null));
        }
        return spans.OrderBy(s => s.Start);
    }

    private static IEnumerable<(Compiled Trigger, TriggerFire Fire)> OrderedFires(Prepared prepared)
    {
        var queue = new PriorityQueue<(int Trigger, int Fire), (int Event, int Trigger)>();
        for (int t = 0; t < prepared.Triggers.Length; t++)
            if (prepared.Triggers[t].Fires.Length > 0) queue.Enqueue((t, 0), (prepared.Triggers[t].Fires[0].EventIndex, t));
        while (queue.TryDequeue(out var item, out _))
        {
            var trigger = prepared.Triggers[item.Trigger];
            yield return (trigger, trigger.Fires[item.Fire]);
            int next = item.Fire + 1;
            if (next < trigger.Fires.Length) queue.Enqueue((item.Trigger, next), (trigger.Fires[next].EventIndex, item.Trigger));
        }
    }

    private Replay Build(SbSprite sprite, Prepared prepared)
    {
        var stages = new List<Stage>();
        var channels = new ReplayChannel[13];
        for (int i = 0; i < 10; i++) channels[i] = new(sprite.Timeline.Channels[i].Segments);
        for (int i = 10; i < 13; i++) channels[i] = new(new[] { new ParameterBase(sprite.Timeline, "HVA"[i - 10]) });
        // Merge the ordered firing lists, preserving trigger order for a shared event.
        var queue = new PriorityQueue<(int Trigger, int Fire), (int Event, int Trigger)>();
        for (int t = 0; t < prepared.Triggers.Length; t++)
            if (prepared.Triggers[t].Fires.Length > 0) queue.Enqueue((t, 0), (prepared.Triggers[t].Fires[0].EventIndex, t));
        int current = -1;
        ReplayChannel[] next = channels;
        bool[] copied = new bool[13];
        while (queue.TryDequeue(out var item, out var priority))
        {
            if (priority.Event != current)
            {
                if (current >= 0) { channels = next; stages.Add(new(stream.Events[current].Time, channels)); }
                current = priority.Event; next = (ReplayChannel[])channels.Clone(); copied = new bool[13];
            }
            var t = prepared.Triggers[item.Trigger];
            double time = t.Fires[item.Fire].Time;
            foreach (var (index, segment) in t.Segments)
            {
                if (!copied[index]) { next[index] = next[index].Copy(time); copied[index] = true; }
                next[index].Insert(new ShiftedSeg(segment, time), current);
            }
            int following = item.Fire + 1;
            if (following < t.Fires.Length) queue.Enqueue((item.Trigger, following), (t.Fires[following].EventIndex, item.Trigger));
        }
        if (current >= 0) stages.Add(new(stream.Events[current].Time, next));
        return new(stages.ToArray());
    }

    private sealed record Stage(double Time, ReplayChannel[] Channels);
    private sealed record Replay(Stage[] Stages)
    {
        public SpriteState Evaluate(SbSprite sprite, double time)
        {
            var state = sprite.Timeline.Evaluate(time, sprite);
            int lo = 0, hi = Stages.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (Stages[mid].Time <= time) lo = mid + 1; else hi = mid;
            }
            if (lo == 0) return state;
            var c = Stages[lo - 1].Channels;
            state.X = c[0].At(time, sprite.X); state.Y = c[1].At(time, sprite.Y);
            state.Scale = c[2].At(time, 1); state.ScaleX = c[3].At(time, 1); state.ScaleY = c[4].At(time, 1);
            state.Rotation = c[5].At(time, 0);
            state.R = c[6].At(time, 255); state.G = c[7].At(time, 255); state.B = c[8].At(time, 255);
            state.Opacity = c[9].At(time, 1);
            state.FlipH = c[10].At(time, 0) > 0; state.FlipV = c[11].At(time, 0) > 0; state.Additive = c[12].At(time, 0) > 0;
            return state;
        }
    }

    private sealed class ReplayChannel
    {
        private readonly List<Seg> segments;
        private readonly double? initial;
        public ReplayChannel(IEnumerable<Seg> source)
        {
            segments = source.ToList();
            initial = segments.Count > 0 ? segments[0].Initial : null;
        }
        private ReplayChannel(List<Seg> segments, double? initial) { this.segments = segments; this.initial = initial; }
        public ReplayChannel Copy(double eventTime)
        {
            // Earlier stages retain the past for seeking. This stage only needs the last completed
            // value plus active/future segments, preventing quadratic history growth on repeated hits.
            int lastCompleted = segments.FindLastIndex(s => s.End <= eventTime);
            var retained = new List<Seg>();
            for (int i = 0; i < segments.Count; i++)
                if (i == lastCompleted || segments[i].End > eventTime) retained.Add(segments[i]);
            return new(retained, initial);
        }
        public void Insert(Seg segment, int source = 0, Action<Seg>? cancelled = null)
        {
            for (int i = segments.Count - 1; i >= 0; i--)
            {
                var previous = segments[i];
                if (previous.Start >= segment.Start || previous.End > segment.Start)
                {
                    if (previous is not SourcedSeg || previous is SourcedSeg { Source: -1 }) cancelled?.Invoke(previous);
                    if (previous.Start >= segment.Start) segments.RemoveAt(i);
                    else segments[i] = new SourcedSeg(new ClippedSeg(previous, segment.Start), previous is SourcedSeg tagged ? tagged.Source : -1);
                }
            }
            segments.Add(new SourcedSeg(segment, source));
        }
        public double At(double time, double fallback)
        {
            Seg? latest = null;
            foreach (var segment in segments)
            {
                if (time < segment.Start) break;
                if (time < segment.End) return segment.At(time);
                latest = segment;
            }
            // Trigger start values must not initialise a property before the command actually starts.
            return latest?.At(time) ?? initial ?? fallback;
        }
    }

    private sealed class SourcedSeg : Seg
    {
        private readonly Seg segment;
        public int Source { get; }
        public SourcedSeg(Seg segment, int source) { this.segment = segment; Source = source; Start = segment.Start; End = segment.End; }
        public override double Initial => segment.Initial;
        public override double At(double time) => segment.At(time);
    }

    private sealed class ShiftedSeg : Seg
    {
        private readonly Seg source;
        private readonly double offset;
        public ShiftedSeg(Seg source, double offset)
        {
            this.source = source; this.offset = offset;
            Start = source.Start + offset; End = source.End + offset;
        }
        public override double Initial => source.Initial;
        public override double At(double time) => source.At(time - offset);
    }
    private sealed class ClippedSeg : Seg
    {
        private readonly Seg source;
        public ClippedSeg(Seg source, double end) { this.source = source; Start = source.Start; End = end; }
        public override double Initial => source.Initial;
        public override double At(double time) => source.At(Math.Min(time, End));
    }
    private sealed class ParameterBase : Seg
    {
        private readonly SpriteTimeline timeline;
        private readonly char parameter;
        public ParameterBase(SpriteTimeline timeline, char parameter)
        {
            this.timeline = timeline; this.parameter = parameter;
            Start = double.NegativeInfinity; End = double.PositiveInfinity;
        }
        public override double Initial => timeline.ParameterAt(parameter, double.NegativeInfinity) ? 1 : 0;
        public override double At(double time) => timeline.ParameterAt(parameter, time) ? 1 : 0;
    }
    private sealed class ValueSeg : Seg
    {
        private readonly KeySeg value;
        public ValueSeg(double start, double end, int easing, double from, double to, EasingCurve.Shape? curve)
        {
            Start = start; End = end;
            value = new KeySeg(0, 1, easing, from, to, curve);
        }
        public override double Initial => value.Initial;
        public override double At(double time) => value.At(End <= Start ? 1 : (time - Start) / (End - Start));
    }
}
