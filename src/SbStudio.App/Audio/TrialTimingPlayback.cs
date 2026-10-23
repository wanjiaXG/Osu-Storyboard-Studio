using ManagedBass;
using NAudio.Wave;
using WaveFormat = NAudio.Wave.WaveFormat;

namespace SbStudio.App.Audio;

/// <summary>Frame-driven music clock and independent sample playback for Timing slowdown.</summary>
internal sealed class TrialTimingPlayback : IDisposable
{
    private readonly Dictionary<string, int> sounds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float[]> soundData = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<int> retiredSounds = new();
    private readonly List<(int Channel, float Gain)> active = new();
    private ScheduledSample[] samples = Array.Empty<ScheduledSample>();
    private readonly double rate;
    private readonly bool followTempo;
    private readonly int device;
    private int next;
    private double previousWall, audioTime;
    internal double Time { get; private set; }
    internal int TriggerCount { get; private set; }
    internal double LastTriggerTime { get; private set; } = double.NaN;
    internal float Gain { get; set; } = 1;

    internal TrialTimingPlayback(double fromMs, double rate, bool followTempo, int device)
    {
        Time = audioTime = fromMs;
        this.rate = rate;
        this.followTempo = followTempo;
        this.device = device;
    }

    internal void Schedule(IReadOnlyList<(ScheduledSample Sample, float[] Data)> loaded, double fromMs)
    {
        foreach (var (sample, data) in loaded)
        {
            if (!sample.BypassTempo || data.Length == 0
                || soundData.TryGetValue(sample.File, out var previous) && ReferenceEquals(previous, data)) continue;
            float[] pcm = followTempo ? Stretch(data, rate) : data;
            BassRuntime.PreparePlayback(device);
            int sound = Bass.CreateSample(pcm.Length * sizeof(float), AudioEngine.SampleRate, 2, 128, BassFlags.Float);
            if (sound == 0) throw BassRuntime.Failure();
            if (!Bass.SampleSetData(sound, pcm))
            {
                Bass.SampleFree(sound);
                throw BassRuntime.Failure();
            }
            if (sounds.TryGetValue(sample.File, out int old)) retiredSounds.Add(old);
            sounds[sample.File] = sound;
            soundData[sample.File] = data;
        }
        samples = loaded.Select(s => s.Sample).Where(s => s.BypassTempo).OrderBy(s => s.TimeMs).ToArray();
        next = 0;
        while (next < samples.Length && samples[next].TimeMs < fromMs) next++;
    }

    // Interpolate the music source position before applying the integer clock and slow-rate correction.
    // Called once per editor frame; merely reading CurrentTimeMs never advances or triggers anything.
    internal void UpdateClock(double sourceMs, double wallMs)
    {
        double increase = (wallMs - previousWall) * rate;
        previousWall = wallMs;
        if (audioTime == 0 || sourceMs == 0)
        {
            audioTime = sourceMs;
        }
        else
        {
            double proposed = audioTime + increase;
            proposed -= (proposed - sourceMs) / 8;
            double difference = proposed - sourceMs;
            if (Math.Abs(difference) > 22)
            {
                audioTime = sourceMs;
            }
            else if (difference < -11)
            {
                audioTime += increase * 2;
            }
            else if (difference < 11) audioTime = proposed;
            else
            {
                audioTime += increase / 2;
            }
        }
        Time = Math.Round(audioTime) + (int)((1 - rate) / 0.75 * 5);
    }

    // A single beat crossing sounds once; a stalled frame does not replay a backlog.
    internal ScheduledSample? CrossedSample(double time)
    {
        int first = next;
        while (next < samples.Length && samples[next].TimeMs <= time) next++;
        return next == first + 1 ? samples[first] : null;
    }

    internal void UpdateSamples()
    {
        for (int i = retiredSounds.Count - 1; i >= 0; i--)
            if (!Bass.SampleGetChannels(retiredSounds[i]).Any(c => Bass.ChannelIsActive(c) == ManagedBass.PlaybackState.Playing))
            {
                Bass.SampleFree(retiredSounds[i]);
                retiredSounds.RemoveAt(i);
            }
        for (int i = active.Count - 1; i >= 0; i--)
        {
            var voice = active[i];
            if (Bass.ChannelIsActive(voice.Channel) != ManagedBass.PlaybackState.Playing) active.RemoveAt(i);
            else if (!Bass.ChannelSetAttribute(voice.Channel, ChannelAttribute.Volume, voice.Gain * Gain)) throw BassRuntime.Failure();
        }
        if (CrossedSample(Time) is not { } sample || !sounds.TryGetValue(sample.File, out int sound)) return;
        int channel = Bass.SampleGetChannel(sound, false);
        if (channel == 0 || !Bass.ChannelSetAttribute(channel, ChannelAttribute.Volume, sample.Volume * Gain)
            || !Bass.ChannelPlay(channel, true)) throw BassRuntime.Failure();
        active.Add((channel, sample.Volume));
        TriggerCount++;
        LastTriggerTime = Time;
    }

    private static float[] Stretch(float[] data, double rate)
    {
        using var tempo = new BassTempo(new Pcm(data), rate);
        var result = new List<float>();
        var buffer = new float[4096];
        int count;
        while ((count = tempo.Read(buffer, 0, buffer.Length)) > 0) result.AddRange(buffer.AsSpan(0, count).ToArray());
        return result.ToArray();
    }

    private sealed class Pcm(float[] data) : ISampleProvider
    {
        private int position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(AudioEngine.SampleRate, 2);
        public int Read(float[] buffer, int offset, int count)
        {
            int take = Math.Min(count, data.Length - position);
            Array.Copy(data, position, buffer, offset, take);
            position += take;
            return take;
        }
    }

    public void Dispose()
    {
        foreach (int sample in sounds.Values) Bass.SampleFree(sample);
        foreach (int sample in retiredSounds) Bass.SampleFree(sample);
        sounds.Clear();
        soundData.Clear();
        retiredSounds.Clear();
        active.Clear();
    }
}
