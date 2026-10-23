using System.Globalization;
using System.Numerics;

namespace SbStudio.Core;

/// <summary>
/// Reads the hit objects of a .osu file and works out what the game works out before it shows them:
/// slider lengths and speeds, combo numbers and colours, stacks. It follows osu!lazer's decoder
/// (LegacyBeatmapDecoder, ConvertHitObjectParser) and its osu! ruleset (Slider, OsuBeatmapProcessor) step
/// by step, quirks included, since those quirks are what makes a map look the way players know it.
/// </summary>
public static class BeatmapReader
{
    /// <summary>The first format version written by osu!lazer, which keeps positions as fractions.</summary>
    private const int FirstLazerVersion = 128;
    private const int StackDistance = 3;
    private const double MaxCoordinate = 131072;

    private readonly record struct TimingLine(double Time, double BeatLength, int Meter, bool OmitFirst);
    private readonly record struct DifficultyLine(double Time, double SliderVelocity, bool GenerateTicks);
    private readonly record struct Break(double Start, double End);

    public static OsuBeatmap Read(string path) => Parse(File.ReadAllText(path));

    public static OsuBeatmap Parse(string text)
    {
        var map = new OsuBeatmap();
        var timing = new List<TimingLine>();
        var difficulty = new List<DifficultyLine>();
        var breaks = new List<Break>();
        var objectLines = new List<string>();
        // Lines for one time that have not been settled yet: see FlushPending.
        var pending = new List<(double Time, double BeatLength, bool Uninherited, int Meter, bool OmitFirst)>();
        bool hasApproachRate = false;
        string section = "";

        void FlushPending()
        {
            if (pending.Count == 0) return;
            // Of several lines at one time the last inherited one says how fast sliders are; without one,
            // the first uninherited line does. The first uninherited line also is the timing of that time.
            int uninherited = pending.FindIndex(p => p.Uninherited), inherited = pending.FindLastIndex(p => !p.Uninherited);
            if (uninherited >= 0) timing.Add(new TimingLine(pending[uninherited].Time, Math.Clamp(pending[uninherited].BeatLength, 6, 60000), pending[uninherited].Meter, pending[uninherited].OmitFirst));
            var speed = pending[inherited >= 0 ? inherited : uninherited];
            double multiplier = speed.BeatLength < 0 ? 100.0 / -speed.BeatLength : 1;
            difficulty.Add(new DifficultyLine(speed.Time, Math.Clamp(multiplier, 0.1, 10), !double.IsNaN(speed.BeatLength)));
            pending.Clear();
        }

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (section.Length == 0 && line.StartsWith("osu file format v", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(line.AsSpan("osu file format v".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int version)) map.FormatVersion = version;
                continue;
            }
            if (line[0] == '[' && line[^1] == ']')
            {
                section = line.Substring(1, line.Length - 2);
                continue;
            }

            switch (section)
            {
                case "General":
                case "Difficulty":
                case "Colours":
                {
                    int colon = line.IndexOf(':');
                    if (colon <= 0) break;
                    string key = line.Substring(0, colon).Trim(), value = line.Substring(colon + 1).Trim();
                    switch (key)
                    {
                        case "Mode": if (TryInt(value, out int mode)) map.Mode = mode; break;
                        case "SampleSet": map.DefaultSampleSet = value.ToLowerInvariant() switch { "soft" => 2, "drum" => 3, _ => 1 }; break;
                        case "StackLeniency": if (TryDouble(value, out double leniency)) map.StackLeniency = (float)leniency; break;
                        case "CircleSize": if (TryDouble(value, out double cs)) map.CircleSize = (float)cs; break;
                        case "OverallDifficulty":
                            if (!TryDouble(value, out double od)) break;
                            map.OverallDifficulty = (float)od;
                            // Old maps have no approach rate of their own.
                            if (!hasApproachRate) map.ApproachRate = map.OverallDifficulty;
                            break;
                        case "ApproachRate":
                            if (!TryDouble(value, out double ar)) break;
                            map.ApproachRate = (float)ar;
                            hasApproachRate = true;
                            break;
                        case "SliderMultiplier": if (TryDouble(value, out double sm)) map.SliderMultiplier = sm; break;
                        case "SliderTickRate": if (TryDouble(value, out double tr)) map.SliderTickRate = tr; break;
                        case "SliderBorder": if (section == "Colours" && TryColour(value, out var border)) map.SliderBorder = border; break;
                        case "SliderTrackOverride": if (section == "Colours" && TryColour(value, out var track)) map.SliderTrackOverride = track; break;
                        default:
                            if (section == "Colours" && key.StartsWith("Combo", StringComparison.Ordinal) &&
                                int.TryParse(key.AsSpan(5), out int number) && number is >= 1 and <= 8 && TryColour(value, out var colour))
                                map.ComboColours.Add(colour);
                            break;
                    }
                    break;
                }

                case "Events":
                {
                    var parts = line.Split(',');
                    if (parts.Length >= 3 && (parts[0].Trim() == "2" || parts[0].Trim().Equals("Break", StringComparison.OrdinalIgnoreCase)) &&
                        TryDouble(parts[1], out double start) && TryDouble(parts[2], out double end))
                        breaks.Add(new Break(start, end));
                    break;
                }

                case "TimingPoints":
                {
                    var parts = line.Split(',');
                    if (parts.Length < 2 || !TryDouble(parts[0], out double time)) break;
                    // A beat length that is not a number is how some maps switch slider ticks off.
                    if (!double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double beatLength)) break;
                    bool uninherited = parts.Length < 7 || parts[6].Trim().StartsWith('1');
                    if (uninherited && double.IsNaN(beatLength)) break;
                    if (pending.Count > 0 && pending[0].Time != time) FlushPending();
                    int meter = parts.Length > 2 && TryInt(parts[2], out int numerator) ? Math.Clamp(numerator, 1, 64) : 4;
                    bool omitFirst = parts.Length > 7 && TryInt(parts[7], out int effectFlags) && (effectFlags & 8) != 0;
                    pending.Add((time, beatLength, uninherited, meter, omitFirst));
                    int bank = parts.Length > 3 && TryInt(parts[3], out int sampleSet) ? Math.Clamp(sampleSet, 0, 3) : 0;
                    int volume = parts.Length > 5 && TryInt(parts[5], out int sampleVolume) ? Math.Clamp(sampleVolume, 0, 100) : 100;
                    int customIndex = parts.Length > 4 && TryInt(parts[4], out int customBank) ? Math.Max(1, customBank) : 1;
                    map.SamplePoints.Add(new(time, bank, volume, customIndex));
                    break;
                }

                case "HitObjects":
                    objectLines.Add(line);
                    break;
            }
        }
        FlushPending();
        timing.Sort((a, b) => a.Time.CompareTo(b.Time));
        difficulty = difficulty.OrderBy(d => d.Time).ToList();
        map.SamplePoints = map.SamplePoints.OrderBy(p => p.Time).ToList();
        for (int i = 0; i < map.SamplePoints.Count; i++)
            if (map.SamplePoints[i].SampleSet == 0) map.SamplePoints[i] = map.SamplePoints[i] with { SampleSet = map.DefaultSampleSet };

        map.SliderMultiplier = Math.Clamp(map.SliderMultiplier, 0.4, 3.6);
        map.SliderTickRate = Math.Clamp(map.SliderTickRate, 0.5, 8);
        map.TimePreempt = (int)OsuBeatmap.DifficultyRange(map.ApproachRate, 1800, 1200, 450);
        map.TimeFadeIn = 400 * Math.Min(1, map.TimePreempt / 450);
        // The factor at the end makes up for a rounding that old versions of osu! did to the playfield.
        map.Scale = (float)(1.0f - 0.7f * (((double)map.CircleSize - 5) / 5)) / 2 * 1.00041f;

        if (map.Mode is < 0 or > 3) return map;
        var timingOffsets = timing.Select(point => point.Time).ToHashSet();
        foreach (var point in difficulty)
        {
            var beat = timing.LastOrDefault(p => p.Time <= point.Time);
            map.ScrollPoints.Add(new(point.Time, beat.BeatLength > 0 ? beat.BeatLength : timing.Count > 0 ? timing[0].BeatLength : 1000, point.SliderVelocity,
                timingOffsets.Contains(point.Time)));
        }
        if (map.Mode == 3) map.TimePreempt = 11485.0 / 8; // lazer's default mania scroll speed.

        HitObject? last = null;
        foreach (string line in objectLines)
        {
            try
            {
                var obj = ParseObject(line, map, last);
                if (obj == null)
                {
                    map.SkippedLines++;
                    continue;
                }
                map.Objects.Add(obj);
                last = obj;
            }
            catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or OverflowException)
            {
                map.SkippedLines++;
            }
        }
        // Stable: objects at the same time keep the order of the file.
        map.Objects = map.Objects.OrderBy(o => o.StartTime).ToList();

        // The first object after a break begins a new combo.
        breaks.Sort((a, b) => a.Start.CompareTo(b.Start));
        int currentBreak = 0;
        foreach (var obj in map.Objects)
        {
            bool force = false;
            while (currentBreak < breaks.Count && breaks[currentBreak].End < obj.StartTime)
            {
                force = true;
                currentBreak++;
            }
            obj.NewCombo |= force;
        }

        foreach (var obj in map.Objects)
            if (obj is Slider slider) ApplySliderDefaults(slider, map, timing, difficulty);

        // DrawableManiaRuleset uses RelativeScaleBeatLengths: the most common timing beat length,
        // weighted by playable duration, cancels global SliderMultiplier before applying local BPM/SV.
        double lastTime = map.Objects.Count > 0 ? map.Objects.Max(o => o.EndTime) : timing.LastOrDefault().Time;
        if (timing.Count > 0)
        {
            var common = timing.Select((p, i) => (Beat: Math.Round(p.BeatLength * 1000) / 1000,
                    Duration: p.Time > lastTime ? 0 : (i + 1 < timing.Count ? timing[i + 1].Time : lastTime) - (i == 0 ? 0 : p.Time)))
                .GroupBy(p => p.Beat).Select(g => (Beat: g.Key, Duration: g.Sum(p => p.Duration)))
                .OrderByDescending(p => p.Duration).First();
            map.CommonBeatLength = Math.Clamp(common.Beat, timing.Min(p => p.BeatLength), timing.Max(p => p.BeatLength));
        }
        if (map.Mode is 1 or 3 && map.Objects.Count > 0)
            for (int i = 0; i < timing.Count; i++)
            {
                var point = timing[i];
                double length = point.BeatLength * point.Meter;
                double minimum = Math.Min(0, map.Objects[0].StartTime);
                double start = point.Time > minimum ? point.Time : point.Time + Math.Ceiling((minimum - point.Time) / length) * length;
                if (point.OmitFirst) start += length;
                double end = i + 1 < timing.Count ? timing[i + 1].Time : lastTime + 1 + length;
                int bar = 0;
                for (double t = start; t < end && map.BarLines.Count < 200000; t += length, bar++)
                    map.BarLines.Add(new(t, bar % point.Meter == 0));
            }

        ApplyCombos(map.Objects);
        if (map.Mode == 0 && map.Objects.Count > 0)
        {
            if (map.FormatVersion >= 6) ApplyStacking(map);
            else ApplyStackingOld(map);
        }
        return map;
    }

    // ---------------------------------------------------------------- numbers

    private static bool TryDouble(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private static bool TryInt(string text, out int value) => int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static bool TryColour(string text, out (byte R, byte G, byte B) colour)
    {
        colour = default;
        var parts = text.Split(',');
        if (parts.Length < 3 || !byte.TryParse(parts[0].Trim(), out byte r) || !byte.TryParse(parts[1].Trim(), out byte g) || !byte.TryParse(parts[2].Trim(), out byte b)) return false;
        colour = (r, g, b);
        return true;
    }

    private static double Number(string text, double limit = int.MaxValue)
    {
        double value = double.Parse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
        if (double.IsNaN(value) || Math.Abs(value) > limit) throw new FormatException();
        return value;
    }

    private static int Whole(string text) => int.Parse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- objects

    /// <summary>One line of [HitObjects]: x,y,time,type,hitSound,… Null for a kind of object osu! standard does not have.</summary>
    private static HitObject? ParseObject(string line, OsuBeatmap map, HitObject? last)
    {
        var split = line.Split(',');
        bool lazer = map.FormatVersion >= FirstLazerVersion;
        float x = Math.Clamp((float)Number(split[0], MaxCoordinate), 0, 512), y = Math.Clamp((float)Number(split[1], MaxCoordinate), 0, 512);
        // Before lazer positions were whole pixels.
        var position = lazer ? new Vector2(x, y) : new Vector2((int)x, (int)y);
        double startTime = Number(split[2]);
        int type = Whole(split[3]);
        int comboOffset = (type & 0b0111_0000) >> 4;
        bool newCombo = (type & 0b0100) != 0;
        // The first object of a map and the first after a spinner begin a combo whatever they say.
        bool forced = last == null || last is Spinner;

        HitObject result;
        if (map.Mode == 3 && (type & 128) != 0)
        {
            result = new HoldNote { Duration = Math.Max(0, Number(split[5].Split(':', 2)[0]) - startTime) };
        }
        else if ((type & 0b0001) != 0)
        {
            result = new HitCircle { NewCombo = forced || newCombo, ComboOffset = newCombo ? comboOffset : 0 };
        }
        else if ((type & 0b0010) != 0)
        {
            int repeats = Whole(split[6]);
            if (repeats > 9000) throw new FormatException();
            // osu!stable counts the first pass along the line as a repeat.
            repeats = Math.Max(0, repeats - 1);
            double? length = null;
            if (split.Length > 7)
            {
                length = Math.Max(0, Number(split[7], MaxCoordinate));
                if (length == 0) length = null;
            }
            var path = new SliderPath(ConvertPath(split[5], position, lazer), length);
            // A slider without length cannot be gone along more than once.
            if (Math.Abs(path.Distance) <= 1e-7) repeats = 0;
            result = new Slider { Path = path, RepeatCount = repeats, NewCombo = forced || newCombo, ComboOffset = newCombo ? comboOffset : 0 };
        }
        else if ((type & 0b1000) != 0)
        {
            // A spinner is always in the middle and takes no part in the colours of the combos.
            position = new Vector2(256, 192);
            result = new Spinner { Duration = Math.Max(0, Number(split[5]) - startTime), NewCombo = newCombo };
        }
        else
        {
            return null;
        }
        result.Position = position;
        result.StartTime = startTime;
        result.HitSound = Whole(split[4]);
        int sampleIndex = result is Slider ? 10 : result is Spinner ? 6 : 5;
        if (split.Length > sampleIndex)
            result.Sample = ParseSample(result is HoldNote ? split[sampleIndex].Split(':', 2).ElementAtOrDefault(1) ?? "" : split[sampleIndex]);
        if (result is Slider slider)
        {
            var sounds = split.Length > 8 ? split[8].Split('|') : Array.Empty<string>();
            var banks = split.Length > 9 ? split[9].Split('|') : Array.Empty<string>();
            for (int edge = 0; edge <= slider.SpanCount; edge++)
            {
                slider.EdgeSounds.Add(edge < sounds.Length && TryInt(sounds[edge], out int sound) ? sound : result.HitSound);
                var sample = edge < banks.Length && banks[edge].Length > 0 ? ParseSample(banks[edge]) : result.Sample;
                slider.EdgeSamples.Add(sample with { Volume = result.Sample.Volume });
                // Legacy sliders read only banks from their final sample field. A node may override
                // its own index/file, but otherwise inherits only those banks, then control points.
                slider.TriggerEdgeSamples.Add(edge < banks.Length && banks[edge].Length > 0 ? ParseSample(banks[edge])
                    : result.Sample with { Volume = 0, CustomIndex = 0, Filename = "" });
            }
        }
        return result;
    }

    private static HitSample ParseSample(string text)
    {
        var parts = text.Split(':', 5);
        int Read(int index, int max) => index < parts.Length && TryInt(parts[index], out int value) ? Math.Clamp(value, 0, max) : 0;
        return new(Read(0, 3), Read(1, 3), Read(3, 100), parts.Length > 4 ? parts[4].Trim() : "", Read(2, int.MaxValue));
    }

    /// <summary>
    /// The control points of a slider from the text that gives them: "B|x:y|x:y|…", where a letter begins a
    /// piece of the curve and two points in the same place begin another of the same kind.
    /// </summary>
    private static List<PathPoint> ConvertPath(string text, Vector2 offset, bool lazer)
    {
        var points = new List<Vector2>();
        var segments = new List<(SplineKind Kind, int? Degree, int Start)>();
        foreach (string part in text.Split('|'))
        {
            if (part.Length == 0) continue;
            if (char.IsLetter(part[0]))
            {
                var (kind, degree) = part[0] switch
                {
                    'B' => (SplineKind.Bezier, part.Length > 1 && int.TryParse(part.AsSpan(1), out int d) && d > 0 ? d : (int?)null),
                    'L' => (SplineKind.Linear, (int?)null),
                    'P' => (SplineKind.PerfectCurve, (int?)null),
                    _ => (SplineKind.Catmull, (int?)null),
                };
                segments.Add((kind, degree, points.Count));
                // The slider's own position is the first point of its first piece.
                if (points.Count == 0) points.Add(Vector2.Zero);
            }
            else
            {
                var xy = part.Split(':');
                float px = (float)Number(xy[0], MaxCoordinate), py = (float)Number(xy[1], MaxCoordinate);
                points.Add((lazer ? new Vector2(px, py) : new Vector2((int)px, (int)py)) - offset);
            }
        }

        var result = new List<PathPoint>(points.Count);
        for (int i = 0; i < segments.Count; i++)
        {
            int start = segments[i].Start, end = i + 1 < segments.Count ? segments[i + 1].Start : points.Count;
            Vector2? endPoint = i + 1 < segments.Count ? points[end] : null;
            ConvertPoints(result, segments[i].Kind, segments[i].Degree, points.GetRange(start, end - start), endPoint, lazer);
        }
        return result;
    }

    /// <summary>The points of one piece as they are written, split up where a point is given twice in a row.</summary>
    /// <param name="endPoint">The point the next piece begins with, which also ends this one.</param>
    private static void ConvertPoints(List<PathPoint> result, SplineKind kind, int? degree, List<Vector2> points, Vector2? endPoint, bool lazer)
    {
        if (points.Count == 0) return;
        if (kind == SplineKind.PerfectCurve)
        {
            int count = points.Count + (endPoint == null ? 0 : 1);
            if (!lazer)
            {
                // osu!stable: an arc needs exactly three points, and three in a line are a straight line.
                if (count != 3) kind = SplineKind.Bezier;
                else if (IsLinear(points[0], points[1], endPoint ?? points[2])) kind = SplineKind.Linear;
            }
            else if (count > 3)
            {
                kind = SplineKind.Bezier;
            }
        }

        var vertices = new PathPoint[points.Count];
        for (int i = 0; i < vertices.Length; i++) vertices[i] = new PathPoint(points[i]);
        vertices[0] = new PathPoint(points[0], kind, degree);

        int startIndex = 0, endIndex = 0;
        while (++endIndex < vertices.Length)
        {
            if (vertices[endIndex].Position != vertices[endIndex - 1].Position) continue;
            // A Catmull slider of old is one piece however its points lie, except right at its head.
            if (kind == SplineKind.Catmull && endIndex > 1 && !lazer) continue;
            // The last point of a piece begins no other.
            if (endIndex == vertices.Length - 1) continue;

            vertices[endIndex - 1] = vertices[endIndex - 1] with { Kind = kind, Degree = degree };
            for (int i = startIndex; i < endIndex; i++) result.Add(vertices[i]);
            // The second of the two points is the one that has just been taken.
            startIndex = endIndex + 1;
        }
        for (int i = startIndex; i < endIndex; i++) result.Add(vertices[i]);
    }

    private static bool IsLinear(Vector2 p0, Vector2 p1, Vector2 p2) =>
        PathApproximator.AlmostEquals(0, (p1.Y - p0.Y) * (p2.X - p0.X) - (p1.X - p0.X) * (p2.Y - p0.Y));

    // ---------------------------------------------------------------- sliders

    private static void ApplySliderDefaults(Slider slider, OsuBeatmap map, List<TimingLine> timing, List<DifficultyLine> difficulty)
    {
        // The timing in force when the slider begins; before the first timing point, that first one.
        double beatLength = 1000;
        if (timing.Count > 0)
        {
            int index = timing.FindLastIndex(t => t.Time <= slider.StartTime);
            beatLength = timing[Math.Max(0, index)].BeatLength;
        }
        int speedIndex = difficulty.FindLastIndex(d => d.Time <= slider.StartTime);
        double speed = speedIndex >= 0 ? difficulty[speedIndex].SliderVelocity : 1;
        bool generateTicks = speedIndex < 0 || difficulty[speedIndex].GenerateTicks;

        // As osu!stable has it: the speed is a beat length of its own, kept within limits as a float.
        double bpmMultiplier = Math.Clamp((float)(100 / speed), 10, 1000) / 100.0;
        slider.Velocity = 100 * map.SliderMultiplier / (beatLength * bpmMultiplier);
        double scoringDistance = slider.Velocity * beatLength;
        slider.TickDistance = generateTicks ? scoringDistance / map.SliderTickRate : double.PositiveInfinity;

        // After osu!lazer's SliderEventGenerator.
        double length = Math.Min(100000, slider.Path.Distance);
        double tickDistance = Math.Clamp(slider.TickDistance, 0, length);
        double minDistanceFromEnd = slider.Velocity * 10;
        double spanDuration = slider.SpanDuration;
        slider.Ticks.Clear();
        if (tickDistance == 0 || !double.IsFinite(spanDuration)) return;
        for (int span = 0; span < slider.SpanCount; span++)
        {
            double spanStart = slider.StartTime + span * spanDuration;
            bool reversed = span % 2 == 1;
            int first = slider.Ticks.Count;
            for (double d = tickDistance; d <= length; d += tickDistance)
            {
                if (d >= length - minDistanceFromEnd) break;
                // Counted from the head of the slider on every pass, so that the ticks are in the same places each time.
                double pathProgress = d / length;
                double timeProgress = reversed ? 1 - pathProgress : pathProgress;
                slider.Ticks.Add(new SliderTick(spanStart + timeProgress * spanDuration, span, spanStart, pathProgress));
            }
            if (reversed) slider.Ticks.Reverse(first, slider.Ticks.Count - first);
        }
    }

    // ---------------------------------------------------------------- combos

    private static void ApplyCombos(List<HitObject> objects)
    {
        HitObject? last = null;
        foreach (var obj in objects)
        {
            int withOffsets = last?.ComboIndexWithOffsets ?? 0;
            int combo = last?.ComboIndex ?? 0;
            int inCombo = last == null ? 0 : last.IndexInCurrentCombo + 1;
            // A spinner never begins a combo for the colours, whatever it says.
            if (obj is not Spinner && (obj.NewCombo || last == null || last is Spinner))
            {
                inCombo = 0;
                withOffsets += obj.ComboOffset + 1;
                combo++;
            }
            obj.ComboIndexWithOffsets = withOffsets;
            obj.ComboIndex = combo;
            obj.IndexInCurrentCombo = inCombo;
            last = obj;
        }
    }

    // ---------------------------------------------------------------- stacks

    private static float StackThreshold(OsuBeatmap map) => (int)map.TimePreempt * map.StackLeniency;

    /// <summary>Objects in one place shortly after each other are shifted so that each can be seen; after OsuBeatmapProcessor.</summary>
    private static void ApplyStacking(OsuBeatmap map)
    {
        var objects = map.Objects;
        float threshold = StackThreshold(map);
        foreach (var obj in objects) obj.StackHeight = 0;

        // From the end back to the start.
        for (int i = objects.Count - 1; i > 0; i--)
        {
            int n = i;
            var objectI = objects[i];
            if (objectI.StackHeight != 0 || objectI is Spinner) continue;

            if (objectI is HitCircle)
            {
                // A stack of circles, or of circles under the end of a slider.
                while (--n >= 0)
                {
                    var objectN = objects[n];
                    if (objectN is Spinner) continue;
                    // Whole milliseconds, as osu!stable compares them.
                    if ((int)objectI.StartTime - (int)objectN.EndTime > threshold) break;

                    // Circles under the end of a slider go down and right instead: the slider stays where it is.
                    if (objectN is Slider && Vector2.Distance(objectN.EndPosition, objectI.Position) < StackDistance)
                    {
                        int offset = objectI.StackHeight - objectN.StackHeight + 1;
                        for (int j = n + 1; j <= i; j++)
                        {
                            var objectJ = objects[j];
                            if (Vector2.Distance(objectN.EndPosition, objectJ.Position) < StackDistance) objectJ.StackHeight -= offset;
                        }
                        // The slider is looked at on its own when the outer loop gets to it.
                        break;
                    }

                    if (Vector2.Distance(objectN.Position, objectI.Position) < StackDistance)
                    {
                        objectN.StackHeight = objectI.StackHeight + 1;
                        objectI = objectN;
                    }
                }
            }
            else if (objectI is Slider)
            {
                // From the first slider of a stack on, everything goes up and left.
                while (--n >= 0)
                {
                    var objectN = objects[n];
                    if (objectN is Spinner) continue;
                    if (objectI.StartTime - objectN.StartTime > threshold) break;
                    if (Vector2.Distance(objectN.EndPosition, objectI.Position) < StackDistance)
                    {
                        objectN.StackHeight = objectI.StackHeight + 1;
                        objectI = objectN;
                    }
                }
            }
        }
    }

    /// <summary>The way maps older than version 6 are stacked.</summary>
    private static void ApplyStackingOld(OsuBeatmap map)
    {
        var objects = map.Objects;
        float threshold = StackThreshold(map);
        foreach (var obj in objects) obj.StackHeight = 0;

        for (int i = 0; i < objects.Count; i++)
        {
            var current = objects[i];
            if (current.StackHeight != 0 && current is not Slider) continue;

            double startTime = current.EndTime;
            int sliderStack = 0;
            for (int j = i + 1; j < objects.Count; j++)
            {
                if (objects[j].StartTime - threshold > startTime) break;
                var position2 = current.EndPosition;
                if (Vector2.Distance(objects[j].Position, current.Position) < StackDistance)
                {
                    current.StackHeight++;
                    startTime = objects[j].StartTime;
                }
                else if (Vector2.Distance(objects[j].Position, position2) < StackDistance)
                {
                    // Under the end of a slider: down and right rather than up and left.
                    sliderStack++;
                    objects[j].StackHeight -= sliderStack;
                    startTime = objects[j].StartTime;
                }
            }
        }
    }
}
