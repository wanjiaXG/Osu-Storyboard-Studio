using SbStudio.Core;
using SbStudio.Resources;

namespace SbStudio.App.Audio;

/// <summary>A metronome click: a beat, or (Divisor above 1) a snap line between beats, which clicks more quietly.</summary>
internal readonly record struct TimingBeat(double Time, double Bpm, bool Measure, int Divisor = 1)
{
    public bool Subdivision => Divisor != 1;
    public ScheduledSample Sample => new(Time, TimingEventIndex.ClickFile,
        Measure ? 1 : Subdivision ? TimingEventIndex.SubdivisionVolume : 0.75f, HitSound: true, BypassTempo: true);
}

/// <summary>
/// The same grid drives both the metronome and its timeline markers: whole beats, and with a snap divisor
/// above 1 also every snap line between them.
/// </summary>
internal sealed class TimingEventIndex
{
    public static readonly TimingEventIndex Empty = new(Array.Empty<TimingBeat>());
    internal static readonly string ClickFile = ResourceAssets.Combine(ResourceAssets.DefaultSkin, "metronomelow.wav");
    internal const float SubdivisionVolume = 0.45f;
    public TimingBeat[] Events { get; }
    /// <summary>Times of the snap lines between the beats; empty unless built with a divisor above 1.</summary>
    public IEnumerable<double> Subdivisions => Events.Where(e => e.Subdivision).Select(e => e.Time);
    public IEnumerable<ScheduledSample> Playback => Events.Select(e => e.Sample);

    /// <summary>The playback clock crosses fractional beat times, independently of the editor's integer snap grid.</summary>
    internal IEnumerable<ScheduledSample> PrecisePlayback(IReadOnlyList<TimingPoint> points, int divisor)
    {
        foreach (var beat in Events)
        {
            var point = TimingUtil.At(points, beat.Time)!;
            double step = point.BeatLength / Math.Max(1, divisor);
            double time = point.Offset + Math.Round((beat.Time - point.Offset) / step) * step;
            yield return beat.Sample with { TimeMs = time };
        }
    }

    private TimingEventIndex(TimingBeat[] events) => Events = events;

    public static TimingEventIndex Build(IReadOnlyList<TimingPoint> points, double from, double to, int snapDivisor = 1) => new(
        TimingUtil.Ticks(points, Math.Max(1, snapDivisor), from, to)
            .Select(t => new TimingBeat(t.Time, TimingUtil.At(points, t.Time)!.Bpm, t.Measure, t.Divisor)).ToArray());

    public int FirstAtOrAfter(double time)
    {
        int lo = 0, hi = Events.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (Events[mid].Time < time) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
