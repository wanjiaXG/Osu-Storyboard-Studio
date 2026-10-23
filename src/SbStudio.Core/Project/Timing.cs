using System.Text.Json.Serialization;

namespace SbStudio.Core;

public sealed class TimingPoint
{
    private double beatLength = 500;

    public double Offset { get; set; }

    /// <summary>
    /// Milliseconds per beat, exactly as a .osu file gives it. Tick times are cut down to whole milliseconds,
    /// so even the last digit can decide which millisecond a tick falls on.
    /// </summary>
    public double BeatLength
    {
        get => beatLength;
        set => beatLength = value > 0 && double.IsFinite(value) ? value : 500;
    }

    public int Meter { get; set; } = 4;

    [JsonIgnore]
    public double Bpm
    {
        get => 60000.0 / beatLength;
        set => BeatLength = 60000.0 / value;
    }

    /// <summary>Project files written before the beat length was stored only have the BPM.</summary>
    [JsonInclude, JsonPropertyName("bpm")]
    private double SavedBpm { set => Bpm = value; }
}

/// <summary>One line of the beat snap grid.</summary>
/// <param name="Time">A whole millisecond.</param>
/// <param name="Divisor">The coarsest divisor whose grid contains this tick: 1 for a beat, 2 for a half beat, and so on.</param>
/// <param name="Measure">The tick starts a measure.</param>
public readonly record struct SnapTick(double Time, int Divisor, bool Measure);

/// <summary>
/// The beat snap grid, as in the osu! editor: each timing section has ticks every beat / divisor starting at
/// its offset. The first section also extends backwards; every later section replaces the grid from its
/// offset on. Ticks are whole milliseconds, the same ones the osu! editor stops on.
/// </summary>
public static class TimingUtil
{
    /// <summary>Beat snap divisors offered by osu!.</summary>
    public static readonly int[] Divisors = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 12, 16 };

    private const int MaxTicks = 50000;

    /// <summary>
    /// One timing section's ticks, computed like osu!'s editor does (BeatSnapValue): tick n is
    /// origin + n * beat / divisor with the fraction cut off. Rounding to the nearest millisecond instead
    /// would put about half of all ticks one millisecond later than osu! has them.
    /// </summary>
    private readonly struct Grid
    {
        // Past this many beats the origin is calculated in one step instead of being walked to.
        private const double MaxWalkedBeats = 100000;

        private readonly double origin;
        private readonly double increment;

        /// <summary>The number of the tick on the section's own offset.</summary>
        public readonly long First;

        public Grid(TimingPoint point, bool firstSection, int divisor)
        {
            double beat = point.BeatLength;
            increment = beat / divisor;
            origin = point.Offset;
            First = 0;
            if (!firstSection || origin <= 0) return;

            // osu! counts the first section's ticks from the last beat that is not after zero, and gets
            // there by taking off one beat at a time (BeatOffsetCloseToZeroAt). The same is done here so
            // that the rounding errors are the same too.
            long beats = 0;
            if (origin / beat <= MaxWalkedBeats)
            {
                for (; origin > 0; beats++) origin -= beat;
            }
            else
            {
                beats = (long)Math.Ceiling(origin / beat);
                origin -= beats * beat;
            }
            First = beats * divisor;
        }

        /// <summary>The time of tick <paramref name="count"/>.</summary>
        public long At(long count) => (long)(count * increment + origin);

        /// <summary>The tick nearest to <paramref name="time"/>; halfway between two, the later one.</summary>
        public long Nearest(double time)
        {
            long count = (long)((time - origin) / increment);
            if (time - origin < 0) count--;
            long left = At(count), right = At(count + 1);
            return time - left < right - time ? left : right;
        }

        /// <summary>The first tick after <paramref name="time"/>.</summary>
        public double After(double time)
        {
            if (EveryMillisecond) return Math.Floor(time) + 1;
            return At(CountAt(time) + 1);
        }

        /// <summary>The number of the tick at or before <paramref name="time"/>, counted from the section's own offset.</summary>
        public long StepAt(double time) => CountAt(time) - First;

        /// <summary>The last tick before <paramref name="time"/>.</summary>
        public double Before(double time)
        {
            if (EveryMillisecond) return Math.Ceiling(time) - 1;
            long count = CountAt(time);
            while (At(count) >= time) count--;
            return At(count);
        }

        /// <summary>Ticks less than a millisecond apart land on every millisecond there is.</summary>
        private bool EveryMillisecond => !(increment >= 1);

        /// <summary>The number of the last tick that is not after <paramref name="time"/>.</summary>
        private long CountAt(double time)
        {
            // Cutting off the fraction moves a tick by up to a millisecond, so the estimate can be one off.
            long count = (long)Math.Floor((time - origin) / increment);
            while (At(count) > time) count--;
            while (At(count + 1) <= time) count++;
            return count;
        }
    }

    /// <summary>A section's first tick; the grid of the section before it ends here.</summary>
    private static long Start(TimingPoint point) => (long)point.Offset;

    private static int IndexAt(IReadOnlyList<TimingPoint> points, double time)
    {
        int index = 0;
        for (int i = 1; i < points.Count; i++)
        {
            if (Start(points[i]) <= time) index = i;
            else break;
        }
        return index;
    }

    /// <summary>The timing section that covers <paramref name="time"/> (the first one also covers earlier times).</summary>
    public static TimingPoint? At(IReadOnlyList<TimingPoint> points, double time) =>
        points.Count == 0 ? null : points[IndexAt(points, time)];

    /// <summary>Time of a snap step counted from a timing point, using the editor's integer grid.</summary>
    public static double TickAt(TimingPoint point, long step, int divisor, bool firstSection = false)
    {
        if (divisor <= 0) throw new ArgumentOutOfRangeException(nameof(divisor));
        var grid = new Grid(point, firstSection, divisor);
        return grid.At(grid.First + step);
    }

    /// <summary>The tick of one timing point's 1/<paramref name="divisor"/> grid nearest a time, on the editor's integer grid.</summary>
    public static double NearestTick(TimingPoint point, double time, int divisor, bool firstSection = false)
    {
        if (divisor <= 0) throw new ArgumentOutOfRangeException(nameof(divisor));
        return new Grid(point, firstSection, divisor).Nearest(time);
    }

    /// <summary>One-based bar, beat and subdivision of the tick at or before time, including negative bars.</summary>
    public static (long Bar, int Beat, int Snap) PositionAt(IReadOnlyList<TimingPoint> points, double time, int divisor)
    {
        if (points.Count == 0 || divisor <= 0) return default;
        int section = IndexAt(points, time);
        var point = points[section];
        long step = new Grid(point, section == 0, divisor).StepAt(time);
        long perBar = (long)Math.Max(1, point.Meter) * divisor;
        long within = Mod(step, perBar);
        return ((step - within) / perBar + 1, (int)(within / divisor) + 1, (int)(within % divisor) + 1);
    }

    /// <summary>The nearest original offset strictly before or after time; null at the ends.</summary>
    public static double? AdjacentOffset(IReadOnlyList<TimingPoint> points, double time, int direction)
    {
        if (direction == 0 || !double.IsFinite(time)) return null;
        double? target = null;
        foreach (var point in points)
        {
            double offset = point.Offset;
            if (!double.IsFinite(offset)) continue;
            if (direction > 0 ? offset > time && (target == null || offset < target.Value)
                : offset < time && (target == null || offset > target.Value)) target = offset;
        }
        return target;
    }

    /// <summary>Nearest tick of the 1/<paramref name="divisor"/> grid. Returns the input when there is no timing.</summary>
    public static double Snap(IReadOnlyList<TimingPoint> points, double time, int divisor)
    {
        if (points.Count == 0 || divisor <= 0) return time;
        int s = IndexAt(points, time);
        long snapped = new Grid(points[s], s == 0, divisor).Nearest(time);
        if (s + 1 < points.Count)
        {
            // The next section starts its own grid, and its first tick may be the nearest one.
            long next = Start(points[s + 1]);
            if (snapped >= next || next - time <= time - snapped) return next;
        }
        return snapped;
    }

    /// <summary>The first tick after <paramref name="time"/>.</summary>
    public static double NextTick(IReadOnlyList<TimingPoint> points, double time, int divisor)
    {
        if (points.Count == 0 || divisor <= 0) return time;
        int s = IndexAt(points, time);
        double tick = new Grid(points[s], s == 0, divisor).After(time);
        if (s + 1 < points.Count && tick >= Start(points[s + 1])) return Start(points[s + 1]);
        return tick;
    }

    /// <summary>The last tick before <paramref name="time"/>.</summary>
    public static double PreviousTick(IReadOnlyList<TimingPoint> points, double time, int divisor)
    {
        if (points.Count == 0 || divisor <= 0) return time;
        int s = IndexAt(points, time);
        // Standing on the first tick of a section, the previous one belongs to the section before.
        if (s > 0 && time <= Start(points[s])) s--;
        return new Grid(points[s], s == 0, divisor).Before(time);
    }

    /// <summary>Which grid a tick belongs to: the smallest osu! divisor that lands on it.</summary>
    public static int DivisorOf(long index, int divisor)
    {
        long within = Mod(index, divisor);
        foreach (int d in Divisors)
            if (within * d % divisor == 0) return d;
        return divisor;
    }

    /// <summary>
    /// Ticks between two times, in order. Subdivisions closer together than <paramref name="minSpacing"/>
    /// milliseconds are left out, coarser ones first: when zoomed far out only beats, then only measures remain.
    /// </summary>
    public static IEnumerable<SnapTick> Ticks(IReadOnlyList<TimingPoint> points, int divisor, double from, double to, double minSpacing = 0)
    {
        if (divisor <= 0) yield break;
        int visited = 0;
        for (int s = 0; s < points.Count; s++)
        {
            var p = points[s];
            double start = s == 0 ? from : Math.Max(from, Start(p));
            // A section ends just before the next one begins; that offset is the next section's first tick.
            double end = s + 1 < points.Count ? Math.Min(to, Start(points[s + 1]) - 1) : to;
            if (end < start) continue;

            double beat = p.BeatLength;
            int meter = Math.Max(1, p.Meter);

            int shown = 0;
            foreach (int d in Divisors)
                if (divisor % d == 0 && beat / d >= minSpacing) shown = d;
            bool measuresOnly = shown == 0;
            if (measuresOnly)
            {
                if (beat * meter < minSpacing) continue;
                shown = 1;
            }

            // Thinned out or not, every tick shown is one of the full grid's and has exactly its time.
            var grid = new Grid(p, s == 0, divisor);
            long perMeasure = (long)divisor * meter;
            long stride = measuresOnly ? perMeasure : divisor / shown;
            double step = beat / divisor * stride;
            if (!(step > 0)) continue;

            // Counted from the section's offset. Begins a millisecond early because ticks lose their fraction;
            // whatever that brings in from before the range is skipped below.
            long index = (long)Math.Floor((start - 1 - p.Offset) / step);
            if (s > 0) index = Math.Max(index, 0);

            for (long local = index * stride; visited < MaxTicks; local += stride, visited++)
            {
                long time = grid.At(grid.First + local);
                if (time > end) break;
                if (time < start) continue;
                yield return new SnapTick(time, DivisorOf(local / stride, shown), Mod(local, perMeasure) == 0);
            }
        }
    }

    private static long Mod(long value, long modulus) => (value % modulus + modulus) % modulus;
}
