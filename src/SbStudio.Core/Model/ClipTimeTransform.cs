namespace SbStudio.Core;

/// <summary>An affine change of a whole clip's command times, prepared on copies before editing.</summary>
public sealed class ClipTimeTransform
{
    public double Scale { get; }
    public double Offset { get; }
    private long expanded;
    private const int ExpansionLimit = 1_000_000;

    public ClipTimeTransform(double scale, double offset)
    {
        if (!double.IsFinite(scale) || scale == 0 || !double.IsFinite(offset))
            throw new InvalidOperationException(L.T("clipTime.invalid"));
        Scale = scale;
        Offset = offset;
    }

    public static ClipTimeTransform Between(int from, int to, int start, int end, bool reverse = false)
    {
        if (to <= from || end <= start) throw new InvalidOperationException(L.T("clipTime.invalid"));
        double scale = ((double)end - start) / ((double)to - from);
        return reverse ? new(-scale, start + scale * to) : new(scale, start - scale * from);
    }

    public int Map(double time)
    {
        double result = Math.Round(Offset + Scale * time);
        if (!double.IsFinite(result) || result <= int.MinValue || result >= int.MaxValue)
            throw new InvalidOperationException(L.T("clipTime.overflow"));
        return (int)result;
    }

    public SbSprite Sprite(SbSprite original)
    {
        var copy = (SbSprite)original.Clone();
        copy.Nodes = Nodes(original.Nodes);
        copy.DepthCommands = Commands(original.DepthCommands, true);
        if (copy.IsAnimation)
        {
            copy.FrameDelay *= Math.Abs(Scale);
            if (!double.IsFinite(copy.FrameDelay) || copy.FrameDelay <= 0)
                throw new InvalidOperationException(L.T("clipTime.invalid"));
            copy.VariableSource = null;
        }
        copy.TouchCopy();
        return copy;
    }

    public List<SbNode> Nodes(IEnumerable<SbNode> source)
    {
        var result = new List<SbNode>();
        foreach (var node in source)
        {
            switch (node)
            {
                case SbCommand command:
                    result.AddRange(Command(command, false));
                    break;
                case SbLoop loop when loop.Commands.Count > 0:
                {
                    double period = Math.Abs(Scale) * loop.IterationLength;
                    // Keep ordinary loops compact; fractional periods are mapped pass by pass to avoid drift.
                    if (Math.Abs(period - Math.Round(period)) > 1e-7)
                    {
                        Reserve((long)loop.Iterations * loop.Commands.Count);
                        for (int pass = 0; pass < loop.Iterations; pass++)
                            foreach (var c in loop.Commands)
                            {
                                var flat = c.CloneCommand();
                                flat.Start = CheckedTime((double)loop.Start + (double)pass * loop.IterationLength + c.Start);
                                flat.End = CheckedTime((double)loop.Start + (double)pass * loop.IterationLength + c.End);
                                result.AddRange(Command(flat, false));
                            }
                        break;
                    }
                    double magnitude = Math.Abs(Scale);
                    double pivot = (double)loop.CommandsStart + loop.CommandsEnd;
                    var relative = new ClipTimeTransform(Scale, Scale < 0 ? magnitude * pivot : 0);
                    var copy = (SbLoop)loop.Clone();
                    copy.VariableSource = null;
                    copy.Commands = relative.Commands(loop.Commands, false);
                    double origin = Scale < 0
                        ? (double)loop.Start + (double)(loop.Iterations - 1) * loop.IterationLength + pivot
                        : loop.Start;
                    copy.Start = Map(origin);
                    if (copy.Commands.Count > 0)
                        CheckedTime((double)copy.Start + copy.CommandsStart + (double)copy.IterationLength * copy.Iterations);
                    result.Add(copy);
                    break;
                }
                case SbTrigger trigger:
                {
                    var copy = (SbTrigger)trigger.Clone();
                    copy.VariableSource = null;
                    int start = trigger.Start == int.MinValue ? int.MinValue : Map(trigger.Start);
                    int end = trigger.End == int.MaxValue ? int.MaxValue : Map(trigger.End);
                    if (Scale < 0)
                    {
                        start = trigger.End == int.MaxValue ? int.MinValue : Map(trigger.End);
                        end = trigger.Start == int.MinValue ? int.MaxValue : Map(trigger.Start);
                    }
                    copy.Start = start;
                    copy.End = end;
                    var relative = new ClipTimeTransform(Scale,
                        Scale < 0 ? Math.Abs(Scale) * ((double)trigger.CommandsStart + trigger.CommandsEnd) : 0);
                    copy.Commands = relative.Commands(trigger.Commands, false);
                    result.Add(copy);
                    break;
                }
                default:
                    result.Add(node.Clone());
                    break;
            }
        }
        if (Scale < 0) result.Reverse();
        return result;
    }

    public List<SbCommand> Commands(IEnumerable<SbCommand> source, bool curvesAllowed)
    {
        var result = source.SelectMany(c => Command(c, curvesAllowed)).ToList();
        if (Scale < 0) result.Reverse();
        return result;
    }

    private IEnumerable<SbCommand> Command(SbCommand original, bool curvesAllowed)
    {
        if (original.End < original.Start) throw new InvalidOperationException(L.T("clipTime.badCommands"));
        var copy = original.CloneCommand();
        copy.VariableSource = null;
        copy.Start = Map(Scale < 0 ? original.End : original.Start);
        copy.End = Map(Scale < 0 ? original.Start : original.End);
        if (Scale >= 0 || copy.Kind == CommandKind.P) return [copy];
        (copy.From, copy.To) = (copy.To, copy.From);
        if (copy.Curve is {} curve)
        {
            var reversed = new EasingCurve();
            reversed.Points.AddRange(curve.Points.AsEnumerable().Reverse().Select(p => (1 - p.X, 1 - p.Y)));
            copy.Curve = reversed;
            if (curvesAllowed) return [copy];
        }
        else if (ReverseEasing(copy.Easing) is {} easing)
        {
            copy.Easing = easing;
            return [copy];
        }
        if (copy.End == copy.Start) { copy.Easing = 0; copy.Curve = null; return [copy]; }
        // Elastic half/quarter inverses have no standard OSB easing. Approximate them with ordinary commands.
        var shape = original.Curve?.Compile();
        double Value(double t) => 1 - (shape?.At(1 - t) ?? Easing.Apply(original.Easing, 1 - t));
        double delta = original.From.Zip(original.To, (a, b) => Math.Abs(b - a)).DefaultIfEmpty(1).Max();
        var points = new SortedDictionary<int, double> { [copy.Start] = 0, [copy.End] = 1 };
        void Split(int a, int b, double va, double vb)
        {
            if ((long)b - a <= 1) return;
            double worst = 0;
            for (int n = 1; n <= 3; n++)
            {
                double at = a + ((double)b - a) * n / 4;
                double actual = Value((at - copy.Start) / ((double)copy.End - copy.Start));
                worst = Math.Max(worst, Math.Abs(actual - (va + (vb - va) * n / 4)) * delta);
            }
            if (worst <= .001) return;
            int middle = (int)((long)a + ((long)b - a) / 2);
            double vm = Value((middle - (double)copy.Start) / ((double)copy.End - copy.Start));
            Reserve(1); points[middle] = vm;
            Split(a, middle, va, vm); Split(middle, b, vm, vb);
        }
        Split(copy.Start, copy.End, 0, 1);
        var pairs = points.ToArray();
        var commands = new List<SbCommand>();
        for (int i = 1; i < pairs.Length; i++)
        {
            double[] At(double value) => copy.From.Zip(copy.To, (a, b) => a + (b - a) * value).ToArray();
            commands.Add(new(copy.Kind, 0, pairs[i - 1].Key, pairs[i].Key, At(pairs[i - 1].Value), At(pairs[i].Value)));
        }
        return commands;
    }

    public static int? ReverseEasing(int easing) => easing switch
    {
        0 => 0, 1 => 2, 2 => 1,
        3 => 4, 4 => 3, 6 => 7, 7 => 6, 9 => 10, 10 => 9, 12 => 13, 13 => 12,
        15 => 16, 16 => 15, 18 => 19, 19 => 18, 21 => 22, 22 => 21, 24 => 25, 25 => 24,
        29 => 30, 30 => 29, 32 => 33, 33 => 32,
        5 or 8 or 11 or 14 or 17 or 20 or 23 or 28 or 31 or 34 => easing,
        _ => null,
    };

    private static int CheckedTime(double value)
    {
        if (!double.IsFinite(value) || value <= int.MinValue || value >= int.MaxValue)
            throw new InvalidOperationException(L.T("clipTime.overflow"));
        return checked((int)Math.Round(value));
    }

    private void Reserve(long count)
    {
        expanded += count;
        if (expanded > ExpansionLimit) throw new InvalidOperationException(L.T("clipTime.tooLarge"));
    }
}
