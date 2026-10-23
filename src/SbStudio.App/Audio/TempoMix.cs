using NAudio.Wave;

namespace SbStudio.App.Audio;

/// <summary>Stretches map audio, with Timing clicks either in the same track or added afterwards at original speed.</summary>
internal sealed class TempoMix : ISampleProvider, IDisposable
{
    private readonly AudioEngine.Mixer map;
    private AudioEngine.Mixer? output;
    private BassTempo? tempo;
    private TimingTempo? timingTempo;
    private readonly double rate;
    private readonly List<(ScheduledSample Sample, float[] Data, double From)> clicks = new();
    private bool hasMapAudio;
    private readonly bool hasMusic;
    private readonly bool timingPrecision;
    private readonly bool liveRefresh;
    private readonly bool timingSoundFollowsTempo;
    private Action<long>[] activations = Array.Empty<Action<long>>();
    private long[] markerFrames = Array.Empty<long>();
    private double[] markerTimes = Array.Empty<double>();
    private Update? pending;
    private sealed record Update(AudioEngine.Mixer.Table Map, AudioEngine.Mixer.Table Output, Action<long>[] Activations);
    private float clickGain = 1;
    private int mutedLanes;
    private float volume = 1, hitSoundVolume = 1;

    public TempoMix(ISampleProvider? music, long delayFrames, double rate, bool timingPrecision = false, bool liveRefresh = true,
        bool timingSoundFollowsTempo = false)
    {
        this.rate = rate;
        this.timingPrecision = timingPrecision;
        this.liveRefresh = liveRefresh;
        this.timingSoundFollowsTempo = timingSoundFollowsTempo;
        map = new AudioEngine.Mixer(music, delayFrames, liveRefresh: liveRefresh);
        hasMapAudio = hasMusic = music != null;
    }

    internal Action<float[], int, int>? BeforeGain;
    public WaveFormat WaveFormat => map.WaveFormat;
    public float ClickGain { set { clickGain = value; map.ClickGain = value; if (output != null) output.ClickGain = value; } }
    public int MutedLanes { set { mutedLanes = value; map.MutedLanes = value; } }
    public float Volume { set { volume = value; if (output != null) output.Volume = value; } }
    public float MusicVolume { set => map.MusicVolume = value; }
    public float HitSoundVolume { set { hitSoundVolume = value; map.HitSoundVolume = value; if (output != null) output.HitSoundVolume = value; } }

    public void Schedule(ScheduledSample sample, float[] data, double fromMs)
    {
        if (OutputClick(sample)) clicks.Add((sample, data, fromMs));
        else
        {
            hasMapAudio |= data.Length > 0;
            Schedule(map, sample, data, Frame(sample.TimeMs - fromMs), 1);
        }
    }

    // BypassTempo identifies Timing clicks for their independent gain as well as their original routing.
    // When following music they keep that identity, but enter the same tempo processor as the map.
    private bool OutputClick(ScheduledSample sample) => sample.BypassTempo && !timingSoundFollowsTempo;

    private static long Frame(double ms) => (long)Math.Round(ms * AudioEngine.SampleRate / 1000);

    private static void Schedule(AudioEngine.Mixer target, ScheduledSample sample, float[] data, long startFrame, double rate) =>
        target.Schedule(startFrame, data, sample.Volume, sample.HitSound,
            sample.DurationMs > 0 ? Frame(sample.DurationMs / rate) : 0, sample.Loop, sample.LaneMask, sample.BypassTempo);

    public void Prepare(bool prime = false)
    {
        if (output != null) return;
        ISampleProvider music = map;
        var future = clicks.Where(c => c.Sample.TimeMs >= c.From).OrderBy(c => c.Sample.TimeMs - c.From).ToArray();
        activations = new Action<long>[future.Length];
        markerFrames = future.Select(c => Frame(c.Sample.TimeMs - c.From)).ToArray();
        markerTimes = future.Select(c => c.Sample.TimeMs).ToArray();
        if (Math.Abs(rate - 1) > 1e-6)
        {
            // Map audio always carries its source position, so the playback clock follows what is heard;
            // the shorter Timing windows are only used while there are clicks to line up.
            if (hasMapAudio)
                music = timingTempo = new TimingTempo(map, rate,
                    markerFrames,
                    (index, frame) => activations[index](frame),
                    timingPrecision: timingPrecision || future.Length > 0);
            else music = tempo = new BassTempo(map, rate);
        }
        var prepared = prime ? new PrimedSource(music, 512) : null;
        output = new AudioEngine.Mixer(prepared ?? music, 0, liveRefresh: liveRefresh) { Volume = volume, HitSoundVolume = hitSoundVolume, ClickGain = clickGain };
        if (timingTempo != null)
        {
            output.ReserveVoices(future.Length);
            for (int i = 0; i < future.Length; i++)
            {
                var click = future[i];
                if (liveRefresh)
                {
                    var voice = output.AddVoice(Spec(click.Sample, click.Data, click.From, rate, triggered: true));
                    activations[i] = frame => output.Activate(voice, frame);
                }
                else activations[i] = output.PrepareVoice(click.Data, click.Sample.Volume, click.Sample.HitSound,
                    click.Sample.DurationMs > 0 ? Frame(click.Sample.DurationMs / rate) : 0, click.Sample.Loop);
            }
        }
        // Already-started tails, 1x playback, and clicks without map audio need no provenance pass.
        foreach (var click in clicks)
            if (timingTempo == null || click.Sample.TimeMs < click.From)
                Schedule(output, click.Sample, click.Data, Frame((click.Sample.TimeMs - click.From) / rate), rate);
        output.BeforeGain = (data, offset, count) => BeforeGain?.Invoke(data, offset, count);
        map.Seal();
        output.Seal();
        prepared?.Prime();
    }

    private static AudioEngine.Mixer.VoiceSpec Spec(ScheduledSample sample, float[] data, double fromMs, double scale, bool triggered = false) =>
        new(Frame((sample.TimeMs - fromMs) / scale), data, sample.Volume, sample.HitSound,
            sample.DurationMs > 0 ? Frame(sample.DurationMs / scale) : 0, sample.Loop, sample.LaneMask, sample.BypassTempo, triggered);

    internal bool TryUpdateSamples(IReadOnlyList<(ScheduledSample Sample, float[] Data)> samples, double fromMs)
    {
        if (!liveRefresh || output == null) return false;
        var future = samples.Where(c => OutputClick(c.Sample) && c.Sample.TimeMs >= fromMs).OrderBy(c => c.Sample.TimeMs).ToArray();
        if (Math.Abs(rate - 1) > 1e-6)
        {
            bool mapAudio = hasMusic || samples.Any(c => !OutputClick(c.Sample) && c.Data.Length > 0);
            if (mapAudio != hasMapAudio) return false;
            if (timingTempo != null && !markerTimes.SequenceEqual(future.Select(c => c.Sample.TimeMs))) return false;
        }
        var mapTable = map.BuildTable(samples.Where(c => !OutputClick(c.Sample)).Select(c => Spec(c.Sample, c.Data, fromMs, 1)));
        var outputSpecs = timingTempo != null
            ? future.Select(c => Spec(c.Sample, c.Data, fromMs, rate, triggered: true))
                .Concat(samples.Where(c => OutputClick(c.Sample) && c.Sample.TimeMs < fromMs).Select(c => Spec(c.Sample, c.Data, fromMs, rate)))
            : samples.Where(c => OutputClick(c.Sample)).Select(c => Spec(c.Sample, c.Data, fromMs, rate));
        var outputTable = output.BuildTable(outputSpecs);
        var nextActivations = new Action<long>[timingTempo != null ? future.Length : 0];
        for (int i = 0; i < nextActivations.Length; i++)
        {
            var voice = outputTable.Desired[i];
            nextActivations[i] = frame => output.Activate(voice, frame);
        }
        Interlocked.Exchange(ref pending, new Update(mapTable, outputTable, nextActivations));
        return true;
    }

    /// <summary>
    /// The map frame (counted from the playback start) heard at an output frame. Null when the output is
    /// not stretched, or not tracked yet: then output frames are map frames at the nominal rate.
    /// </summary>
    internal double? MapFrameAt(double outputFrame) => timingTempo?.SourceFrameAt(outputFrame);
    internal long NewestTrackedFrame => timingTempo?.NewestFrame ?? -1;

    public int Read(float[] buffer, int offset, int count)
    {
        Prepare();
        if (Interlocked.Exchange(ref pending, null) is { } update)
        {
            activations = update.Activations;
            map.Publish(update.Map);
            output!.Publish(update.Output);
        }
        return output!.Read(buffer, offset, count);
    }

    public void Dispose() { timingTempo?.Dispose(); tempo?.Dispose(); }
}
