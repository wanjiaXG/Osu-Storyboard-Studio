using SbStudio.Core;

namespace SbStudio.App;

internal enum TimingBpmDirection { Previous, Next }

internal sealed record TimingBpmResult(TimingPoint Reference, int Target, int Steps, int Divisor,
    double ExactBpm, double Bpm, double GridOffset, TimingBpmDirection Direction = TimingBpmDirection.Previous)
{
    public double Beats => (double)Steps / Divisor;
    public double StartOffset => Direction == TimingBpmDirection.Next ? Target : Reference.Offset;
    public double EndOffset => Direction == TimingBpmDirection.Next ? Reference.Offset : Target;
    public double ErrorMs => GridOffset - EndOffset;
    public bool Aligned => ErrorMs == 0;
}

internal static class TimingBpmCalculator
{
    public static TimingPoint? Previous(IReadOnlyList<TimingPoint> points, int target) =>
        points.Where(p => double.IsFinite(p.Offset) && p.Offset < target).OrderBy(p => p.Offset).LastOrDefault();

    public static TimingPoint? Next(IReadOnlyList<TimingPoint> points, int target) =>
        points.Where(p => double.IsFinite(p.Offset) && p.Offset > target).OrderBy(p => p.Offset).FirstOrDefault();

    public static TimingPoint? Reference(IReadOnlyList<TimingPoint> points, int target, TimingBpmDirection direction) =>
        direction == TimingBpmDirection.Next ? Next(points, target) : Previous(points, target);

    // Before the first section, a new red line inherits the next line's BPM and meter.
    internal static TimingPoint StartPoint(IReadOnlyList<TimingPoint> points, int target, TimingPoint reference, TimingBpmDirection direction) =>
        direction == TimingBpmDirection.Previous ? reference : points.Where(p => double.IsFinite(p.Offset) && p.Offset <= target)
            .OrderBy(p => p.Offset).LastOrDefault() ?? reference;

    public static int EstimateSteps(IReadOnlyList<TimingPoint> points, int target, int divisor, TimingBpmDirection direction = TimingBpmDirection.Previous)
    {
        var reference = Reference(points, target, direction);
        if (reference == null || divisor <= 0) return 1;
        var start = StartPoint(points, target, reference, direction);
        return (int)Math.Clamp(Math.Round(Math.Abs(target - reference.Offset) / start.BeatLength * divisor), 1, int.MaxValue);
    }

    public static TimingBpmResult? Calculate(IReadOnlyList<TimingPoint> points, int target, int steps, int divisor, int decimals = 3,
        TimingBpmDirection direction = TimingBpmDirection.Previous)
    {
        var reference = Reference(points, target, direction);
        if (reference == null || steps <= 0 || !TimingUtil.Divisors.Contains(divisor) || decimals is < 0 or > 3) return null;
        double exact = 60000.0 * steps / divisor / Math.Abs(target - reference.Offset);
        if (!double.IsFinite(exact) || exact <= 0 || exact > 1000) return null;
        TimingBpmResult? best = null;
        // Rounded-up BPM can put the last tick a millisecond early because osu! truncates tick times.
        // Check neighbouring values at the chosen precision against the exact grid, not just the ideal formula.
        double rounded = Math.Round(exact, decimals);
        double step = Math.Pow(10, -decimals);
        for (int delta = -1; delta <= 1; delta++)
        {
            double bpm = Math.Round(rounded + delta * step, decimals);
            if (Evaluate(points, target, steps, divisor, bpm, direction) is not { } result) continue;
            if (best == null || Math.Abs(result.ErrorMs) < Math.Abs(best.ErrorMs)
                || Math.Abs(result.ErrorMs) == Math.Abs(best.ErrorMs) && Math.Abs(bpm - exact) < Math.Abs(best.Bpm - exact)) best = result;
        }
        return best;
    }

    /// <summary>Where the chosen interval's final snap lands; null when the inputs cannot be used.</summary>
    public static TimingBpmResult? Evaluate(IReadOnlyList<TimingPoint> points, int target, int steps, int divisor, double bpm,
        TimingBpmDirection direction = TimingBpmDirection.Previous)
    {
        var reference = Reference(points, target, direction);
        if (reference == null || steps <= 0 || !TimingUtil.Divisors.Contains(divisor) || TimingPointEditing.ValidateBpm(bpm) != null) return null;
        double exact = 60000.0 * steps / divisor / Math.Abs(target - reference.Offset);
        var start = StartPoint(points, target, reference, direction);
        double offset = direction == TimingBpmDirection.Next ? target : reference.Offset;
        bool firstSection = !points.Any(p => double.IsFinite(p.Offset) && p.Offset < offset);
        var probe = new TimingPoint { Offset = offset, Meter = start.Meter, Bpm = bpm };
        return new TimingBpmResult(reference, target, steps, divisor, exact, bpm, TimingUtil.TickAt(probe, steps, divisor, firstSection), direction);
    }
}
