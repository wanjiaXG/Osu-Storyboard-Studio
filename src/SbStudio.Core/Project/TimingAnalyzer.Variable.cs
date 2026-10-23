namespace SbStudio.Core;

/// <summary>One red line found by the variable-tempo analysis.</summary>
/// <param name="OffsetMs">Where it starts, in milliseconds from the start of the analysed audio.</param>
/// <param name="Beats">How many tracked beats it covers.</param>
/// <param name="MaxErrorMs">The largest distance of one of those beats from the red line's grid.</param>
/// <param name="Correction">
/// Not a change of tempo: the same BPM again, on the next downbeat, because the red line before it had to start in the
/// middle of a bar and a red line restarts the bar. It puts the bar lines back on the music's first beats.
/// </param>
public sealed record TimingSection(double OffsetMs, double Bpm, int Beats, double MaxErrorMs, bool Correction = false);

/// <summary>Settings of the variable-tempo analysis.</summary>
/// <param name="ToleranceMs">How far 8 beats in a row may stray on average before a new red line starts.</param>
/// <param name="MinBeats">The fewest beats one red line covers.</param>
/// <param name="TempoHint">Above 0: look for the beat near this BPM.</param>
/// <param name="OriginMs">Where the analysed audio starts on the song's time line (red lines sit on its whole milliseconds).</param>
/// <param name="Divisor">Every red line starts on a 1/Divisor tick of the red line before it.</param>
/// <param name="Previous">The red line in force where the analysed audio starts, if any: the first red line found starts on its grid.</param>
/// <param name="PreviousIsFirst">Whether <paramref name="Previous"/> is the first red line of the map (its grid differs slightly).</param>
/// <param name="Meter">Beats per bar: which beats are downbeats, and what the red lines are set to.</param>
/// <param name="CalibrationMs">Added to every beat before the red lines are placed: the user's own calibration.</param>
/// <param name="BpmDecimals">Decimals a red line's BPM may have (0 = whole BPMs only, up to 3 as the editor allows).</param>
public sealed record VariableTimingOptions(double ToleranceMs = 5, int MinBeats = 4, double TempoHint = 0, double OriginMs = 0, int Divisor = 4,
    TimingPoint? Previous = null, bool PreviousIsFirst = false, int Meter = 4, double CalibrationMs = 0, int BpmDecimals = 3);

public sealed class VariableTimingAnalysis
{
    public TimingConfidence Confidence { get; internal set; } = TimingConfidence.Failed;
    /// <summary>Every tracked beat, in milliseconds from the start of the analysed audio.</summary>
    public double[] Beats { get; internal set; } = Array.Empty<double>();
    public TimingSection[] Sections { get; internal set; } = Array.Empty<TimingSection>();
    /// <summary>The tempo the tracking was centred on (a global estimate, or the hint).</summary>
    public double CentreBpm { get; internal set; }
    /// <summary>Lowest and highest local tempo seen.</summary>
    public double MinBpm { get; internal set; }
    public double MaxBpm { get; internal set; }
    /// <summary>For every tracked beat, whether it is the first beat of a bar (from the accents, see <see cref="TimingAnalyzer.BarPositions"/>).</summary>
    public bool[] Downbeats { get; internal set; } = Array.Empty<bool>();
    /// <summary>Share of tracked beats that land on a clear attack (0..1).</summary>
    public double BeatsOnAttacks { get; internal set; }
}

public static partial class TimingAnalyzer
{
    /// <summary>
    /// Per-beat attack peaks sit this much after the folded centroid that <see cref="AttackCalibrationMs"/> refers to;
    /// measured on ranked maps of constant BPM, so tracked beats line up with the timing mappers set.
    /// </summary>
    public const double BeatCalibrationMs = 15.3;

    private const double TrackHopMs = 2;
    // Tuned on ranked maps with several red lines: local periods within 1/1.3 to 1.3 of the overall one, smoothed
    // over 13 windows (6.5 s), and a tight pull towards the local period.
    private const int Spread = 6;
    private const double LagRange = 1.3, Tightness = 800;

    /// <summary>
    /// Experimental: follows a tempo that changes (a piece played freely, a gradual speed-up) beat by beat, then splits
    /// the beats into as few red lines as keep the beats within the tolerance of their red line's grid.
    /// <list type="number">
    /// <item>The same band-energy attack curve as the constant-tempo analysis, sampled every 2 ms.</item>
    /// <item>A local beat period every half second from the curve's autocorrelation over 6 seconds, kept between 1/1.3
    /// and 1.3 times the overall period so it does not jump between half and double tempo, then median-smoothed.</item>
    /// <item>Beat tracking by dynamic programming: the best chain of beats trades attack strength against how far each
    /// interval strays from the local period (Ellis, "Beat Tracking by Dynamic Programming", J. New Music Research
    /// 36(1), 2007), here with the local rather than a single global period.</item>
    /// <item>Each beat refined between samples, then split into red lines that each start on a snap tick of the one
    /// before (see <see cref="Split"/>).</item>
    /// </list>
    /// </summary>
    public static VariableTimingAnalysis AnalyzeVariable(ReadOnlySpan<float> mono, double sampleRate, VariableTimingOptions? options = null,
        CancellationToken cancel = default)
    {
        options ??= new VariableTimingOptions();
        double tempoHint = options.TempoHint;
        var result = new VariableTimingAnalysis();
        if (mono.Length < sampleRate * 4 || sampleRate < 8000) return result;
        float[] audio = Normalized(mono, out bool silent);
        if (silent) return result;
        double length = audio.Length * 1000.0 / sampleRate;

        var envelope = OnsetEnvelope(audio, sampleRate, cancel);
        double rough = RoughPeriod(envelope, tempoHint);
        if (!(rough > 0)) return result;
        double period = FinePeriod(envelope, rough);
        result.CentreBpm = 60000.0 / period;
        cancel.ThrowIfCancellationRequested();

        var bands = new BandEnergy(audio, sampleRate, BandWindowMs);
        int n = (int)(length / TrackHopMs);
        var curve = new double[n];
        for (int i = 0; i < n; i++)
        {
            if ((i & 16383) == 0) cancel.ThrowIfCancellationRequested();
            double t = i * TrackHopMs;
            curve[i] = Math.Max(0, bands.Level(t + RiseMs) - bands.Level(t - RiseMs));
        }
        // In units of its own spread, so the trade-off below does not depend on loudness.
        double mean = curve.Average(), sd = Math.Sqrt(curve.Average(v => (v - mean) * (v - mean)));
        if (!(sd > 0)) return result;
        var strength = curve.Select(v => v / sd).ToArray();

        double[] local = LocalPeriods(strength, period / TrackHopMs, cancel);
        int[] beats = TrackBeats(strength, local, cancel);
        if (beats.Length < 8) return result;

        // Each beat at the centre of its attack peak (the part above 70% of the peak within ±16 ms), which jitters less
        // than the single highest sample.
        var times = new double[beats.Length];
        var raw = new double[beats.Length];
        int onAttacks = 0;
        double peakLevel = Median(beats.Select(b => strength[b]).ToArray());
        int reach = (int)(16 / TrackHopMs);
        for (int k = 0; k < beats.Length; k++)
        {
            int b = beats[k];
            for (int i = Math.Max(0, beats[k] - reach); i <= Math.Min(n - 1, beats[k] + reach); i++)
                if (strength[i] > strength[b]) b = i;
            double limit = 0.7 * strength[b], sum = 0, weighted = 0;
            for (int i = Math.Max(0, b - reach); i <= Math.Min(n - 1, b + reach); i++)
            {
                double w = strength[i] - limit;
                if (w <= 0) continue;
                sum += w;
                weighted += w * i;
            }
            raw[k] = (sum > 0 ? weighted / sum : b) * TrackHopMs;
            times[k] = raw[k] - BeatCalibrationMs + options.CalibrationMs;
            if (strength[b] >= 0.3 * peakLevel) onAttacks++;
        }
        result.Beats = times;
        result.BeatsOnAttacks = (double)onAttacks / beats.Length;
        var intervals = times.Zip(times.Skip(1), (a, b) => b - a).Where(d => d > 0).OrderBy(d => d).ToArray();
        result.MinBpm = 60000.0 / intervals[(int)(intervals.Length * 0.95)];
        result.MaxBpm = 60000.0 / intervals[(int)(intervals.Length * 0.05)];
        // Downbeats from the accents of 8 bars around each beat, so a bar the tracking counted short does not shift all after it.
        int meter = Math.Max(1, options.Meter);
        var positions = BarPositions(Accents(bands, raw), meter, 8 * meter);
        result.Downbeats = positions.Select(position => position == 0).ToArray();
        result.Sections = Split(times.Select(t => t + options.OriginMs).ToArray(), options, result.Downbeats)
            .Select(section => section with { OffsetMs = section.OffsetMs - options.OriginMs }).ToArray();
        result.Confidence = result.BeatsOnAttacks >= 0.8 ? TimingConfidence.High
            : result.BeatsOnAttacks >= 0.6 ? TimingConfidence.Normal : TimingConfidence.Approximate;
        return result;
    }

    private static double Median(double[] values)
    {
        if (values.Length == 0) return 0;
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }

    /// <summary>The beat period (in samples of the curve) around every sample, from 6-second windows every half second.</summary>
    internal static double[] LocalPeriods(double[] curve, double period, CancellationToken cancel = default)
    {
        int n = curve.Length;
        int window = (int)(6000 / TrackHopMs), hop = (int)(500 / TrackHopMs);
        int minLag = Math.Max(2, (int)Math.Floor(period / LagRange)), maxLag = (int)Math.Ceiling(period * LagRange);
        var centres = new List<int>();
        var periods = new List<double>();
        for (int start = 0; start + Math.Min(window, n) <= n; start += hop)
        {
            cancel.ThrowIfCancellationRequested();
            int length = Math.Min(window, n - start);
            if (length <= maxLag + 2) break;
            double mean = 0;
            for (int i = 0; i < length; i++) mean += curve[start + i];
            mean /= length;
            var acf = new double[maxLag + 2];
            for (int lag = minLag - 1; lag <= maxLag + 1; lag++)
            {
                double s = 0;
                for (int i = 0; i + lag < length; i++) s += (curve[start + i] - mean) * (curve[start + i + lag] - mean);
                acf[lag] = s / (length - lag);
            }
            int best = minLag;
            for (int lag = minLag; lag <= maxLag; lag++) if (acf[lag] > acf[best]) best = lag;
            double y0 = acf[best - 1], y1 = acf[best], y2 = acf[best + 1], denom = y0 - 2 * y1 + y2;
            double delta = denom < 0 ? Math.Clamp(0.5 * (y0 - y2) / denom, -0.5, 0.5) : 0;
            // A window without a clear peak keeps the overall period.
            centres.Add(start + length / 2);
            periods.Add(acf[best] > 0 ? best + delta : period);
            if (start + window >= n) break;
        }
        var result = new double[n];
        if (periods.Count == 0)
        {
            Array.Fill(result, period);
            return result;
        }
        // A running median over 13 windows removes stray estimates.
        var smooth = new double[periods.Count];
        for (int i = 0; i < periods.Count; i++)
            smooth[i] = Median(periods.Skip(Math.Max(0, i - Spread)).Take(Math.Min(periods.Count, i + Spread + 1) - Math.Max(0, i - Spread)).ToArray());
        for (int i = 0, w = 0; i < n; i++)
        {
            while (w + 1 < centres.Count && centres[w + 1] <= i) w++;
            if (i <= centres[0]) result[i] = smooth[0];
            else if (w + 1 >= centres.Count) result[i] = smooth[^1];
            else
            {
                double f = (double)(i - centres[w]) / (centres[w + 1] - centres[w]);
                result[i] = smooth[w] * (1 - f) + smooth[w + 1] * f;
            }
        }
        return result;
    }

    /// <summary>
    /// The chain of beats that best balances landing on strong attacks against intervals that stray from the local
    /// period (dynamic programming as in Ellis 2007). Returns the beats as indices of the curve.
    /// </summary>
    internal static int[] TrackBeats(double[] strength, double[] period, CancellationToken cancel = default)
    {
        int n = strength.Length;
        var score = new double[n];
        var from = new int[n];
        for (int i = 0; i < n; i++)
        {
            if ((i & 16383) == 0) cancel.ThrowIfCancellationRequested();
            double p = period[i];
            int lo = i - (int)Math.Round(1.5 * p), hi = i - (int)Math.Round(0.6 * p);
            double best = double.NegativeInfinity;
            int previous = -1;
            for (int j = Math.Max(0, lo); j <= hi; j++)
            {
                double ratio = Math.Log((i - j) / p);
                double s = score[j] - Tightness * ratio * ratio;
                if (s > best)
                {
                    best = s;
                    previous = j;
                }
            }
            // Starting a chain here is always allowed; continuing one is better when it pays.
            score[i] = strength[i] + Math.Max(0, best);
            from[i] = best > 0 ? previous : -1;
        }
        // The chain ends at the best score within the last beat.
        int end = n - 1;
        int tail = Math.Max(0, n - (int)Math.Round(period[n - 1]));
        for (int i = tail; i < n; i++) if (score[i] > score[end]) end = i;
        var beats = new List<int>();
        for (int i = end; i >= 0; i = from[i]) beats.Add(i);
        beats.Reverse();
        return beats.ToArray();
    }

    /// <summary>
    /// Splits beat times (on the song's time line) into red lines as a mapper would set them.
    /// <list type="bullet">
    /// <item>Every red line starts on a 1/<see cref="VariableTimingOptions.Divisor"/> tick of the red line before it (the
    /// first on the grid of <see cref="VariableTimingOptions.Previous"/>, if given), so the space between two red lines is
    /// always a whole number of snaps; offsets are whole milliseconds and BPMs have three decimals, exactly as they will
    /// be added.</item>
    /// <item>A red line's BPM is fitted with its start held on that tick, and it runs as long as its beats, averaged over
    /// every 8 in a row, stay within the tolerance (and for at least <see cref="VariableTimingOptions.MinBeats"/>
    /// beats).</item>
    /// <item>With <paramref name="downbeats"/> known, the first red line starts on a downbeat, and a red line that has to
    /// start in the middle of a bar is followed by a correction red line (same BPM, on a beat tick of its grid) at the
    /// next downbeat, so the bar lines stay on the music's first beats.</item>
    /// </list>
    /// </summary>
    internal static TimingSection[] Split(double[] times, VariableTimingOptions options, bool[]? downbeats = null)
    {
        var sections = new List<TimingSection>();
        if (times.Length < 2) return sections.ToArray();
        int divisor = Math.Max(1, options.Divisor), minBeats = Math.Max(1, options.MinBeats), meter = Math.Max(1, options.Meter);
        int decimals = Math.Clamp(options.BpmDecimals, 0, 3);
        // The first red line on the first downbeat of the first two bars; the grid also reaches back before it.
        int s = 0;
        if (downbeats != null)
        {
            int firstDownbeat = Array.IndexOf(downbeats, true);
            if (firstDownbeat >= 0 && firstDownbeat < Math.Min(2 * meter, times.Length - 1)) s = firstDownbeat;
        }
        double start = options.Previous != null
            ? TimingUtil.NearestTick(options.Previous, Anchor(times, s), divisor, options.PreviousIsFirst)
            : Math.Round(Anchor(times, s), MidpointRounding.AwayFromZero);
        bool first = options.Previous == null;
        while (s < times.Length - 1)
        {
            int end = s + 1;
            var fit = Line(times, s, end, start, decimals);
            while (end + 1 < times.Length)
            {
                var next = Line(times, s, end + 1, start, decimals);
                if (next.drift > options.ToleranceMs && end - s + 1 >= minBeats) break;
                fit = next;
                end++;
            }
            bool last = end == times.Length - 1;
            var point = new TimingPoint { Offset = start, Bpm = fit.bpm, Meter = meter };
            // Started mid-bar: a correction red line on the next downbeat within this tempo, on a beat tick of its grid.
            int correction = -1;
            if (downbeats != null && !downbeats[s])
                for (int k = s + 1; k <= end; k++)
                    if (downbeats[k])
                    {
                        correction = k;
                        break;
                    }
            if (correction > s && (correction < end || last))
            {
                double at = TimingUtil.TickAt(point, (long)(correction - s) * divisor, divisor, first);
                sections.Add(new TimingSection(start, fit.bpm, correction - s, fit.worst));
                sections.Add(new TimingSection(at, fit.bpm, last ? end - correction + 1 : end - correction, fit.worst, Correction: true));
                point = new TimingPoint { Offset = at, Bpm = fit.bpm, Meter = meter };
                first = false;
            }
            else sections.Add(new TimingSection(start, fit.bpm, last ? end - s + 1 : end - s, fit.worst));
            if (last) break;
            // The next red line: the tick of the latest red line's grid nearest the beat where this tempo stops fitting.
            double next0 = TimingUtil.NearestTick(point, Anchor(times, end), divisor, first);
            if (next0 <= point.Offset) next0 = TimingUtil.TickAt(point, 1, divisor, first);
            start = next0;
            first = false;
            s = end;
        }
        return sections.ToArray();
    }

    /// <summary>
    /// Where the beats from <paramref name="index"/> on put their first one: a straight line through up to 8 of them,
    /// read at the first. Steadier than the single tracked beat, whose own position jitters by a few milliseconds.
    /// </summary>
    private static double Anchor(double[] times, int index)
    {
        int last = Math.Min(times.Length - 1, index + 7), count = last - index + 1;
        if (count < 3) return times[index];
        double kMean = (count - 1) / 2.0, tMean = 0, skk = 0, skt = 0;
        for (int i = index; i <= last; i++) tMean += times[i];
        tMean /= count;
        for (int i = index; i <= last; i++)
        {
            double k = i - index - kMean;
            skk += k * k;
            skt += k * (times[i] - tMean);
        }
        return tMean - skt / skk * kMean;
    }

    /// <summary>
    /// The BPM (to <paramref name="decimals"/> decimals) of a red line at <paramref name="start"/> that best fits beats first..last, with the
    /// first beat counted as beat 0 of the red line; its largest drift (the worst average miss over 8 beats in a row, over
    /// all when fewer) and its worst single miss.
    /// </summary>
    private static (double bpm, double drift, double worst) Line(double[] times, int first, int last, double start, int decimals)
    {
        double skk = 0, skt = 0;
        for (int i = first + 1; i <= last; i++)
        {
            double k = i - first;
            skk += k * k;
            skt += k * (times[i] - start);
        }
        double period = skk > 0 && skt > 0 ? skt / skk : times[last] - times[first];
        // Rounded first, then everything below uses the BPM the red line will really have.
        double bpm = Math.Round(60000.0 / period, decimals, MidpointRounding.AwayFromZero);
        if (!(bpm > 0)) bpm = Math.Pow(10, -decimals);
        period = 60000.0 / bpm;
        int count = last - first + 1, run = Math.Min(8, count);
        double window = 0, drift = 0, worst = 0;
        for (int i = first; i <= last; i++)
        {
            double miss = times[i] - start - (i - first) * period;
            worst = Math.Max(worst, Math.Abs(miss));
            window += miss;
            if (i - first >= run) window -= times[i - run] - start - (i - run - first) * period;
            if (i - first >= run - 1) drift = Math.Max(drift, Math.Abs(window / run));
        }
        return (bpm, drift, worst);
    }
}
