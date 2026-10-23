namespace SbStudio.Core;

public enum TimingConfidence
{
    Failed = -1,
    High = 0,
    Normal = 1,
    /// <summary>A beat was found, but it fits the music only loosely.</summary>
    Approximate = 2,
}

/// <summary>How the beats of one 10-second stretch sit against the found timing.</summary>
public enum TimingColumnFlag
{
    None,
    /// <summary>Few beats could be heard where the timing puts them: another timing section may start here.</summary>
    NoBeat,
    /// <summary>The beats here come consistently earlier than the timing.</summary>
    Earlier,
    /// <summary>The beats here come consistently later than the timing.</summary>
    Later,
    /// <summary>The beats here scatter widely around the timing: drifting tempo or loose playing.</summary>
    Unsteady,
}

public sealed class TimingAnalysis
{
    public double Bpm { get; internal set; }
    /// <summary>Standard error of <see cref="Bpm"/>.</summary>
    public double BpmError { get; internal set; }
    /// <summary>
    /// The first downbeat (first beat of a bar) at or after the start of the analysed audio, in milliseconds from that start.
    /// </summary>
    public double OffsetMs { get; internal set; }
    /// <summary>Standard error of <see cref="OffsetMs"/>.</summary>
    public double OffsetError { get; internal set; }
    public TimingConfidence Confidence { get; internal set; }
    /// <summary>Beats per bar the offset was placed for (on the first beat of a bar).</summary>
    public int Meter { get; internal set; } = 4;
    /// <summary>Share of the expected beats that were heard (0..1).</summary>
    public double BeatsHeard { get; internal set; }
    /// <summary>Spread of the heard beats around the timing (robust standard deviation, ms).</summary>
    public double SpreadMs { get; internal set; }
    /// <summary>One flag per 10 seconds of the analysed audio.</summary>
    public TimingColumnFlag[] Columns { get; internal set; } = Array.Empty<TimingColumnFlag>();

    /// <summary>'?' no beat, '&lt;' earlier, '&gt;' later, '~' unsteady, ' ' fine.</summary>
    public static char Symbol(TimingColumnFlag flag) => flag switch
    {
        TimingColumnFlag.NoBeat => '?',
        TimingColumnFlag.Earlier => '<',
        TimingColumnFlag.Later => '>',
        TimingColumnFlag.Unsteady => '~',
        _ => ' ',
    };

    public string Timeline => new(Columns.Select(Symbol).ToArray());
}

/// <summary>
/// Estimates the average BPM and offset of a stretch of music. The steps follow well-known published methods; the
/// combination, parameters and calibration are this program's own, tuned against ranked osu! maps of constant BPM.
/// <list type="number">
/// <item>Onset strength: spectral flux, the positive change of log-compressed STFT magnitudes between frames
/// (Bello et al., "A Tutorial on Onset Detection in Music Signals", IEEE TSAP 13(5), 2005), compared against the
/// maximum of neighbouring bins of the previous frame so vibrato does not count as onsets (Böck &amp; Widmer,
/// "Maximum Filter Vibrato Suppression for Onset Detection", DAFx-13, 2013).</item>
/// <item>Rough tempo: the autocorrelation of the onset strength with a log-normal preference for common tempos
/// (Ellis, "Beat Tracking by Dynamic Programming", J. New Music Research 36(1), 2007).</item>
/// <item>Exact tempo: the period at which the Fourier coefficient of the onset strength (with harmonics) is largest,
/// searched on a continuous scale (the Fourier tempogram of Grosche &amp; Müller, "Extracting Predominant Local Pulse
/// Information From Music Recordings", IEEE TASLP 19(6), 2011), so the period is not rounded to frames.</item>
/// <item>Offset: the log energy of six frequency bands, each relative to its own average level and summed so every
/// band counts alike (after Klapuri, "Sound Onset Detection by Applying Psychoacoustic Knowledge", ICASSP 1999). Its
/// rise is folded at the beat period over the whole stretch at quarter-millisecond resolution, and the centre of the
/// folded peak, less a calibration, is the offset. Band filters are zero-phase biquads from R. Bristow-Johnson's
/// "Audio EQ Cookbook".</item>
/// <item>Errors and checks: the strongest rise near every grid point is located on the same curve, and a weighted
/// straight-line fit through them gives standard errors and the per-10-second flags.</item>
/// </list>
/// </summary>
public static partial class TimingAnalyzer
{
    public const int ColumnMs = 10000;
    public const double MinBpm = 40, MaxBpm = 400;
    /// <summary>
    /// The folded attack peak lies this much after the offset that mappers set: the median over 160 ranked osu! maps with
    /// a single timing section (four random samples of 40; their medians spread over about 1 ms).
    /// </summary>
    public const double AttackCalibrationMs = 14.4;

    private const int FrameSamples44 = 2048, HopSamples44 = 256;
    private const double StepMs = 0.25, BandWindowMs = 20, RiseMs = 10;
    // The offset step reads a smoother curve: 30 ms windows, the rise over ±15 ms, without the top band (cymbals and
    // hi-hats smear the attack), and the centre of the top 15% of the folded peak. Chosen among 216 variants on 80
    // ranked maps, and confirmed on 80 others (within ±5 ms: 46% against 41% for the curve the beats are tracked on).
    private const double OffsetWindowMs = 30, OffsetRiseMs = 15, OffsetShare = 0.85;
    private static readonly int[] OffsetBands = { 0, 1, 2, 3, 4 };

    /// <summary>
    /// Analyses mono samples (any scale; the channels may be summed). <paramref name="exactBpm"/> above 0 keeps that
    /// BPM and measures only the offset; <paramref name="tempoHint"/> above 0 looks for the beat near that BPM instead
    /// of the most likely one (for the half or double tempo). <paramref name="calibrationMs"/> is added to the offset: the
    /// user's correction of the built-in calibration to their own preference.
    /// </summary>
    public static TimingAnalysis Analyze(ReadOnlySpan<float> mono, double sampleRate, double exactBpm = 0, double tempoHint = 0, int meter = 4,
        double calibrationMs = 0, CancellationToken cancel = default)
    {
        var result = new TimingAnalysis { Confidence = TimingConfidence.Failed };
        if (mono.Length < sampleRate * 2 || sampleRate < 8000) return result;
        float[] audio = Normalized(mono, out bool silent);
        if (silent) return result;
        double length = audio.Length * 1000.0 / sampleRate;

        double period;
        if (exactBpm > 0) period = 60000.0 / exactBpm;
        else
        {
            var envelope = OnsetEnvelope(audio, sampleRate, cancel);
            double rough = RoughPeriod(envelope, tempoHint);
            if (!(rough > 0)) return result;
            cancel.ThrowIfCancellationRequested();
            period = FinePeriod(envelope, rough);
        }
        cancel.ThrowIfCancellationRequested();

        var bands = new BandEnergy(audio, sampleRate, BandWindowMs);
        var offsetBands = bands.WithWindow(OffsetWindowMs).Subset(OffsetBands);
        double Rise(double t) => Math.Max(0, offsetBands.Level(t + OffsetRiseMs) - offsetBands.Level(t - OffsetRiseMs));
        double[] fold = Fold(Rise, period, length, cancel);
        double attack = Centroid(fold, Peak(fold), OffsetShare);
        cancel.ThrowIfCancellationRequested();

        // The red line goes on the first beat of a bar (a red line starts a bar in osu!), so the bar lines fall on the
        // music's downbeats: the beat of the grid whose bar position, from the accents of all beats, is 0.
        // The user's own calibration moves the result later (positive) or earlier (negative).
        double first = attack - AttackCalibrationMs + calibrationMs;
        int firstIndex = (int)Math.Ceiling(-first / period);
        var grid = new List<double>();
        for (int k = firstIndex; first + k * period < length; k++) grid.Add(attack + k * period);
        int barStart = 0;
        if (grid.Count >= 2 * Math.Max(1, meter))
        {
            var positions = BarPositions(Accents(bands, grid), meter, grid.Count * 2);
            barStart = Array.IndexOf(positions, 0);
            if (barStart < 0) barStart = 0;
        }
        double offset = first + (firstIndex + barStart) * period;
        result.Bpm = 60000.0 / period;
        result.OffsetMs = offset;
        result.Meter = Math.Max(1, meter);

        var check = CheckBeats(Rise, fold, length, period, attack);
        if (check == null || check.Heard < 8) return result;
        result.BpmError = exactBpm > 0 ? 0 : 60000.0 / (period * period) * check.PeriodError;
        double beatsFromCentre = (offset - calibrationMs + AttackCalibrationMs - check.CentreTime) / period;
        result.OffsetError = Math.Sqrt(check.CentreError * check.CentreError + Math.Pow(beatsFromCentre * check.PeriodError, 2));
        result.BeatsHeard = (double)check.Heard / check.Expected;
        result.SpreadMs = check.Spread;
        result.Columns = check.Columns;
        result.Confidence = result.BeatsHeard >= 0.6 && check.Spread <= 6 && result.BpmError < 0.01 ? TimingConfidence.High
            : result.BeatsHeard >= 0.35 && check.Spread <= 12 && result.BpmError < 0.05 ? TimingConfidence.Normal
            : TimingConfidence.Approximate;
        return result;
    }

    private static float[] Normalized(ReadOnlySpan<float> mono, out bool silent)
    {
        float peak = 0;
        foreach (float s in mono) peak = Math.Max(peak, Math.Abs(s));
        silent = !(peak > 1e-5f);
        var audio = new float[mono.Length];
        if (silent) return audio;
        float scale = 1f / peak;
        for (int i = 0; i < audio.Length; i++) audio[i] = mono[i] * scale;
        return audio;
    }

    // ---------------------------------------------------------------- onset strength and tempo

    /// <summary>Onset strength at evenly spaced times: value i belongs to time i × <see cref="HopMs"/>.</summary>
    internal sealed class Envelope
    {
        public required float[] Values;
        public required double HopMs;
    }

    internal static Envelope OnsetEnvelope(float[] audio, double sampleRate, CancellationToken cancel = default)
    {
        // Frame and hop scale with the sample rate, so their lengths in time stay the same.
        int size = 512;
        while (size < FrameSamples44 * sampleRate / 44100.0) size <<= 1;
        int hop = Math.Max(1, (int)Math.Round(HopSamples44 * sampleRate / 44100.0));
        int frames = audio.Length / hop + 1;
        var window = new float[size];
        for (int i = 0; i < size; i++) window[i] = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / size);
        int low = Math.Max(1, (int)(30.0 * size / sampleRate)), high = Math.Min(size / 2 - 1, (int)(16000.0 * size / sampleRate));
        var fft = new Fft(size);
        var re = new float[size];
        var im = new float[size];
        var previous = new float[high + 2];
        var current = new float[high + 2];
        var flux = new float[frames];
        float gain = 1000f / size;
        for (int f = 0; f < frames; f++)
        {
            if ((f & 1023) == 0) cancel.ThrowIfCancellationRequested();
            int start = f * hop - size / 2;
            for (int i = 0; i < size; i++)
            {
                int at = start + i;
                re[i] = at >= 0 && at < audio.Length ? audio[at] * window[i] : 0;
                im[i] = 0;
            }
            fft.Forward(re, im);
            float sum = 0;
            for (int k = low; k <= high; k++)
            {
                current[k] = MathF.Log(1 + gain * MathF.Sqrt(re[k] * re[k] + im[k] * im[k]));
                if (f > 0)
                {
                    float before = MathF.Max(previous[k], MathF.Max(previous[k - 1], previous[k + 1]));
                    if (current[k] > before) sum += current[k] - before;
                }
            }
            flux[f] = sum;
            (previous, current) = (current, previous);
        }

        // Only rises above the local level count, so loud and quiet passages weigh alike.
        double hopMs = hop * 1000.0 / sampleRate;
        int half = Math.Max(1, (int)Math.Round(100 / hopMs));
        var values = new float[frames];
        double running = 0;
        int count = 0;
        for (int i = -half; i < frames; i++)
        {
            int add = i + half, remove = i - half - 1;
            if (add < frames) { running += flux[add]; count++; }
            if (remove >= 0) { running -= flux[remove]; count--; }
            if (i >= 0) values[i] = Math.Max(0, flux[i] - (float)(running / count));
        }
        return new Envelope { Values = values, HopMs = hopMs };
    }

    /// <summary>The beat period, roughly (ms), from the onset strength's autocorrelation; 0 when there is no beat.</summary>
    internal static double RoughPeriod(Envelope envelope, double tempoHint)
    {
        int n = envelope.Values.Length;
        int maxBeatLag = (int)Math.Ceiling(60000.0 / MinBpm / envelope.HopMs);
        int minLag = Math.Max(2, (int)Math.Floor(60000.0 / MaxBpm / envelope.HopMs));
        int lags = Math.Min(n / 2, 2 * maxBeatLag + 2);
        if (lags <= minLag + 4) return 0;
        double[] acf = Autocorrelation(envelope.Values, lags);
        if (!(acf[0] > 0)) return 0;
        double At(double lag)
        {
            int i = (int)lag;
            if (i + 1 >= acf.Length) return 0;
            double t = lag - i;
            return (acf[i] * (1 - t) + acf[i + 1] * t) / acf[0];
        }

        // A tempo is likelier when onsets also line up at its double and half periods, and when it lies where most
        // music is counted; a hint takes the place of that range.
        double centre = tempoHint > 0 ? tempoHint : 150, width = tempoHint > 0 ? 0.25 : 1.1;
        double best = 0, bestScore = 0;
        for (int lag = minLag; lag <= Math.Min(maxBeatLag, lags - 2); lag++)
        {
            if (!(acf[lag] > 0) || acf[lag] < acf[lag - 1] || acf[lag] < acf[lag + 1]) continue;
            double score = acf[lag] / acf[0] + 0.5 * At(2 * lag) + 0.25 * At(lag / 2.0);
            double octaves = Math.Log2(60000.0 / (lag * envelope.HopMs) / centre) / width;
            score *= Math.Exp(-0.5 * octaves * octaves);
            if (score <= bestScore) continue;
            bestScore = score;
            double y0 = acf[lag - 1], y1 = acf[lag], y2 = acf[lag + 1], denom = y0 - 2 * y1 + y2;
            double delta = denom < 0 ? Math.Clamp(0.5 * (y0 - y2) / denom, -0.5, 0.5) : 0;
            best = (lag + delta) * envelope.HopMs;
        }
        return best;
    }

    private static double[] Autocorrelation(float[] signal, int lags)
    {
        int size = 1;
        while (size < signal.Length + lags) size <<= 1;
        var fft = new Fft(size);
        var re = new float[size];
        var im = new float[size];
        double mean = 0;
        foreach (float v in signal) mean += v;
        mean /= signal.Length;
        for (int i = 0; i < signal.Length; i++) re[i] = (float)(signal[i] - mean);
        fft.Forward(re, im);
        for (int i = 0; i < size; i++)
        {
            re[i] = re[i] * re[i] + im[i] * im[i];
            im[i] = 0;
        }
        fft.Inverse(re, im);
        var acf = new double[lags];
        // Per overlapping pair, so long lags are not penalised for being long.
        for (int i = 0; i < lags; i++) acf[i] = re[i] / Math.Max(1, signal.Length - i);
        return acf;
    }

    /// <summary>How strongly the onset strength repeats with a period: its Fourier coefficients there and at the first harmonics, relative to its total.</summary>
    internal static double Periodicity(Envelope envelope, double period)
    {
        float[] o = envelope.Values;
        double total = 0, score = 0;
        foreach (float v in o) total += v;
        for (int h = 1; h <= 4; h++)
        {
            double step = 2 * Math.PI * h * envelope.HopMs / period;
            double cr = Math.Cos(step), ci = -Math.Sin(step), wr = 1, wi = 0, sr = 0, si = 0;
            for (int i = 0; i < o.Length; i++)
            {
                sr += o[i] * wr;
                si += o[i] * wi;
                double nr = wr * cr - wi * ci;
                wi = wr * ci + wi * cr;
                wr = nr;
                if ((i & 4095) == 4095)
                {
                    // Keeps the rotating unit vector at unit length over long songs.
                    double len = Math.Sqrt(wr * wr + wi * wi);
                    wr /= len;
                    wi /= len;
                }
            }
            score += Math.Sqrt(sr * sr + si * si) / h;
        }
        return total > 0 ? score / total : 0;
    }

    /// <summary>The period near a rough one at which the onset strength repeats most strongly, on a continuous scale.</summary>
    internal static double FinePeriod(Envelope envelope, double rough)
    {
        double lo = rough * 0.98, hi = rough * 1.02;
        const int steps = 160;
        double best = rough, bestScore = double.MinValue;
        for (int i = 0; i <= steps; i++)
        {
            double p = lo + (hi - lo) * i / steps, s = Periodicity(envelope, p);
            if (s > bestScore)
            {
                bestScore = s;
                best = p;
            }
        }
        // Golden-section search between the grid neighbours of the best period.
        double step = (hi - lo) / steps, a = best - step, b = best + step, g = (Math.Sqrt(5) - 1) / 2;
        double c = b - g * (b - a), d = a + g * (b - a);
        double fc = Periodicity(envelope, c), fd = Periodicity(envelope, d);
        for (int i = 0; i < 40; i++)
        {
            if (fc > fd) { b = d; d = c; fd = fc; c = b - g * (b - a); fc = Periodicity(envelope, c); }
            else { a = c; c = d; fc = fd; d = a + g * (b - a); fd = Periodicity(envelope, d); }
        }
        return (a + b) / 2;
    }

    // ---------------------------------------------------------------- band energy and offset

    internal enum FilterKind { LowPass, HighPass, BandPass }

    /// <summary>A biquad (R. Bristow-Johnson's Audio EQ Cookbook), run forwards and backwards so it does not delay the signal.</summary>
    internal static float[] Filter(float[] audio, double sampleRate, FilterKind kind, double frequency, double q = 0.7071)
    {
        double w = 2 * Math.PI * frequency / sampleRate, alpha = Math.Sin(w) / (2 * q), cos = Math.Cos(w);
        double a0 = 1 + alpha, a1 = -2 * cos / a0, a2 = (1 - alpha) / a0;
        double b0, b1, b2;
        switch (kind)
        {
            case FilterKind.LowPass: b0 = (1 - cos) / 2 / a0; b1 = (1 - cos) / a0; b2 = b0; break;
            case FilterKind.HighPass: b0 = (1 + cos) / 2 / a0; b1 = -(1 + cos) / a0; b2 = b0; break;
            default: b0 = alpha / a0; b1 = 0; b2 = -alpha / a0; break;
        }
        var y = (float[])audio.Clone();
        for (int direction = 0; direction < 2; direction++)
        {
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (int n = 0; n < y.Length; n++)
            {
                int i = direction == 0 ? n : y.Length - 1 - n;
                double x = y[i], v = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
                x2 = x1; x1 = x; y2 = y1; y1 = v;
                y[i] = (float)v;
            }
        }
        return y;
    }

    /// <summary>
    /// Six frequency bands, each as its energy over a short window relative to its own average and log-compressed,
    /// summed: a kick, a snare and a hi-hat all mark the beat, however loud each band is.
    /// </summary>
    internal sealed class BandEnergy
    {
        private readonly double[][] sums;
        private readonly double[] means;
        private readonly double rate, windowMs;
        private readonly int[] used;

        private BandEnergy(double[][] sums, double[] means, double rate, double windowMs, int[] used)
        {
            this.sums = sums;
            this.means = means;
            this.rate = rate;
            this.windowMs = windowMs;
            this.used = used;
        }

        /// <summary>The same bands over another window length, without filtering again.</summary>
        public BandEnergy WithWindow(double ms) => new(sums, means, rate, ms, used);

        /// <summary>Only some of the bands (by index, 0 = bass) count in <see cref="Level"/>.</summary>
        public BandEnergy Subset(int[] bands) => new(sums, means, rate, windowMs, bands);

        public BandEnergy(float[] audio, double sampleRate, double windowMs)
        {
            rate = sampleRate;
            this.windowMs = windowMs;
            var bands = new[]
            {
                Filter(audio, sampleRate, FilterKind.LowPass, 120),
                Filter(audio, sampleRate, FilterKind.BandPass, 250, 1.2),
                Filter(audio, sampleRate, FilterKind.BandPass, 600, 1.2),
                Filter(audio, sampleRate, FilterKind.BandPass, 1500, 1.2),
                Filter(audio, sampleRate, FilterKind.BandPass, 3500, 1.2),
                Filter(audio, sampleRate, FilterKind.HighPass, 7000),
            };
            sums = new double[bands.Length][];
            means = new double[bands.Length];
            for (int b = 0; b < bands.Length; b++)
            {
                var s = sums[b] = new double[audio.Length + 1];
                for (int i = 0; i < audio.Length; i++) s[i + 1] = s[i] + (double)bands[b][i] * bands[b][i];
                means[b] = Math.Max(1e-12, s[^1] / audio.Length);
            }
            used = Enumerable.Range(0, bands.Length).ToArray();
        }

        /// <summary>The summed relative log energy around a time (ms).</summary>
        public double Level(double ms)
        {
            double level = 0;
            foreach (int b in used) level += BandLevel(b, ms);
            return level;
        }

        /// <summary>The relative log energy of one band around a time (ms); band 0 is the bass, where the kick drum lies.</summary>
        public double BandLevel(int band, double ms)
        {
            double centre = ms * rate / 1000, half = windowMs * rate / 2000;
            var s = sums[band];
            double from = Math.Clamp(centre - half, 0, s.Length - 1), to = Math.Clamp(centre + half, 0, s.Length - 1);
            double energy = to > from ? (At(s, to) - At(s, from)) / (to - from) : 0;
            return Math.Log(1 + energy / means[band]);
        }

        /// <summary>
        /// How strongly a beat is accented: the rise of the bass band (the kick drum on strong beats) plus half the rise
        /// of all bands together, each in units of its average.
        /// </summary>
        public (double Bass, double All) Rise(double ms) =>
            (Math.Max(0, BandLevel(0, ms + RiseMs) - BandLevel(0, ms - RiseMs)), Math.Max(0, Level(ms + RiseMs) - Level(ms - RiseMs)));

        private static double At(double[] s, double index)
        {
            int i = (int)index;
            if (i >= s.Length - 1) return s[^1];
            double t = index - i;
            return s[i] * (1 - t) + s[i + 1] * t;
        }
    }

    /// <summary>Folds a curve at a period: its sum over all beats at every quarter millisecond within the beat, lightly smoothed.</summary>
    internal static double[] Fold(Func<double, double> curve, double period, double lengthMs, CancellationToken cancel = default)
    {
        int bins = (int)Math.Ceiling(period / StepMs);
        var sum = new double[bins];
        long step = 0;
        for (double t = 0; t < lengthMs; t = ++step * StepMs)
        {
            if ((step & 65535) == 0) cancel.ThrowIfCancellationRequested();
            double position = t % period / StepMs;
            int b = (int)position;
            double f = position - b, v = curve(t);
            sum[b % bins] += v * (1 - f);
            sum[(b + 1) % bins] += v * f;
        }
        // Weights 1-2-3-2-1 over ±0.5 ms, summing to one.
        var smooth = new double[bins];
        for (int i = 0; i < bins; i++)
            for (int j = -2; j <= 2; j++) smooth[i] += sum[((i + j) % bins + bins) % bins] * (3 - Math.Abs(j)) / 9.0;
        return smooth;
    }

    private static double Peak(double[] fold)
    {
        int bins = fold.Length, best = 0;
        for (int i = 1; i < bins; i++) if (fold[i] > fold[best]) best = i;
        double y0 = fold[(best - 1 + bins) % bins], y1 = fold[best], y2 = fold[(best + 1) % bins], denom = y0 - 2 * y1 + y2;
        return (best + (denom < 0 ? Math.Clamp(0.5 * (y0 - y2) / denom, -0.5, 0.5) : 0)) * StepMs;
    }

    /// <summary>The centre of mass of a folded peak, where it is above a share of its height.</summary>
    private static double Centroid(double[] fold, double peakMs, double share)
    {
        int bins = fold.Length, peak = (int)Math.Round(peakMs / StepMs);
        double F(int i) => fold[(i % bins + bins) % bins];
        double limit = share * F(peak), sum = 0, weighted = 0;
        int lo = peak, hi = peak;
        while (lo > peak - bins / 4 && F(lo - 1) >= limit) lo--;
        while (hi < peak + bins / 4 && F(hi + 1) >= limit) hi++;
        for (int i = lo; i <= hi; i++)
        {
            double w = F(i) - limit;
            sum += w;
            weighted += w * i;
        }
        double total = bins * StepMs, ms = (sum > 0 ? weighted / sum : peak) * StepMs;
        return (ms % total + total) % total;
    }

    /// <summary>
    /// The start of the attack in a folded curve: going back from its peak, where it crosses a share of the way from
    /// its lowest value in the preceding 60 ms up to the peak.
    /// </summary>
    internal static double Knee(double[] fold, double peakMs, double share)
    {
        int bins = fold.Length, peak = (int)Math.Round(peakMs / StepMs), back = (int)(60 / StepMs);
        double F(int i) => fold[(i % bins + bins) % bins];
        double low = double.MaxValue;
        for (int i = peak - back; i <= peak; i++) low = Math.Min(low, F(i));
        double threshold = low + share * (F(peak) - low);
        int at = peak;
        while (at > peak - back && F(at - 1) >= threshold) at--;
        double a = F(at - 1), b = F(at), frac = b != a ? Math.Clamp((threshold - a) / (b - a), 0, 1) : 0;
        double total = bins * StepMs, ms = (at - 1 + frac) * StepMs;
        return (ms % total + total) % total;
    }

    /// <summary>
    /// Evaluation only: where the attack lies (before calibration) by many variants of the offset step, for a known
    /// period, so they can be compared against ranked maps.
    /// </summary>
    internal static List<(string Label, double Attack)> AttackCandidates(ReadOnlySpan<float> mono, double sampleRate, double period)
    {
        float[] audio = Normalized(mono, out _);
        double length = audio.Length * 1000.0 / sampleRate;
        var full = new BandEnergy(audio, sampleRate, 20);
        var sets = new (string Name, int[] Bands)[]
        {
            ("all", new[] { 0, 1, 2, 3, 4, 5 }), ("noHigh", new[] { 0, 1, 2, 3, 4 }), ("low4", new[] { 0, 1, 2, 3 }), ("upper4", new[] { 2, 3, 4, 5 }),
        };
        var result = new List<(string, double)>();
        foreach (double window in new[] { 10.0, 20, 30 })
            foreach (var (name, set) in sets)
            {
                var view = full.WithWindow(window).Subset(set);
                foreach (double rise in new[] { 5.0, 10, 15 })
                {
                    double[] fold = Fold(t => Math.Max(0, view.Level(t + rise) - view.Level(t - rise)), period, length);
                    double peak = Peak(fold);
                    string label = $"w{window}-{name}-r{rise}";
                    result.Add(($"{label}-peak", peak));
                    foreach (double share in new[] { 0.5, 0.7, 0.85 }) result.Add(($"{label}-c{share * 100:0}", Centroid(fold, peak, share)));
                    foreach (double share in new[] { 0.3, 0.5 }) result.Add(($"{label}-k{share * 100:0}", Knee(fold, peak, share)));
                }
            }
        return result;
    }

    // ---------------------------------------------------------------- bars

    /// <summary>
    /// How much each beat of a bar is expected to be stressed: the first most, and in an even metre the middle one half
    /// as much (4/4: strong, weak, medium, weak; 3/4: strong, weak, weak).
    /// </summary>
    internal static double[] BarPattern(int meter)
    {
        var weights = new double[Math.Max(1, meter)];
        weights[0] = 1;
        if (meter >= 4 && meter % 2 == 0) weights[meter / 2] = 0.5;
        return weights;
    }

    /// <summary>
    /// Accent of every beat in a list of beat times: its bass and overall attack (strongest within ±15 ms), each relative
    /// to the average over all the beats so neither dominates by scale.
    /// </summary>
    internal static double[] Accents(BandEnergy bands, IReadOnlyList<double> rawTimes)
    {
        var bass = new double[rawTimes.Count];
        var all = new double[rawTimes.Count];
        for (int k = 0; k < rawTimes.Count; k++)
            for (double t = rawTimes[k] - 15; t <= rawTimes[k] + 15; t += 1)
            {
                var (b, a) = bands.Rise(t);
                bass[k] = Math.Max(bass[k], b);
                all[k] = Math.Max(all[k], a);
            }
        double bassMean = Math.Max(1e-9, bass.Average()), allMean = Math.Max(1e-9, all.Average());
        return bass.Select((b, k) => b / bassMean + 0.5 * all[k] / allMean).ToArray();
    }

    /// <summary>
    /// Which beat of the bar every beat is: for each, the bar position (0 = downbeat) under the phase that best matches
    /// the accents of the surrounding <paramref name="window"/> beats to <see cref="BarPattern"/>. A window covering
    /// all beats gives one phase for the whole piece.
    /// </summary>
    internal static int[] BarPositions(double[] accents, int meter, int window)
    {
        var pattern = BarPattern(meter);
        int m = pattern.Length, n = accents.Length;
        var positions = new int[n];
        for (int k = 0; k < n; k++)
        {
            int lo = Math.Max(0, k - window / 2), hi = Math.Min(n - 1, k + window / 2);
            int bestPhase = 0;
            double best = double.MinValue;
            for (int phase = 0; phase < m; phase++)
            {
                double score = 0;
                for (int j = lo; j <= hi; j++) score += accents[j] * pattern[((j - phase) % m + m) % m];
                if (score > best)
                {
                    best = score;
                    bestPhase = phase;
                }
            }
            positions[k] = ((k - bestPhase) % m + m) % m;
        }
        return positions;
    }

    // ---------------------------------------------------------------- checks

    internal sealed class BeatCheck
    {
        public double PeriodError, CentreTime, CentreError, Spread;
        public int Heard, Expected;
        public TimingColumnFlag[] Columns = Array.Empty<TimingColumnFlag>();
    }

    /// <summary>
    /// Locates the strongest rise near every beat of the grid and fits a weighted straight line through the ones heard:
    /// the scatter gives the standard errors, and each 10 seconds is flagged by how its beats sit.
    /// </summary>
    internal static BeatCheck? CheckBeats(Func<double, double> rise, double[] fold, double lengthMs, double period, double attack)
    {
        double radius = Math.Min(period * 0.2, 30);
        if (lengthMs / period < 4) return null;
        // A beat counts as heard when its rise reaches 40% of the average rise at the folded peak.
        double peakRise = fold.Max() / Math.Max(1, lengthMs / period);
        var beats = new List<(int Index, double Expected, double Time, double Strength)>();
        var missed = new List<double>();
        for (int k = (int)Math.Ceiling((radius - attack) / period); attack + k * period < lengthMs - radius; k++)
        {
            double expected = attack + k * period, bestTime = expected, best = 0;
            for (double t = expected - radius; t <= expected + radius; t += 0.5)
            {
                double r = rise(t);
                if (r > best)
                {
                    best = r;
                    bestTime = t;
                }
            }
            if (best >= 0.4 * peakRise) beats.Add((k, expected, bestTime, best));
            else missed.Add(expected);
        }
        var check = new BeatCheck { Expected = beats.Count + missed.Count };
        if (beats.Count < 4) return check;

        // Weighted least squares time = a + b·index, twice dropping beats far from the line.
        var used = beats;
        double slope = period, intercept = attack, kMean = 0, tMean = 0, w = 0, skk = 0;
        for (int round = 0; round < 3; round++)
        {
            w = used.Sum(b => b.Strength);
            kMean = used.Sum(b => b.Strength * b.Index) / w;
            tMean = used.Sum(b => b.Strength * b.Time) / w;
            skk = used.Sum(b => b.Strength * (b.Index - kMean) * (b.Index - kMean));
            double skt = used.Sum(b => b.Strength * (b.Index - kMean) * (b.Time - tMean));
            if (!(skk > 0)) return check;
            slope = skt / skk;
            intercept = tMean - slope * kMean;
            double s = slope, i0 = intercept;
            var deviations = used.Select(b => Math.Abs(b.Time - i0 - s * b.Index)).OrderBy(x => x).ToList();
            double limit = Math.Max(3 * 1.4826 * deviations[deviations.Count / 2], 2.5);
            var kept = used.Where(b => Math.Abs(b.Time - i0 - s * b.Index) <= limit).ToList();
            if (kept.Count == used.Count || kept.Count < 4) break;
            used = kept;
        }
        double finalSlope = slope, finalIntercept = intercept;
        double effective = w * w / used.Sum(b => b.Strength * b.Strength);
        double residual2 = used.Sum(b => b.Strength * Math.Pow(b.Time - finalIntercept - finalSlope * b.Index, 2)) / w;
        double variance = used.Count > 2 ? residual2 * used.Count / (used.Count - 2) : residual2;
        check.PeriodError = Math.Sqrt(variance / (skk / w * effective));
        check.CentreTime = tMean;
        check.CentreError = Math.Sqrt(variance / effective);
        var spread = used.Select(b => Math.Abs(b.Time - finalIntercept - finalSlope * b.Index)).OrderBy(x => x).ToList();
        check.Spread = spread[spread.Count / 2] * 1.4826;
        check.Heard = used.Count;

        // Per 10 seconds, against the grid the offset came from (not the fitted line, which may lean on a few parts).
        // The strongest rise of a beat sits a fixed distance from the folded centre; that distance is taken out first.
        int columns = Math.Max(1, (int)Math.Ceiling(lengthMs / ColumnMs));
        check.Columns = new TimingColumnFlag[columns];
        var usedSet = used.Select(b => b.Index).ToHashSet();
        var allResiduals = used.Select(b => b.Time - b.Expected).OrderBy(r => r).ToList();
        double typical = allResiduals[allResiduals.Count / 2];
        for (int c = 0; c < columns; c++)
        {
            var here = beats.Where(b => (int)(b.Expected / ColumnMs) == c).ToList();
            int total = here.Count + missed.Count(m => (int)(m / ColumnMs) == c);
            if (total < 4) continue;
            var heard = here.Where(b => usedSet.Contains(b.Index)).Select(b => b.Time - b.Expected - typical).OrderBy(r => r).ToList();
            if (heard.Count < total * 0.3)
            {
                check.Columns[c] = TimingColumnFlag.NoBeat;
                continue;
            }
            double median = heard[heard.Count / 2];
            double scatter = heard.Select(r => Math.Abs(r - median)).OrderBy(r => r).ElementAt(heard.Count / 2) * 1.4826;
            double standardError = Math.Max(scatter, 0.5) / Math.Sqrt(heard.Count);
            if (scatter > 10) check.Columns[c] = TimingColumnFlag.Unsteady;
            else if (Math.Abs(median) > Math.Max(3.0, 3 * standardError))
                check.Columns[c] = median < 0 ? TimingColumnFlag.Earlier : TimingColumnFlag.Later;
        }
        return check;
    }

    // ---------------------------------------------------------------- FFT

    /// <summary>In-place radix-2 complex FFT with precomputed twiddle factors.</summary>
    private sealed class Fft
    {
        private readonly int size;
        private readonly float[] cos, sin;
        private readonly int[] reversed;

        public Fft(int size)
        {
            this.size = size;
            cos = new float[size / 2];
            sin = new float[size / 2];
            for (int i = 0; i < size / 2; i++)
            {
                cos[i] = (float)Math.Cos(2 * Math.PI * i / size);
                sin[i] = (float)Math.Sin(2 * Math.PI * i / size);
            }
            reversed = new int[size];
            int bits = System.Numerics.BitOperations.Log2((uint)size);
            for (int i = 0; i < size; i++)
            {
                int r = 0;
                for (int b = 0; b < bits; b++) r |= ((i >> b) & 1) << (bits - 1 - b);
                reversed[i] = r;
            }
        }

        public void Forward(float[] re, float[] im) => Transform(re, im, -1);

        public void Inverse(float[] re, float[] im)
        {
            Transform(re, im, 1);
            float scale = 1f / size;
            for (int i = 0; i < size; i++)
            {
                re[i] *= scale;
                im[i] *= scale;
            }
        }

        private void Transform(float[] re, float[] im, int sign)
        {
            for (int i = 0; i < size; i++)
            {
                int j = reversed[i];
                if (j > i)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }
            for (int length = 2; length <= size; length <<= 1)
            {
                int halfLength = length / 2, stride = size / length;
                for (int start = 0; start < size; start += length)
                    for (int j = 0; j < halfLength; j++)
                    {
                        float wr = cos[j * stride], wi = sign * sin[j * stride];
                        int a = start + j, b = a + halfLength;
                        float tr = re[b] * wr - im[b] * wi, ti = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - tr;
                        im[b] = im[a] - ti;
                        re[a] += tr;
                        im[a] += ti;
                    }
            }
        }
    }
}
