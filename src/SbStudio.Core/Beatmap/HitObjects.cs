using System.Numerics;

namespace SbStudio.Core;

/// <summary>
/// A decoded native hit object. Modes interpret circles, sliders, spinners and mania holds differently.
/// Positions use the .osu file's 512x384 coordinates; mania derives a column from X.
/// </summary>
public abstract class HitObject
{
    public int HitSound;
    public HitSample Sample = new();
    public double StartTime;
    public Vector2 Position;
    /// <summary>The object begins a new combo: its number is 1 again and the colour moves on.</summary>
    public bool NewCombo;
    /// <summary>How many combo colours a new combo skips.</summary>
    public int ComboOffset;
    /// <summary>How far up a stack of objects in one place this one lies; each step shifts it up and left a little.</summary>
    public int StackHeight;
    /// <summary>The number of the combo the object is in, counting the colours that were skipped. It picks the colour.</summary>
    public int ComboIndexWithOffsets;
    /// <summary>The combo index without beatmap colour skips, used when beatmap skins are disabled.</summary>
    public int ComboIndex;
    /// <summary>The place in its combo, from 0. The number on the object is one more.</summary>
    public int IndexInCurrentCombo;

    public virtual double EndTime => StartTime;
    /// <summary>Where the object ends: for a slider the far end of its line.</summary>
    public virtual Vector2 EndPosition => Position;
}

public sealed class HitCircle : HitObject
{
}

/// <summary>A mania long note, whose head and tail stay in the same column.</summary>
public sealed class HoldNote : HitObject
{
    public double Duration;
    public override double EndTime => StartTime + Duration;
}

/// <summary>A point of a slider that scores when the ball passes it.</summary>
/// <param name="SpanIndex">Which pass along the line it belongs to, from 0.</param>
/// <param name="PathProgress">Where on the line it is, from 0 at the head to 1 at the far end.</param>
public readonly record struct SliderTick(double Time, int SpanIndex, double SpanStartTime, double PathProgress);

public sealed class Slider : HitObject
{
    public List<int> EdgeSounds = new();
    public List<HitSample> EdgeSamples = new();
    internal List<HitSample> TriggerEdgeSamples = new();
    public SliderPath Path = new(Array.Empty<PathPoint>(), null);
    /// <summary>How often the ball turns round; it passes along the line one more time than that.</summary>
    public int RepeatCount;
    /// <summary>osu! pixels per millisecond.</summary>
    public double Velocity = 1;
    public double TickDistance = double.PositiveInfinity;
    public List<SliderTick> Ticks = new();

    public int SpanCount => RepeatCount + 1;
    public double Duration => SpanCount * Path.Distance / Velocity;
    public double SpanDuration => Duration / SpanCount;
    public override double EndTime => StartTime + Duration;
    public override Vector2 EndPosition => Position + CurvePositionAt(1);

    /// <summary>Which pass along the line is under way at a part of the slider's whole duration, from 0 to 1.</summary>
    public int SpanAt(double progress) => (int)(progress * SpanCount);

    /// <summary>How far along the line the ball is at a part of the slider's whole duration: every other pass runs backwards.</summary>
    public double ProgressAt(double progress)
    {
        double p = progress * SpanCount % 1;
        if (SpanAt(progress) % 2 == 1) p = 1 - p;
        return p;
    }

    /// <summary>Where the ball is at a part of the slider's whole duration, from the slider's own position.</summary>
    public Vector2 CurvePositionAt(double progress) => Path.PositionAt(ProgressAt(progress));
}

public sealed class Spinner : HitObject
{
    public double Duration;
    public override double EndTime => StartTime + Duration;
}

/// <summary>
/// The hit objects of one difficulty with what is needed to show them as the game does: how early they
/// appear, how large they are, their colours. Read from a .osu file by <see cref="BeatmapReader"/>.
/// </summary>
public sealed class OsuBeatmap
{
    public int DefaultSampleSet = 1;
    public List<SampleControlPoint> SamplePoints = new();

    public SampleControlPoint SampleAt(double time)
    {
        int low = 0, high = SamplePoints.Count;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (SamplePoints[mid].Time <= time) low = mid + 1;
            else high = mid;
        }
        return low > 0 ? SamplePoints[low - 1] : new(double.NegativeInfinity, DefaultSampleSet, 100);
    }
    /// <summary>The radius of a hit object at a scale of 1, in osu! pixels.</summary>
    public const float ObjectRadius = 64;

    /// <summary>The colours combos go through when the beatmap brings none of its own: those of osu!'s classic skin.</summary>
    public static readonly (byte R, byte G, byte B)[] DefaultComboColours =
    {
        (255, 192, 0), (0, 202, 0), (18, 124, 255), (242, 24, 57),
    };

    public int FormatVersion = 14;
    /// <summary>0: osu!, 1: taiko, 2: catch, 3: mania.</summary>
    public int Mode;
    public int Columns
    {
        get
        {
            int keys = Math.Clamp((int)Math.Round(CircleSize), 1, 20);
            return keys > 10 ? keys / 2 * 2 : keys;
        }
    }
    public int ColumnOf(HitObject obj) => Math.Clamp((int)(obj.Position.X * Columns / 512), 0, Columns - 1);
    public List<ScrollControlPoint> ScrollPoints = new();
    public double CommonBeatLength = 1000;
    public List<BeatmapBarLine> BarLines = new();

    public ScrollControlPoint ScrollAt(double time)
    {
        int low = 0, high = ScrollPoints.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (ScrollPoints[middle].Time <= time) low = middle + 1;
            else high = middle;
        }
        return low > 0 ? ScrollPoints[low - 1] : Mode is 1 or 3 && ScrollPoints.Count > 0 ? ScrollPoints[0] : new(double.NegativeInfinity, 1000, 1);
    }

    /// <summary>Integrated scrolling distance; the result is independent of playback and seeking history.</summary>
    public double ScrollDistance(double from, double to)
    {
        if (to < from) return -ScrollDistance(to, from);
        var point = ScrollAt(from);
        double result = 0, previous = from;
        int low = 0, high = ScrollPoints.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (ScrollPoints[middle].Time <= from) low = middle + 1;
            else high = middle;
        }
        for (int i = low; i < ScrollPoints.Count; i++)
        {
            var next = ScrollPoints[i];
            if (next.Time >= to) break;
            result += (next.Time - previous) * Speed(point);
            point = next;
            previous = next.Time;
        }
        return result + (to - previous) * Speed(point);

        double Speed(ScrollControlPoint p) => Mode == 1
            ? 100 * SliderMultiplier * 1.4 * p.Velocity / p.BeatLength
            : Mode == 3 ? p.Velocity * CommonBeatLength / p.BeatLength : p.Velocity;
    }
    public float StackLeniency = 0.7f;
    public float CircleSize = 5, ApproachRate = 5, OverallDifficulty = 5;
    public double SliderMultiplier = 1.4, SliderTickRate = 1;
    /// <summary>The colours of the [Colours] section, in the order Combo1, Combo2… are written in.</summary>
    public List<(byte R, byte G, byte B)> ComboColours = new();
    /// <summary>The colour of the border of sliders, when the [Colours] section sets one; white otherwise.</summary>
    public (byte R, byte G, byte B)? SliderBorder;
    /// <summary>A colour for the track of every slider in place of the colour of its combo, when the [Colours] section sets one.</summary>
    public (byte R, byte G, byte B)? SliderTrackOverride;
    public List<HitObject> Objects = new();
    /// <summary>Lines of the file that could not be read as hit objects.</summary>
    public int SkippedLines;

    /// <summary>How long before its time an object appears, in milliseconds.</summary>
    public double TimePreempt = 1200;
    /// <summary>How long an object takes to fade in.</summary>
    public double TimeFadeIn = 400;
    /// <summary>The size of the objects: 1 would be a radius of <see cref="ObjectRadius"/>.</summary>
    public float Scale = 0.5f;

    /// <summary>The radius of a circle in osu! pixels, which are also storyboard units.</summary>
    public float Radius => ObjectRadius * Scale;

    /// <summary>How far an object is shifted for lying on a stack.</summary>
    public Vector2 StackOffset(HitObject obj) => obj is Spinner ? Vector2.Zero : new Vector2(obj.StackHeight * Scale * -6.4f);

    /// <summary>
    /// The colour of an object's combo. As in the game, the first combo of a map has the second colour of
    /// the list: the count starts at 1.
    /// </summary>
    public (byte R, byte G, byte B) ComboColour(HitObject obj)
    {
        IReadOnlyList<(byte R, byte G, byte B)> colours = ComboColours.Count > 0 ? ComboColours : DefaultComboColours;
        return colours[Math.Abs(obj.ComboIndexWithOffsets) % colours.Count];
    }

    /// <summary>
    /// The playfield in a storyboard: 512x384 in the middle of the 640x480 picture, 8 units lower than the
    /// middle, as osu! has it.
    /// </summary>
    public static readonly Vector2 PlayfieldOrigin = new(64, 56);

    public static Vector2 ToStoryboard(Vector2 osuPixels) => osuPixels + PlayfieldOrigin;

    /// <summary>A value that goes from one number at difficulty 0 through another at 5 to a third at 10.</summary>
    public static double DifficultyRange(double difficulty, double min, double mid, double max)
    {
        if (difficulty > 5) return mid + (max - mid) * (difficulty - 5) / 5;
        if (difficulty < 5) return mid + (mid - min) * (difficulty - 5) / 5;
        return mid;
    }

    /// <summary>The index of the first object that has not appeared yet at a time: all before it began to show earlier.</summary>
    public int FirstAfter(double time)
    {
        int low = 0, high = Objects.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (Objects[middle].StartTime - TimePreempt <= time) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}

/// <summary>Zero banks and volume inherit the active sample control point.</summary>
public sealed record HitSample(int NormalSet = 0, int AdditionSet = 0, int Volume = 0, string Filename = "", int CustomIndex = 0);
public readonly record struct SampleControlPoint(double Time, int SampleSet, int Volume, int CustomIndex = 1);
public readonly record struct ScrollControlPoint(double Time, double BeatLength, double Velocity, bool TimingChange = false);
public readonly record struct BeatmapBarLine(double Time, bool Major);
