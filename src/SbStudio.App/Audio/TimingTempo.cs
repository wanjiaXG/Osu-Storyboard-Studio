using NAudio.Wave;

namespace SbStudio.App.Audio;

/// <summary>
/// Carries inaudible provenance through the same time stretching as the music: which beats have been
/// passed, and which source frame each output frame came from. Pitch-preserving stretching does not keep
/// output frame k at source frame k × rate: it starts slightly ahead, and it plays short stretches of the
/// source at their own speed, jumping back (slower) or ahead (faster) between them. The playback clock
/// follows the furthest source frame heard so far. A beat's click starts as soon as any of the music after
/// the beat is heard, and the clock is moved up to the beat then, so the two cannot disagree.
/// </summary>
internal sealed class TimingTempo : ISampleProvider, IDisposable
{
    private const int Channels = 5;
    private const float MarkerStep = 1e-9f;
    private const float MarkerReference = 1e-6f;
    // About as small as the reference at ten minutes of audio, so it cannot steer the stretching either.
    private const float PositionStep = 1e-13f;
    // One output frame in every 2^PositionShift is remembered, for about the last twelve seconds of output.
    private const int PositionShift = 3, PositionSlots = 65536;
    private readonly BassTempo tempo;
    private readonly Action<int, long> reached;
    private readonly long[] beatFrames;
    private readonly double rate;
    private readonly long[] positionFrames = new long[PositionSlots];
    private readonly double[] positions = new double[PositionSlots];
    private float[] processed = new float[16384];
    private long outputFrame, newestPosition = -1;
    private double furthest = double.NegativeInfinity;
    private int nextBeat;

    public TimingTempo(ISampleProvider source, double rate, long[] beatFrames, Action<int, long> reached, bool timingPrecision = true)
    {
        WaveFormat = source.WaveFormat;
        this.rate = rate;
        this.reached = reached;
        this.beatFrames = beatFrames;
        Array.Fill(positionFrames, -1);
        tempo = new BassTempo(new MarkedSource(source, beatFrames), rate, timingPrecision);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int wanted = count / 2 * Channels;
        if (processed.Length < wanted) processed = new float[wanted];
        int frames = tempo.Read(processed, 0, wanted) / Channels;
        for (int i = 0; i < frames; i++)
        {
            int p = i * Channels;
            // The reference removes overlap gain/rounding from the marker channels. They are
            // stripped here and never reach speakers, export, or the actual click waveform.
            float reference = processed[p + 3];
            long frame = outputFrame + i;
            // Taken on every frame here: the editor only looks once per screen frame and would miss the
            // furthest point of a stretch played before a jump back.
            if (reference > 0) furthest = Math.Max(furthest, (double)processed[p + 4] / reference * (MarkerReference / PositionStep));
            // The current method uses the first trace of the beat's source audio.
            double marker = reference > 0 ? (double)processed[p + 2] / reference * (MarkerReference / MarkerStep) : 0;
            while (nextBeat < beatFrames.Length && (marker > nextBeat + Math.Max(0.02, nextBeat * 2e-7)
                || furthest >= beatFrames[nextBeat]))
            {
                furthest = Math.Max(furthest, beatFrames[nextBeat]);
                reached(nextBeat++, frame);
            }
            if ((frame & ((1 << PositionShift) - 1)) == 0 && furthest > double.NegativeInfinity) Remember(frame, furthest);
            buffer[offset + i * 2] = processed[p];
            buffer[offset + i * 2 + 1] = processed[p + 1];
        }
        outputFrame += frames;
        return frames * 2;
    }

    // Written on the audio thread and read on the editor thread: a slot is marked invalid while it changes.
    private void Remember(long frame, double position)
    {
        int slot = (int)((frame >> PositionShift) & (PositionSlots - 1));
        Volatile.Write(ref positionFrames[slot], -1);
        positions[slot] = position;
        Volatile.Write(ref positionFrames[slot], frame);
        Volatile.Write(ref newestPosition, frame);
    }

    private bool Recall(long frame, out double position)
    {
        int slot = (int)((frame >> PositionShift) & (PositionSlots - 1));
        position = 0;
        if (Volatile.Read(ref positionFrames[slot]) != frame) return false;
        position = positions[slot];
        return Volatile.Read(ref positionFrames[slot]) == frame;
    }

    /// <summary>
    /// The furthest source frame (counted from where playback started) heard by an output frame, or null
    /// when that part of the output has not been produced yet or is too long ago.
    /// </summary>
    internal double? SourceFrameAt(double outputFrame)
    {
        if (outputFrame < 0) return null;
        long before = (long)outputFrame >> PositionShift << PositionShift, after = before + (1 << PositionShift);
        if (!Recall(before, out double from))
        {
            // The clock may extrapolate a little past what has been produced; carry on from the newest frame.
            long newest = Volatile.Read(ref newestPosition);
            return newest >= 0 && newest < outputFrame && Recall(newest, out double last)
                ? last + (outputFrame - newest) * rate : null;
        }
        double fraction = outputFrame - before;
        // Between two remembered frames, interpolate; past the newest one, carry on at the nominal rate.
        return Recall(after, out double to) ? from + (to - from) * fraction / (after - before) : from + fraction * rate;
    }

    internal long NewestFrame => Volatile.Read(ref newestPosition);

    public void Dispose() => tempo.Dispose();

    private sealed class MarkedSource(ISampleProvider source, long[] beatFrames) : ISampleProvider
    {
        private float[] stereo = new float[16384];
        private long frame;
        private int beat;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, Channels);

        public int Read(float[] buffer, int offset, int count)
        {
            int wanted = count / Channels * 2;
            if (stereo.Length < wanted) stereo = new float[wanted];
            int frames = source.Read(stereo, 0, wanted) / 2;
            for (int i = 0; i < frames; i++, frame++)
            {
                while (beat < beatFrames.Length && beatFrames[beat] <= frame) beat++;
                int p = offset + i * Channels;
                buffer[p] = stereo[i * 2];
                buffer[p + 1] = stereo[i * 2 + 1];
                buffer[p + 2] = beat * MarkerStep;
                buffer[p + 3] = MarkerReference;
                buffer[p + 4] = frame * PositionStep;
            }
            return frames * Channels;
        }
    }
}
