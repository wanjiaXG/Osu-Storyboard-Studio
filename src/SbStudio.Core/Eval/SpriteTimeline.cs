namespace SbStudio.Core;

public struct SpriteState
{
    public double X, Y;
    public double Scale, ScaleX, ScaleY;
    public double Rotation;
    public double R, G, B;
    public double Opacity;
    public bool FlipH, FlipV, Additive;
}

/// <summary>
/// Compiled form of a sprite's commands. Each scalar property is a channel of segments sorted by
/// start time. Rules follow osu!:
///  - before the first command of a property, the property holds that command's start value;
///  - after a command ends the property keeps its end value until the next command starts;
///  - when commands of the same property overlap, the one that started first wins while it runs;
///  - loops repeat their commands every (latest end - earliest start) milliseconds;
///  - finite P commands enable their parameter at the start and disable it at the end;
///  - a property's first P command initializes it before its start only when zero-length.
/// Triggers depend on gameplay and are not evaluated.
/// </summary>
public sealed class SpriteTimeline
{
    public bool HasCommands { get; private set; }
    public int StartTime { get; private set; }
    public int EndTime { get; private set; }
    public bool HasTriggers { get; private set; }

    private readonly ChannelSet channels = new();
    internal ChannelSet Channels => channels;
    private readonly List<ParamSpan> paramSpans = new();

    private SpriteTimeline() { }

    public static SpriteTimeline Build(SbSprite sprite)
    {
        var tl = new SpriteTimeline();
        int start = int.MaxValue, end = int.MinValue;

        foreach (var node in sprite.Nodes)
        {
            switch (node)
            {
                case SbCommand c:
                    if (c.Kind == CommandKind.P)
                        tl.paramSpans.Add(new ParamSpan(c.Param, c.Start, c.End, 0, 0, 1));
                    else
                        tl.channels.Add(c);
                    start = Math.Min(start, c.Start);
                    end = Math.Max(end, Math.Max(c.Start, c.End));
                    break;

                case SbLoop loop when loop.Commands.Count > 0:
                    int length = loop.IterationLength;
                    int iterations = loop.Iterations;
                    var inner = new ChannelSet();
                    foreach (var c in loop.Commands)
                    {
                        if (c.Kind == CommandKind.P)
                            tl.paramSpans.Add(new ParamSpan(c.Param, c.Start, c.End, loop.Start, length, iterations));
                        else
                            inner.Add(c);
                    }
                    inner.Seal();
                    for (int i = 0; i < ChannelSet.Count; i++)
                    {
                        if (!inner[i].IsEmpty)
                            tl.channels[i].Add(new LoopSeg(inner[i], loop.Start, length, iterations));
                    }
                    start = Math.Min(start, loop.AbsoluteStart);
                    end = Math.Max(end, loop.AbsoluteEnd);
                    break;

                case SbTrigger:
                    tl.HasTriggers = true;
                    break;
            }
        }

        tl.channels.Seal();
        tl.HasCommands = start != int.MaxValue;
        tl.StartTime = tl.HasCommands ? start : 0;
        tl.EndTime = tl.HasCommands ? end : 0;
        // lazer applies an initial value only from the first command of each parameter property.
        var orderedParameters = tl.paramSpans.OrderBy(span => span.FirstStart).ThenBy(span => span.FirstEnd).ToArray();
        tl.paramSpans.Clear();
        tl.paramSpans.AddRange(orderedParameters);
        return tl;
    }

    public bool IsActive(double time) => HasCommands && time >= StartTime && time <= EndTime;

    /// <summary>
    /// The opacity <see cref="Evaluate"/> would report. Most sprites that are alive at a given moment are
    /// faded out, and this tells without working out everything else about them.
    /// </summary>
    public double OpacityAt(double time) => channels[ChannelSet.Opacity].At(time, 1);

    public SpriteState Evaluate(double time, SbSprite sprite)
    {
        SpriteState st;
        st.X = channels[ChannelSet.X].At(time, sprite.X);
        st.Y = channels[ChannelSet.Y].At(time, sprite.Y);
        st.Scale = channels[ChannelSet.Scale].At(time, 1);
        st.ScaleX = channels[ChannelSet.ScaleX].At(time, 1);
        st.ScaleY = channels[ChannelSet.ScaleY].At(time, 1);
        st.Rotation = channels[ChannelSet.Rotation].At(time, 0);
        st.R = channels[ChannelSet.R].At(time, 255);
        st.G = channels[ChannelSet.G].At(time, 255);
        st.B = channels[ChannelSet.B].At(time, 255);
        st.Opacity = channels[ChannelSet.Opacity].At(time, 1);
        st.FlipH = ParameterAt('H', time);
        st.FlipV = ParameterAt('V', time);
        st.Additive = ParameterAt('A', time);
        return st;
    }

    internal bool ParameterAt(char parameter, double time)
    {
        bool value = false, initialized = false;
        double latestTransition = double.NegativeInfinity;
        foreach (var span in paramSpans)
        {
            if (span.Param != parameter) continue;
            if (!initialized)
            {
                value = span.FirstStart == span.FirstEnd;
                initialized = true;
            }
            if (span.TryTransitionAt(time, out double transition, out bool nextValue) && transition >= latestTransition)
            {
                latestTransition = transition;
                value = nextValue;
            }
        }
        return value;
    }

    /// <summary>Animation frame shown at <paramref name="time"/>.</summary>
    public int FrameAt(double time, SbSprite sprite)
    {
        if (!sprite.IsAnimation || sprite.FrameCount <= 1) return 0;
        double delay = Math.Max(1, sprite.FrameDelay);
        long frame = (long)Math.Floor(Math.Max(0, time - StartTime) / delay);
        return sprite.LoopType == SbLoopType.LoopOnce
            ? (int)Math.Min(frame, sprite.FrameCount - 1)
            : (int)(frame % sprite.FrameCount);
    }

    private readonly struct ParamSpan
    {
        public readonly char Param;
        private readonly int start, end, loopStart, loopLength, iterations;
        public double FirstStart => (double)loopStart + start;
        public double FirstEnd => (double)loopStart + end;

        public ParamSpan(char param, int start, int end, int loopStart, int loopLength, int iterations)
        {
            Param = char.ToUpperInvariant(param);
            this.start = start;
            this.end = Math.Max(start, end);
            this.loopStart = loopStart;
            this.loopLength = loopLength;
            this.iterations = iterations;
        }

        public bool TryTransitionAt(double time, out double transition, out bool value)
        {
            transition = LatestOccurrence(FirstStart, time);
            value = true;
            double endTransition = LatestOccurrence(FirstEnd, time);
            // At a loop boundary, the next iteration starts as the previous one ends.
            if (end > start && endTransition > transition)
            {
                transition = endTransition;
                value = false;
            }
            return !double.IsNegativeInfinity(transition);
        }

        private double LatestOccurrence(double first, double time)
        {
            if (time < first) return double.NegativeInfinity;
            double iteration = iterations > 1 && loopLength > 0
                ? Math.Min(Math.Floor((time - first) / loopLength), iterations - 1) : 0;
            return first + iteration * loopLength;
        }
    }
}

internal abstract class Seg
{
    public double Start, End;
    public abstract double Initial { get; }
    public abstract double At(double time);
}

internal sealed class KeySeg : Seg
{
    private readonly int easing;
    private readonly double from, to;
    /// <summary>A transition drawn by hand, in place of the easing; see <see cref="EasingCurve"/>.</summary>
    private readonly EasingCurve.Shape? curve;

    public KeySeg(int start, int end, int easing, double from, double to, EasingCurve.Shape? curve = null)
    {
        Start = start;
        End = Math.Max(start, end);
        this.easing = easing;
        this.from = from;
        this.to = to;
        this.curve = curve;
    }

    public override double Initial => from;

    public override double At(double time)
    {
        if (time >= End) return to;
        if (time <= Start) return from;
        double t = (time - Start) / (End - Start);
        t = curve != null ? curve.At(t) : Easing.Apply(easing, t);
        return from + (to - from) * t;
    }
}

internal sealed class LoopSeg : Seg
{
    private readonly Channel inner;
    private readonly double loopStart, length;
    private readonly int iterations;

    public LoopSeg(Channel inner, int loopStart, int length, int iterations)
    {
        this.inner = inner;
        this.loopStart = loopStart;
        this.length = length;
        this.iterations = iterations;
        Start = loopStart + inner.FirstStart;
        End = loopStart + (double)(iterations - 1) * length + inner.LastEnd;
    }

    public override double Initial => inner.Initial;

    public override double At(double time)
    {
        if (time <= Start) return Initial;
        double k = iterations - 1;
        if (length > 0)
            k = Math.Min(k, Math.Floor((time - Start) / length));
        return inner.At(time - loopStart - k * length, Initial);
    }
}

internal sealed class Channel
{
    private readonly List<Seg> pending = new();
    private Seg[] segs = Array.Empty<Seg>();
    private bool overlaps;
    internal IReadOnlyList<Seg> Segments => segs;

    public bool IsEmpty => pending.Count == 0;
    public double FirstStart => segs[0].Start;
    public double LastEnd { get; private set; }
    public double Initial => segs[0].Initial;

    public void Add(Seg seg) => pending.Add(seg);

    public void Seal()
    {
        // OrderBy is stable: commands with equal start keep their file order.
        segs = pending.OrderBy(s => s.Start).ToArray();
        overlaps = false;
        double maxEnd = double.NegativeInfinity;
        for (int i = 0; i < segs.Length; i++)
        {
            if (segs[i].Start < maxEnd) overlaps = true;
            maxEnd = Math.Max(maxEnd, segs[i].End);
        }
        LastEnd = maxEnd;
    }

    public double At(double time, double fallback)
    {
        if (segs.Length == 0) return fallback;
        if (time < segs[0].Start) return segs[0].Initial;

        // Last segment that has started.
        int lo = 0, hi = segs.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (segs[mid].Start <= time) lo = mid;
            else hi = mid - 1;
        }

        if (overlaps)
        {
            for (int i = 0; i < lo; i++)
                if (time < segs[i].End) return segs[i].At(time);
        }
        return segs[lo].At(time);
    }
}

internal sealed class ChannelSet
{
    public const int X = 0, Y = 1, Scale = 2, ScaleX = 3, ScaleY = 4, Rotation = 5, R = 6, G = 7, B = 8, Opacity = 9;
    public const int Count = 10;

    private readonly Channel[] channels = new Channel[Count];

    public ChannelSet()
    {
        for (int i = 0; i < Count; i++) channels[i] = new Channel();
    }

    public Channel this[int index] => channels[index];

    public void Add(SbCommand c)
    {
        var curve = c.Curve?.Compile();
        switch (c.Kind)
        {
            case CommandKind.F: Add(Opacity, c, 0, curve); break;
            case CommandKind.M: Add(X, c, 0, curve); Add(Y, c, 1, curve); break;
            case CommandKind.MX: Add(X, c, 0, curve); break;
            case CommandKind.MY: Add(Y, c, 0, curve); break;
            case CommandKind.S: Add(Scale, c, 0, curve); break;
            case CommandKind.V: Add(ScaleX, c, 0, curve); Add(ScaleY, c, 1, curve); break;
            case CommandKind.R: Add(Rotation, c, 0, curve); break;
            case CommandKind.C: Add(R, c, 0, curve); Add(G, c, 1, curve); Add(B, c, 2, curve); break;
        }
    }

    private void Add(int channel, SbCommand c, int component, EasingCurve.Shape? curve)
    {
        if (component >= c.From.Length || component >= c.To.Length) return;
        channels[channel].Add(new KeySeg(c.Start, c.End, c.Easing, c.From[component], c.To[component], curve));
    }

    public void Seal()
    {
        foreach (var ch in channels) ch.Seal();
    }
}
