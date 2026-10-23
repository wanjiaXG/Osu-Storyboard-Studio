namespace SbStudio.App.Audio;

public readonly record struct WaveformRange(float Minimum, float Maximum, bool HasSamples);

/// <summary>Signed, independent PCM channel ranges with a reduction pyramid for pixel-sized queries.</summary>
public sealed class WaveformDetail
{
    private sealed record Level(float[] Minimum, float[] Maximum, long Stride);
    private readonly Level[][] channels;
    public int SampleRate { get; }
    public int ChannelCount => channels.Length;
    public int FramesPerBucket { get; }
    public long FrameCount { get; }

    internal WaveformDetail(int sampleRate, int framesPerBucket, long frames, float[][] minimum, float[][] maximum)
    {
        SampleRate = sampleRate;
        FramesPerBucket = framesPerBucket;
        FrameCount = frames;
        channels = new Level[minimum.Length][];
        for (int channel = 0; channel < channels.Length; channel++)
        {
            var levels = new List<Level> { new(minimum[channel], maximum[channel], 1) };
            while (levels[^1].Minimum.Length > 1)
            {
                var source = levels[^1];
                int count = 1 + (source.Minimum.Length - 1) / 4;
                var min = new float[count];
                var max = new float[count];
                for (int bucket = 0; bucket < count; bucket++)
                {
                    min[bucket] = float.PositiveInfinity;
                    max[bucket] = float.NegativeInfinity;
                    for (int i = bucket * 4; i < Math.Min(source.Minimum.Length, bucket * 4 + 4); i++)
                    {
                        min[bucket] = Math.Min(min[bucket], source.Minimum[i]);
                        max[bucket] = Math.Max(max[bucket], source.Maximum[i]);
                    }
                }
                levels.Add(new(min, max, source.Stride * 4));
            }
            channels[channel] = levels.ToArray();
        }
    }

    public WaveformRange RangeForDisplay(int channel, double fromMs, double toMs)
    {
        if (!double.IsFinite(fromMs) || !double.IsFinite(toMs) || toMs <= fromMs) return default;
        long from = (long)Math.Clamp(Math.Floor((fromMs + Waveform.VisualOffsetMs) * SampleRate / 1000), 0, FrameCount);
        long to = (long)Math.Clamp(Math.Ceiling((toMs + Waveform.VisualOffsetMs) * SampleRate / 1000), 0, FrameCount);
        return RangeFrames(channel, from, to);
    }

    public WaveformRange RangeFrames(int channel, long from, long to)
    {
        if ((uint)channel >= channels.Length || to <= from || to <= 0 || from >= FrameCount) return default;
        var levels = channels[channel];
        if (levels[0].Minimum.Length == 0) return default;
        long cursor = Math.Max(0, from) / FramesPerBucket;
        long end = Math.Min(FrameCount, to);
        long until = Math.Min(levels[0].Minimum.Length, 1 + (end - 1) / FramesPerBucket);
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        while (cursor < until)
        {
            int selected = levels.Length - 1;
            while (selected > 0 && (cursor % levels[selected].Stride != 0 || cursor + levels[selected].Stride > until)) selected--;
            var level = levels[selected];
            int index = (int)(cursor / level.Stride);
            min = Math.Min(min, level.Minimum[index]);
            max = Math.Max(max, level.Maximum[index]);
            cursor += level.Stride;
        }
        return new(min, max, true);
    }
}

internal sealed class WaveformDetailBuilder
{
    private readonly int sampleRate, sourceChannels, framesPerBucket;
    private readonly List<float>[] minimum, maximum;
    private readonly float[] min, max;
    private int framesInBucket;
    private long frames;

    public WaveformDetailBuilder(int sampleRate, int channels)
    {
        if (sampleRate <= 0 || channels <= 0) throw new ArgumentOutOfRangeException();
        this.sampleRate = sampleRate;
        sourceChannels = channels;
        // At the maximum timeline zoom (5 px/ms), retained buckets remain less than one pixel wide.
        framesPerBucket = Math.Max(1, sampleRate / 5000);
        int shownChannels = Math.Min(2, channels);
        minimum = Enumerable.Range(0, shownChannels).Select(_ => new List<float>()).ToArray();
        maximum = Enumerable.Range(0, shownChannels).Select(_ => new List<float>()).ToArray();
        min = Enumerable.Repeat(float.PositiveInfinity, shownChannels).ToArray();
        max = Enumerable.Repeat(float.NegativeInfinity, shownChannels).ToArray();
    }

    public void AddSamples(ReadOnlySpan<float> samples)
    {
        for (int i = 0; i + sourceChannels <= samples.Length; i += sourceChannels)
        {
            for (int channel = 0; channel < min.Length; channel++)
            {
                float value = samples[i + channel];
                if (!float.IsFinite(value)) value = 0;
                min[channel] = Math.Min(min[channel], value);
                max[channel] = Math.Max(max[channel], value);
            }
            frames++;
            if (++framesInBucket >= framesPerBucket) Flush();
        }
    }

    private void Flush()
    {
        for (int channel = 0; channel < min.Length; channel++)
        {
            minimum[channel].Add(min[channel]);
            maximum[channel].Add(max[channel]);
            min[channel] = float.PositiveInfinity;
            max[channel] = float.NegativeInfinity;
        }
        framesInBucket = 0;
    }

    public WaveformDetail Complete()
    {
        if (framesInBucket > 0) Flush();
        return new(sampleRate, framesPerBucket, frames,
            minimum.Select(c => c.ToArray()).ToArray(), maximum.Select(c => c.ToArray()).ToArray());
    }
}
