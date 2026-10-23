using System.Diagnostics;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SbStudio.Core;
using SbStudio.Resources;

namespace SbStudio.App.Audio;

public readonly record struct ScheduledSample(double TimeMs, string File, float Volume, bool HitSound = false, double DurationMs = 0, bool Loop = false, bool BypassTempo = false, int LaneMask = 0);

/// <summary>Peak amplitude per <see cref="BucketMs"/> of the main audio, for the timeline.</summary>
public sealed class Waveform
{
    public const double BucketMs = 1;
    /// <summary>osu!lazer Editor.WAVEFORM_VISUAL_OFFSET: historical beatmap visual alignment.</summary>
    public const double VisualOffsetMs = 20;
    public float[] Peaks = Array.Empty<float>();
    public double DurationMs;
    public WaveformDetail? Detail { get; init; }

    /// <summary>Amplitude in a half-open editor time interval, using lazer's visual alignment.</summary>
    public float PeakForDisplay(double fromMs, double toMs)
    {
        if (toMs <= fromMs) return 0;
        int from = (int)Math.Clamp(Math.Floor((fromMs + VisualOffsetMs) / BucketMs), 0, Peaks.Length);
        int to = (int)Math.Clamp(Math.Ceiling((toMs + VisualOffsetMs) / BucketMs), 0, Peaks.Length);
        float peak = 0;
        for (int i = from; i < to; i++) peak = Math.Max(peak, Peaks[i]);
        return peak;
    }
}

/// <summary>
/// Plays the sound of the map in sync with the timeline, at any rate and from any (even negative) time: the
/// main audio and the storyboard samples as one track, as if they had been mixed together beforehand. While
/// playing, the sound card's position is the clock. Timing clicks are added after tempo processing at their
/// original sample speed, or stretched with the music when enabled in Timing check.
/// </summary>
public sealed partial class AudioEngine : IDisposable
{
    public const int SampleRate = 44100;

    private readonly Dictionary<string, float[]?> sampleCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Ticks, long Size)> sampleStamps = new(StringComparer.OrdinalIgnoreCase);
    public bool SeamlessRefresh { get; set; } = true;
    private string? playingMusic, playingDevice;
    private bool playingTimingMode;
    private double playingRate;
    public int LiveUpdates { get; private set; }
    public int Rebuilds { get; private set; }
    /// <summary>How many times a sample file's date and size have been looked up.</summary>
    internal int SampleStampChecks { get; private set; }
    private float clickGain = 1;
    private int mutedLanes;
    public float ClickGain { get => clickGain; set { clickGain = value; if (mixer != null) mixer.ClickGain = value; } }
    public int MutedLanes { get => mutedLanes; set { mutedLanes = value; if (mixer != null) mixer.MutedLanes = value; } }
    private readonly Stopwatch stopwatch = new();

    private string? musicFile;
    private int musicGeneration;
    private BassOutput? output;
    private WaveStream? reader;
    private TempoMix? mixer;
    private TrialTimingPlayback? trialTiming;
    internal bool PlayingTrialTiming => trialTiming != null;
    internal int TrialTimingTriggers => trialTiming?.TriggerCount ?? 0;
    internal double? TrialTimingLastTrigger => trialTiming is { TriggerCount: > 0 } trial ? trial.LastTriggerTime : null;
    private double startMs, rate = 1;
    private long lastBytes;
    private double lastBytesAt, lastReported;
    private float volume = 0.7f;
    private float hitSoundVolume = 0.7f;
    private float musicVolume = 1f;
    private long devicePollAt;
    private ScheduledSample[] playingSamples = Array.Empty<ScheduledSample>();
    internal int ScheduledHitSoundCount => playingSamples.Count(s => s.HitSound);
    /// <summary>Played frames (native source frames in the trial method), and produced marker frames (-1 untracked).</summary>
    internal string ClockState => $"{output?.Position / (2 * sizeof(float)) ?? -1},{mixer?.NewestTrackedFrame ?? -1}";
    internal double[] ScheduledClickTimes => playingSamples.Where(s => s.BypassTempo).Select(s => s.TimeMs).ToArray();
    internal int ScheduledStoryboardSampleCount => playingSamples.Count(s => !s.HitSound);
    internal double? OutputBufferMilliseconds => output?.BufferMilliseconds;
    internal double? DeviceLatencyMilliseconds => output?.DeviceLatencyMilliseconds;
    internal double? PeakAudioCallbackMilliseconds => output?.PeakCallbackMilliseconds;
    private BassOutput? auditionOutput;
    private Mixer? auditionMixer;
    private long auditionUntil;
    internal int AuditionCount { get; private set; }
    internal ScheduledSample[] LastAudition { get; private set; } = Array.Empty<ScheduledSample>();
    internal bool IsAuditioning => auditionOutput?.IsActive == true;
    public IReadOnlyList<AudioDevice> OutputDevices { get; private set; } = Array.Empty<AudioDevice>();
    public string OutputDeviceId { get; private set; } = "";

    public float HitSoundVolume
    {
        get => hitSoundVolume;
        set
        {
            hitSoundVolume = Math.Clamp(value, 0, 1);
            if (mixer != null) mixer.HitSoundVolume = hitSoundVolume;
            if (auditionMixer != null) auditionMixer.HitSoundVolume = hitSoundVolume;
        }
    }

    public float MusicVolume
    {
        get => musicVolume;
        set
        {
            musicVolume = Math.Clamp(value, 0, 1);
            if (mixer != null) mixer.MusicVolume = musicVolume;
        }
    }

    public void SetOutputDevice(string? id)
    {
        OutputDeviceId = id ?? "";
        UpdateDevices(true);
    }

    public void UpdateDevices(bool force = false)
    {
        if (auditionOutput != null && Environment.TickCount64 >= auditionUntil) StopAudition();
        if (!force && Environment.TickCount64 < devicePollAt) return;
        devicePollAt = Environment.TickCount64 + 1000;
        try
        {
            OutputDevices = BassRuntime.Devices();
            var selected = BassRuntime.ResolveDevice(OutputDevices, OutputDeviceId);
            if (OutputDeviceId.Length > 0 && selected?.Id != OutputDeviceId) OutputDeviceId = "";
            if (auditionOutput != null)
            {
                try { if (selected != null) auditionOutput.SwitchDevice(selected.Index); else StopAudition(); }
                catch { StopAudition(); }
            }
            if (output != null)
            {
                double time = CurrentTimeMs;
                if (selected == null || !output.IsActive || output.Error != null)
                {
                    Play(time, rate, playingSamples);
                }
                else
                {
                    try
                    {
                        if (trialTiming != null && output.Device != selected.Index) Play(time, rate, playingSamples);
                        else output.SwitchDevice(selected.Index);
                    }
                    catch
                    {
                        // A disconnected endpoint may have destroyed its stream. Rebuild the
                        // track at the current time on the default endpoint without snapping.
                        OutputDeviceId = "";
                        Play(time, rate, playingSamples);
                    }
                }
            }
            else if (output == null && IsPlaying && selected != null)
            {
                // Recover playback if there were no speakers when it started.
                double time = CurrentTimeMs;
                Play(time, rate, playingSamples);
            }
        }
        catch (Exception ex) { Error = L.T("audio.playFailed", ex.Message); }
    }

    public double DurationMs { get; private set; }
    public Waveform? Waveform { get; private set; }
    public bool IsPlaying { get; private set; }
    public string? Error { get; private set; }
    public bool HasMusic => musicFile != null;
    /// <summary>The file of the main audio; null when there is none.</summary>
    public string? MusicFile => musicFile;

    public float Volume
    {
        get => volume;
        set
        {
            volume = Math.Clamp(value, 0, 1);
            if (mixer != null) mixer.Volume = volume;
            if (auditionMixer != null) auditionMixer.Volume = volume;
        }
    }

    public void SetMusic(string? file)
    {
        Stop();
        StopAudition();
        musicFile = null;
        DurationMs = 0;
        Waveform = null;
        Error = null;
        int generation = Interlocked.Increment(ref musicGeneration);
        ResetTimingMusic();
        if (file == null || !File.Exists(file)) return;

        try
        {
            using var probe = Open(file);
            DurationMs = probe.TotalTime.TotalMilliseconds;
            musicFile = file;
            PrepareTimingMusic();
        }
        catch (Exception ex)
        {
            Error = L.T("audio.readFailed", ex.Message);
            return;
        }

        Task.Run(() =>
        {
            try
            {
                var waveform = BuildWaveform(file, () => generation != Volatile.Read(ref musicGeneration));
                if (waveform != null && generation == Volatile.Read(ref musicGeneration))
                {
                    DurationMs = waveform.DurationMs;
                    Waveform = waveform;
                }
            }
            catch (Exception)
            {
                // The waveform is decoration; playback does not depend on it.
            }
        });
    }

    public void ClearSampleCache() { sampleCache.Clear(); sampleStamps.Clear(); StopAudition(); }

    /// <summary>Preview sample attacks on a separate stream, without changing the map's playback clock.</summary>
    public void Audition(IEnumerable<ScheduledSample> samples)
    {
        LastAudition = samples.ToArray();
        AuditionCount++;
        StopAudition();
        try
        {
            auditionMixer = new Mixer(null, 0, liveRefresh: false) { Volume = volume, HitSoundVolume = hitSoundVolume };
            double length = 0;
            foreach (var sample in LastAudition)
            {
                var data = LoadSample(sample.File);
                if (data == null || data.Length == 0) continue;
                auditionMixer.Schedule(0, data, sample.Volume, hitSound: true);
                length = Math.Max(length, data.Length * 1000.0 / (2 * SampleRate));
            }
            if (length <= 0) { auditionMixer = null; return; }
            UpdateDevices(true);
            var device = BassRuntime.ResolveDevice(OutputDevices, OutputDeviceId);
            auditionMixer.Seal();
            auditionOutput = new BassOutput(auditionMixer, device: device?.Index ?? -1);
            auditionUntil = Environment.TickCount64 + (long)Math.Ceiling(length) + 300;
            auditionOutput.Play();
        }
        catch (Exception ex) { StopAudition(); Error = L.T("audio.playFailed", ex.Message); }
    }

    internal void StopAudition()
    {
        try { auditionOutput?.Dispose(); } catch { /* Device may already have disconnected. */ }
        auditionOutput = null;
        auditionMixer = null;
    }

    public void Play(double fromMs, double playbackRate, IEnumerable<ScheduledSample> samples)
    {
        playingSamples = samples.ToArray();
        Rebuilds++;
        Stop();
        playingMusic = musicFile;
        playingDevice = OutputDeviceId;
        playingTimingMode = timingMode;
        playingTimingSoundFollowsTempo = timingSoundFollowsTempo;
        playingClockTimingSlowdown = clockTimingSlowdown && playbackRate < 1;
        playingRate = playbackRate;
        startMs = fromMs;
        rate = Math.Clamp(playbackRate, PlaybackRate.Min, PlaybackRate.Max);
        lastBytes = 0;
        lastBytesAt = 0;
        lastReported = fromMs;

        try
        {
            ISampleProvider? music = null;
            UsingTimingMusicCache = false;
            if (musicFile != null && fromMs < DurationMs)
            {
                if (timingMode || playingSamples.Any(s => s.BypassTempo))
                {
                    PrepareTimingMusic(force: true);
                    if (timingMusic?.Data is { } cached)
                    {
                        music = cached.At(fromMs);
                        UsingTimingMusicCache = true;
                    }
                }
                if (music == null)
                {
                    reader = Open(musicFile);
                    reader.CurrentTime = TimeSpan.FromMilliseconds(Math.Max(0, fromMs));
                    music = Normalize(reader.ToSampleProvider());
                }
            }

            // Positions on the track, in frames from where playback starts. A sample that began earlier
            // gets a negative one and is heard from wherever it has got to.
            long delayFrames = fromMs < 0 ? FramesOf(-fromMs) : 0;
            mixer = new TempoMix(music, delayFrames, playingClockTimingSlowdown ? 1 : rate,
                timingPrecision: SeamlessRefresh && timingMode, liveRefresh: SeamlessRefresh,
                timingSoundFollowsTempo: timingSoundFollowsTempo)
                { Volume = volume, HitSoundVolume = hitSoundVolume, MusicVolume = musicVolume, ClickGain = clickGain, MutedLanes = mutedLanes };
            var loaded = new List<(ScheduledSample Sample, float[] Data)>();
            var checkedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in playingSamples)
            {
                var data = LoadSample(s.File, checkedFiles);
                if (data == null) continue;
                loaded.Add((s, data));
                if (!playingClockTimingSlowdown || !s.BypassTempo) mixer.Schedule(s, data, fromMs);
            }
            // Pay native initialization, decoder/resampler warm-up and the first stretch before Play.
            // The prefetched first block is replayed at sample zero; it adds no silence or timing shift.
            mixer.Prepare(prime: timingMode || playingSamples.Any(s => s.BypassTempo));

            OutputDevices = BassRuntime.Devices();
            var selected = BassRuntime.ResolveDevice(OutputDevices, OutputDeviceId);
            if (OutputDeviceId.Length > 0 && selected?.Id != OutputDeviceId) OutputDeviceId = "";
            try
            {
                output = new BassOutput(mixer, rate: playingClockTimingSlowdown ? rate : 1,
                    device: selected?.Index ?? -1, nativeTempo: playingClockTimingSlowdown);
                if (playingClockTimingSlowdown)
                {
                    trialTiming = new TrialTimingPlayback(fromMs, rate, timingSoundFollowsTempo, output.Device);
                    trialTiming.Schedule(loaded, fromMs);
                }
                AudioTap.Stream(output.StreamId, new { StartMs = startMs, Rate = rate, MusicFile = musicFile,
                    Samples = playingSamples, HitSoundVolume = hitSoundVolume, MusicVolume = musicVolume,
                    TimingMode = timingMode, UsingTimingMusicCache, SeamlessRefresh, MutedLanes = mutedLanes, ClickGain = clickGain });
                output.Play();
            }
            catch when (OutputDeviceId.Length > 0)
            {
                OutputDeviceId = "";
                DisposeOutput();
                // A failed output may already have consumed a buffer. Reopen from the
                // requested time so falling back does not skip music or sample attacks.
                Play(fromMs, rate, playingSamples);
                return;
            }
            Error = null;
        }
        catch (Exception ex)
        {
            // No device, or an unreadable file: keep time running on the stopwatch alone.
            Error = L.T("audio.playFailed", ex.Message);
            DisposeOutput();
        }

        stopwatch.Restart();
        IsPlaying = true;
    }

    public bool TryUpdateSamples(IEnumerable<ScheduledSample> samples, double playbackRate)
    {
        if (!SeamlessRefresh || !IsPlaying || output == null || !output.IsActive || output.Error != null || Error != null || mixer == null
            || playingMusic != musicFile || playingRate != playbackRate || playingDevice != OutputDeviceId || playingTimingMode != timingMode
            || playingTimingSoundFollowsTempo != timingSoundFollowsTempo
            || playingClockTimingSlowdown != (clockTimingSlowdown && playbackRate < 1)) return false;
        var next = samples.ToArray();
        var loaded = new List<(ScheduledSample, float[])>(next.Length);
        var checkedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sample in next)
            if (LoadSample(sample.File, checkedFiles) is { } data) loaded.Add((sample, data));
        if (!mixer.TryUpdateSamples(trialTiming != null ? loaded.Where(s => !s.Item1.BypassTempo).ToArray() : loaded, startMs)) return false;
        if (trialTiming != null)
        {
            try { trialTiming.Schedule(loaded, CurrentTimeMs + 0.000001); }
            catch (Exception ex) { Error = L.T("audio.playFailed", ex.Message); return false; }
        }
        playingSamples = next;
        LiveUpdates++;
        return true;
    }

    public void Stop()
    {
        if (IsPlaying) startMs = CurrentTimeMs;
        IsPlaying = false;
        stopwatch.Stop();
        DisposeOutput();
    }

    private void DisposeOutput()
    {
        try
        {
            output?.Dispose();
        }
        catch (Exception)
        {
            // Device vanished while playing; nothing left to release.
        }
        output = null;
        trialTiming?.Dispose();
        trialTiming = null;
        UsingTimingMusicCache = false;
        mixer?.Dispose();
        mixer = null;
        reader?.Dispose();
        reader = null;
    }

    public double CurrentTimeMs
    {
        get
        {
            if (!IsPlaying) return startMs;
            if (trialTiming != null) return trialTiming.Time;

            double wall = stopwatch.Elapsed.TotalSeconds;
            double time = startMs + wall * 1000 * rate;
            if (output != null)
            {
                // The device reports in steps; the stopwatch fills in between them.
                if (output.Error != null) Error = L.T("audio.playFailed", output.Error.Message);
                long bytes = output.Position;
                if (bytes != lastBytes)
                {
                    lastBytes = bytes;
                    lastBytesAt = wall;
                }
                double played = bytes / (double)(2 * sizeof(float)) + Math.Min(0.05, wall - lastBytesAt) * SampleRate;
                // Stretched audio is not at the nominal position: follow the source frame actually being heard.
                time = mixer?.MapFrameAt(played) is { } mapFrame
                    ? startMs + mapFrame * 1000 / SampleRate
                    : startMs + played * 1000 / SampleRate * rate;
            }

            lastReported = Math.Max(lastReported, time);
            return lastReported;
        }
    }

    private static long FramesOf(double ms, int sampleRate = SampleRate) => (long)Math.Round(ms * sampleRate / 1000);

    /// <summary>
    /// The sound of the map from a time on as one stereo track, for writing into a file: the music and the
    /// samples mixed exactly as playback mixes them, at their own speed and at full volume. It has the
    /// sample rate it was asked for, and nothing to do with what is being played at the moment.
    /// </summary>
    public sealed class Mixdown : IDisposable
    {
        private readonly WaveStream? reader;
        private readonly Mixer mixer;
        private readonly IDisposable? resampler;

        internal Mixdown(WaveStream? reader, Mixer mixer, IDisposable? resampler)
        {
            this.reader = reader;
            this.mixer = mixer;
            this.resampler = resampler;
        }

        /// <summary>Fills a buffer with the next stretch of the track: left and right sample by turns. It never runs out; past the end there is silence.</summary>
        public void Read(float[] buffer, int frames) => mixer.Read(buffer, 0, frames * 2);

        public void Dispose()
        {
            reader?.Dispose();
            resampler?.Dispose();
        }
    }

    /// <summary>
    /// A source without its beginning. Read and discard on the consuming thread so export starts at
    /// an exact output sample even when resampling from another rate.
    /// </summary>
    private sealed class Skipping : ISampleProvider
    {
        private readonly ISampleProvider source;
        private long skip;

        public Skipping(ISampleProvider source, long samples)
        {
            this.source = source;
            skip = samples;
        }

        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            if (skip > 0)
            {
                var scratch = new float[SampleRate];
                while (skip > 0)
                {
                    int read = source.Read(scratch, 0, (int)Math.Min(scratch.Length, skip));
                    if (read <= 0) break;
                    skip -= read;
                }
                skip = 0;
            }
            return source.Read(buffer, offset, count);
        }
    }

    /// <summary>
    /// Music that is all in memory, from a place in it on. Before its beginning there is silence, which is
    /// what a place below zero stands for: music that has to wait before it comes in.
    /// </summary>
    private sealed class Loaded : ISampleProvider
    {
        private readonly float[] samples;
        private long position;

        /// <param name="startFrame">The frame of the music to begin with; negative for that many frames of silence first.</param>
        public Loaded(float[] samples, long startFrame, int sampleRate)
        {
            this.samples = samples;
            position = startFrame * 2;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int done = 0;
            if (position < 0)
            {
                done = (int)Math.Min(count, -position);
                for (int i = 0; i < done; i++) buffer[offset + i] = 0;
                position += done;
            }
            int take = (int)Math.Max(0, Math.Min(count - done, samples.Length - position));
            if (take > 0)
            {
                Array.Copy(samples, position, buffer, offset + done, take);
                position += take;
            }
            return done + take;
        }
    }

    /// <summary>
    /// The main audio from its first sample to its last as a video gets it (see <see cref="OpenMixdown"/>),
    /// as stereo at a sample rate with left and right by turns. Null when the project has none, or it
    /// cannot be read. Opens the file anew, so it can be called from any thread.
    /// </summary>
    public float[]? DecodeMusic(int sampleRate = SampleRate)
    {
        string? file = musicFile;
        if (file == null) return null;
        try
        {
            using var source = Open(file);
            var provider = ForFile(source.ToSampleProvider(), sampleRate, out var resampler);
            using var held = resampler;
            var all = new float[Math.Max(sampleRate, (long)((source.TotalTime.TotalSeconds + 1) * sampleRate) * 2)];
            int used = 0, read;
            while ((read = provider.Read(all, used, Math.Min(sampleRate, all.Length - used))) > 0)
            {
                used += read;
                if (used == all.Length) Array.Resize(ref all, all.Length + sampleRate * 20);
            }
            Array.Resize(ref all, used);
            return all;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <param name="sampleRate">
    /// The sample rate of the track. Every source is brought to it once, straight from the rate its file
    /// has and by the careful resampler (see <see cref="Resampled"/>): nothing goes by way of the 44.1 kHz
    /// of playback, and a source that has the rate already is taken sample for sample.
    /// </param>
    /// <param name="replacement">
    /// Other music to put in place of the main audio: the whole of it as stereo at that sample rate, such
    /// as the lossless file the beatmap's MP3 was made from. The samples of the storyboard are mixed in as ever.
    /// </param>
    /// <param name="replacementDelayMs">How much later than time 0 the replacement begins; negative when its beginning lies before that.</param>
    /// <param name="usePlaybackGains">Use the preview's music/effect gains. Video export disables this while retaining each sample's own volume.</param>
    public Mixdown OpenMixdown(double fromMs, IEnumerable<ScheduledSample> samples, int sampleRate = SampleRate, float[]? replacement = null, double replacementDelayMs = 0,
        bool usePlaybackGains = true)
    {
        WaveStream? source = null;
        ISampleProvider? music = null;
        IDisposable? resampler = null;
        if (replacement != null)
        {
            // The mixer waits out the time before 0 itself and asks for the music from 0 on.
            music = new Loaded(replacement, FramesOf(Math.Max(0, fromMs) - replacementDelayMs, sampleRate), sampleRate);
        }
        else if (musicFile != null && fromMs < DurationMs)
        {
            source = Open(musicFile);
            // Trim after resampling so an exported range uses exactly the same output samples as
            // a full export, independently of the decoder's seek precision and resampler phase.
            music = new Skipping(ForFile(source.ToSampleProvider(), sampleRate, out resampler), fromMs > 0 ? FramesOf(fromMs, sampleRate) * 2 : 0);
        }
        var mixer = new Mixer(music, fromMs < 0 ? FramesOf(-fromMs, sampleRate) : 0, sampleRate, liveRefresh: false)
        {
            HitSoundVolume = usePlaybackGains ? hitSoundVolume : 1,
            MusicVolume = usePlaybackGains ? musicVolume : 1,
        };
        // Read for this track alone: the ones playback keeps were made for listening along.
        var read = new Dictionary<string, float[]?>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in samples)
        {
            var data = LoadSample(s.File, sampleRate, read, true);
            if (data != null) mixer.Schedule(FramesOf(s.TimeMs - fromMs, sampleRate), data, s.Volume, s.HitSound, s.DurationMs > 0 ? FramesOf(s.DurationMs, sampleRate) : 0, s.Loop);
        }
        return new Mixdown(source, mixer, resampler);
    }

    private static WaveStream Open(string file) => new BassWaveReader(file);

    /// <summary>Converts any source to stereo float at a sample rate: the 44.1 kHz of playback unless told otherwise.</summary>
    private static ISampleProvider Normalize(ISampleProvider source, int sampleRate = SampleRate)
    {
        if (source.WaveFormat.Channels == 1) source = new MonoToStereoSampleProvider(source);
        else if (source.WaveFormat.Channels > 2) source = new MultiplexingSampleProvider(new[] { source }, 2);
        if (source.WaveFormat.SampleRate != sampleRate) source = new WdlResamplingSampleProvider(source, sampleRate);
        return source;
    }

    /// <summary>
    /// Converts any source to stereo float at a sample rate for the sound of a file. Where the rate has
    /// to change, that is the work of the careful resampler; the quick one of playback only stands in
    /// when that cannot be had.
    /// </summary>
    /// <param name="resampler">To be let go of once the source has been read; null when there is nothing to let go of.</param>
    private static ISampleProvider ForFile(ISampleProvider source, int sampleRate, out IDisposable? resampler)
    {
        int own = source.WaveFormat.SampleRate;
        source = Normalize(source, own);
        resampler = null;
        if (own == sampleRate) return source;
        var careful = Resampled.Create(source, sampleRate);
        resampler = careful;
        return careful != null ? careful : new WdlResamplingSampleProvider(source, sampleRate);
    }

    private float[]? LoadSample(string file) => LoadSample(file, null);

    /// <param name="checkedFiles">
    /// The files whose date and size this refresh has looked up already, as a map uses each of its few
    /// files many times; null to look this one up regardless.
    /// </param>
    private float[]? LoadSample(string file, HashSet<string>? checkedFiles)
    {
        if (SeamlessRefresh && !ResourceAssets.IsEmbedded(file) && (checkedFiles?.Add(file) ?? true))
        {
            SampleStampChecks++;
            var info = new FileInfo(file);
            var stamp = info.Exists ? (info.LastWriteTimeUtc.Ticks, info.Length) : (0L, 0L);
            if (!sampleStamps.TryGetValue(file, out var previous) || previous != stamp) sampleCache.Remove(file);
            sampleStamps[file] = stamp;
        }
        return LoadSample(file, SampleRate, sampleCache, false);
    }

    /// <summary>A sample as stereo at a sample rate, from the ones read before when it is among them.</summary>
    /// <param name="forFile">For the sound of a file rather than for playback; see <see cref="ForFile"/>.</param>
    private static float[]? LoadSample(string file, int sampleRate, Dictionary<string, float[]?> cache, bool forFile)
    {
        if (cache.TryGetValue(file, out var cached)) return cached;
        float[]? data = null;
        try
        {
            if (ResourceAssets.Exists(file))
            {
                using var stream = Open(file);
                IDisposable? resampler = null;
                var provider = forFile ? ForFile(stream.ToSampleProvider(), sampleRate, out resampler) : Normalize(stream.ToSampleProvider(), sampleRate);
                using var held = resampler;
                var all = new List<float>();
                var buffer = new float[sampleRate];
                int read;
                // Samples are short effects; anything longer than a minute is cut.
                while ((read = provider.Read(buffer, 0, buffer.Length)) > 0 && all.Count < sampleRate * 2 * 60)
                    all.AddRange(buffer.AsSpan(0, read).ToArray());
                data = all.ToArray();
            }
        }
        catch (Exception)
        {
            data = null;
        }
        cache[file] = data;
        return data;
    }

    private static Waveform? BuildWaveform(string file, Func<bool> cancelled)
    {
        using var stream = Open(file);
        var provider = stream.ToSampleProvider();
        int channels = provider.WaveFormat.Channels;
        var detail = new WaveformDetailBuilder(provider.WaveFormat.SampleRate, channels);
        int framesPerBucket = Math.Max(1, (int)(provider.WaveFormat.SampleRate * Waveform.BucketMs / 1000));
        double exactFramesPerBucket = provider.WaveFormat.SampleRate * Waveform.BucketMs / 1000;
        long bucketEnd = (long)Math.Ceiling(exactFramesPerBucket);
        var peaks = new List<float>();
        var buffer = new float[framesPerBucket * channels * 64];
        float peak = 0;
        int framesInBucket = 0;
        long totalFrames = 0;
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (cancelled()) return null;
            detail.AddSamples(buffer.AsSpan(0, read));
            for (int i = 0; i + channels <= read; i += channels)
            {
                totalFrames++;
                float v = Math.Abs(buffer[i]);
                if (channels > 1) v = Math.Max(v, Math.Abs(buffer[i + 1]));
                if (v > peak) peak = v;
                framesInBucket++;
                // A millisecond need not contain a whole number of frames. Alternate bucket sizes
                // rather than discarding fractions and making the waveform drift over the song.
                if (totalFrames >= bucketEnd)
                {
                    peaks.Add(peak);
                    peak = 0;
                    framesInBucket = 0;
                    bucketEnd = (long)Math.Ceiling((peaks.Count + 1) * exactFramesPerBucket);
                }
            }
        }
        if (framesInBucket > 0) peaks.Add(peak);
        return new Waveform { Peaks = peaks.ToArray(), DurationMs = totalFrames * 1000.0 / provider.WaveFormat.SampleRate, Detail = detail.Complete() };
    }

    public void Dispose()
    {
        Interlocked.Increment(ref musicGeneration);
        ResetTimingMusic();
        Stop();
        StopAudition();
    }

    /// <summary>
    /// Endless stereo stream of the map's sound from a starting time on. The track it plays is the lead-in
    /// silence, the music and every sample at its place, all at their own speed; that track as a whole is
    /// then tempo-adjusted by the output, so samples speed up and slow down with the music without changing pitch.
    /// </summary>
    internal sealed class Mixer : ISampleProvider
    {
        internal readonly record struct VoiceSpec(long Start, float[] Data, float Volume, bool HitSound = false,
            long Duration = 0, bool Loop = false, int LaneMask = 0, bool Click = false, bool Triggered = false);
        internal readonly record struct VoiceKey(VoiceSpec Spec, int Occurrence);
        internal sealed class Voice
        {
            internal VoiceSpec Spec;
            internal Table? ActiveTable, PlayingTable;
            internal VoiceKey key;
            internal long StartFrame, Length;
            internal float Envelope = 1, EnvelopeStep;
            internal int EnvelopeRemaining;
            internal bool Removing;
            internal float Gate = 1, GateTarget = 1, GateStep;
            internal int GateRemaining;
        }
        /// <summary>
        /// One schedule. Active holds every voice taken into it, and is only ever added to: the editor thread
        /// reads it to keep voices that are still fading out (see <see cref="BuildTable"/>) while the audio
        /// thread plays. Playing is Active less the voices that can no longer be heard, in the same order;
        /// it is what each block goes through, and only the audio thread touches it.
        /// </summary>
        internal sealed class Table
        {
            internal readonly Voice[] Desired, Active, Playing;
            internal readonly HashSet<Voice> Membership;
            internal readonly Dictionary<long, Voice> Triggers;
            internal int Count, PlayingCount;
            internal Table(Voice[] desired, int capacity)
            {
                Desired = desired;
                Active = new Voice[capacity];
                Playing = new Voice[capacity];
                Membership = new(desired);
                Triggers = new();
                foreach (var voice in desired)
                    if (voice.Spec.Triggered) Triggers.TryAdd(voice.Spec.Start, voice);
            }
        }
        private readonly ISampleProvider? music;
        private readonly bool liveRefresh;
        private readonly List<Voice> initial = new();
        private readonly Dictionary<VoiceSpec, int> initialOccurrences = new();
        private Table? current, pending;
        private int lastBuiltSize;
        private bool musicEnded;
        private long delayFrames, framePosition;
        internal const int RampFrames = 88;
        internal long FramePosition => Volatile.Read(ref framePosition);
        /// <summary>How many voices the next block goes through.</summary>
        internal int MixedVoices => liveRefresh ? current?.PlayingCount ?? 0 : initial.Count;
        public WaveFormat WaveFormat { get; }
        internal Action<float[], int, int>? BeforeGain;
        public volatile float Volume = 1;
        public volatile float HitSoundVolume = 1;
        public volatile float MusicVolume = 1;
        public volatile float ClickGain = 1;
        public volatile int MutedLanes;

        public Mixer(ISampleProvider? music, long delayFrames, int sampleRate = SampleRate, bool liveRefresh = true)
        {
            this.music = music;
            this.liveRefresh = liveRefresh;
            this.delayFrames = delayFrames;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        }

        public void Schedule(long startFrame, float[] data, float volume, bool hitSound = false, long durationFrames = 0,
            bool loop = false, int laneMask = 0, bool click = false)
        {
            var spec = new VoiceSpec(startFrame, data, volume, hitSound, durationFrames, loop, laneMask, click);
            if (data.Length == 0) return;
            AddVoice(spec);
        }

        internal Voice AddVoice(VoiceSpec spec)
        {
            int occurrence = liveRefresh ? initialOccurrences.GetValueOrDefault(spec) : 0;
            if (liveRefresh) initialOccurrences[spec] = occurrence + 1;
            var voice = NewVoice(spec, occurrence);
            initial.Add(voice);
            return voice;
        }

        internal void Activate(Voice voice, long frame)
        {
            voice.StartFrame = frame;
            var table = current!;
            if (voice.ActiveTable != table)
            {
                table.Active[table.Count] = voice;
                voice.ActiveTable = table;
                Volatile.Write(ref table.Count, table.Count + 1);
            }
            else if (voice.PlayingTable == table) return;
            voice.Gate = voice.GateTarget = voice.Spec.Click ? ClickGain : (voice.Spec.LaneMask & MutedLanes) != 0 ? 0 : 1;
            voice.GateRemaining = 0;
            AddPlaying(table, voice);
        }

        // Seal runs these two on the editor thread first, so the audio thread never waits for them to be compiled.
        private static void AddPlaying(Table table, Voice voice)
        {
            table.Playing[table.PlayingCount++] = voice;
            voice.PlayingTable = table;
        }

        /// <summary>Whether all of a voice lies before the frame the next block begins with.</summary>
        private bool Ended(Voice voice) => (framePosition - voice.StartFrame) * 2 >= voice.Length;

        internal void ReserveVoices(int count) => initial.EnsureCapacity(count + initial.Count);

        internal Action<long> PrepareVoice(float[] data, float volume, bool hitSound, long durationFrames, bool loop)
        {
            if (!liveRefresh)
            {
                var legacy = NewVoice(new(0, data, volume, hitSound, durationFrames, loop), 0);
                return frame =>
                {
                    if (frame * 2 + legacy.Length <= 0) return;
                    legacy.StartFrame = frame;
                    initial.Add(legacy);
                };
            }
            Schedule(long.MaxValue / 4, data, volume, hitSound, durationFrames, loop, click: true);
            var voice = initial[^1];
            return frame => voice.StartFrame = frame;
        }

        private Voice NewVoice(VoiceSpec spec, int occurrence)
        {
            long length = spec.Duration > 0 ? spec.Duration * 2 : spec.Data.Length;
            if (!spec.Loop) length = Math.Min(length, spec.Data.Length);
            float gate = spec.Click ? ClickGain : (spec.LaneMask & MutedLanes) != 0 ? 0 : 1;
            return new Voice { Spec = spec, key = new(spec, occurrence), StartFrame = spec.Triggered ? long.MaxValue / 4 : spec.Start, Length = length,
                Gate = gate, GateTarget = gate };
        }

        // All storage and identity matching are prepared by the editor thread. The consumer owns voice state.
        internal Table BuildTable(IEnumerable<VoiceSpec> specs)
        {
            var old = Volatile.Read(ref current);
            var reuse = new Dictionary<VoiceKey, Voice>();
            int oldCount = old == null ? 0 : Volatile.Read(ref old.Count);
            if (old != null)
            {
                foreach (var voice in old.Desired) reuse.TryAdd(voice.key, voice);
                for (int i = 0; i < oldCount; i++) reuse.TryAdd(old.Active[i].key, old.Active[i]);
            }
            else foreach (var voice in initial) reuse.TryAdd(voice.key, voice);
            var occurrences = new Dictionary<VoiceSpec, int>();
            var desired = new List<Voice>();
            foreach (var spec in specs)
            {
                if (spec.Data.Length == 0) continue;
                int occurrence = occurrences.GetValueOrDefault(spec);
                occurrences[spec] = occurrence + 1;
                desired.Add(reuse.GetValueOrDefault(new(spec, occurrence)) ?? NewVoice(spec, occurrence));
            }
            int capacity = (old == null ? initial.Count : oldCount + old.Desired.Length) + lastBuiltSize + desired.Count;
            lastBuiltSize = desired.Count;
            return new Table(desired.ToArray(), capacity);
        }
        internal void Publish(Table table) => Interlocked.Exchange(ref pending, table);
        internal void Seal()
        {
            if (!liveRefresh || current != null) return;
            var table = new Table(initial.ToArray(), initial.Count);
            foreach (var voice in initial)
                if (!voice.Spec.Triggered)
                {
                    table.Active[table.Count++] = voice;
                    voice.ActiveTable = table;
                    if (!Ended(voice)) AddPlaying(table, voice);
                }
            Volatile.Write(ref current, table);
        }
        private void TakeTable()
        {
            if (!liveRefresh) return;
            var next = Interlocked.Exchange(ref pending, null);
            if (next == null) return;
            var old = current!;
            foreach (var voice in next.Desired)
            {
                if (voice.Spec.Triggered && !old.Membership.Contains(voice)
                    && old.Triggers.TryGetValue(voice.Spec.Start, out var previous)) voice.StartFrame = previous.StartFrame;
                // Left out of the old table's blocks once faded out, so its gate has not followed the lanes since.
                bool leftOut = voice.ActiveTable == old && voice.PlayingTable != old;
                if (voice.ActiveTable != old && voice.StartFrame < framePosition)
                {
                    voice.Envelope = 0;
                    RampEnvelope(voice, 1);
                }
                else if (voice.Removing) RampEnvelope(voice, 1);
                voice.Removing = false;
                if (voice.Spec.Triggered && (voice.StartFrame >= long.MaxValue / 4 || Ended(voice))) continue;
                next.Active[next.Count++] = voice;
                voice.ActiveTable = next;
                if (Ended(voice)) continue;
                if (leftOut)
                {
                    // Written out rather than a method of its own: first needed here, on the audio thread, it
                    // would be compiled in the middle of a block.
                    voice.Gate = voice.GateTarget = voice.Spec.Click ? ClickGain : (voice.Spec.LaneMask & MutedLanes) != 0 ? 0 : 1;
                    voice.GateRemaining = 0;
                }
                AddPlaying(next, voice);
            }
            // What has ended or faded out is in Active but no longer in Playing, and would be passed over here.
            for (int i = 0; i < old.PlayingCount; i++)
            {
                var voice = old.Playing[i];
                if (next.Membership.Contains(voice) || voice.StartFrame >= framePosition || Ended(voice)) continue;
                if (!voice.Removing)
                {
                    RampEnvelope(voice, 0);
                    voice.Removing = true;
                }
                if (voice.EnvelopeRemaining > 0)
                {
                    next.Active[next.Count++] = voice;
                    voice.ActiveTable = next;
                    AddPlaying(next, voice);
                }
            }
            Volatile.Write(ref current, next);
        }

        private static void RampEnvelope(Voice voice, float target)
        {
            voice.EnvelopeRemaining = RampFrames;
            voice.EnvelopeStep = (target - voice.Envelope) / RampFrames;
        }

        private static void AdvanceGate(Voice voice, int frames)
        {
            int take = Math.Min(frames, voice.GateRemaining);
            if (take <= 0) return;
            voice.Gate += voice.GateStep * take;
            voice.GateRemaining -= take;
            if (voice.GateRemaining == 0) voice.Gate = voice.GateTarget;
        }

        /// <summary>
        /// Zeroes part of an output buffer. Not Array.Clear: what NAudio hands over as float[] is the
        /// sound card's byte buffer under another name, of which Array.Clear would only clear a quarter.
        /// The rest would still hold what was played three buffers ago, and the samples added on top of
        /// that came back as an echo that never died away.
        /// </summary>
        private static void Silence(float[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++) buffer[offset + i] = 0;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / 2;
            ReadTrack(buffer, offset, frames);

            BeforeGain?.Invoke(buffer, offset, count);
            float gain = Volume;
            for (int i = 0; i < count; i++) buffer[offset + i] *= gain;
            return count;
        }

        /// <summary>The next stretch of the track at its own speed: music and samples together.</summary>
        private void ReadTrack(float[] buffer, int offset, int frames)
        {
            Seal();
            TakeTable();
            Silence(buffer, offset, frames * 2);

            int skip = (int)Math.Min(delayFrames, frames);
            delayFrames -= skip;
            if (music != null && !musicEnded && skip < frames)
            {
                int wanted = (frames - skip) * 2;
                int read = music.Read(buffer, offset + skip * 2, wanted);
                float gain = MusicVolume;
                for (int i = 0; i < read; i++) buffer[offset + skip * 2 + i] *= gain;
                if (read < wanted)
                {
                    Silence(buffer, offset + skip * 2 + read, wanted - read);
                    musicEnded = true;
                }
            }

            long end = framePosition + frames;
            if (!liveRefresh)
            {
                for (int v = initial.Count - 1; v >= 0; v--)
                {
                    var voice = initial[v];
                    if (voice.StartFrame >= end) continue;
                    long from = Math.Max(framePosition, voice.StartFrame);
                    long dataIndex = (from - voice.StartFrame) * 2;
                    int outIndex = offset + (int)(from - framePosition) * 2;
                    int n = (int)Math.Max(0, Math.Min((end - from) * 2, voice.Length - dataIndex));
                    float gain = voice.Spec.Volume * (voice.Spec.HitSound ? HitSoundVolume : 1);
                    for (int i = 0; i < n; i++) buffer[outIndex + i] += voice.Spec.Data[voice.Spec.Loop ? (dataIndex + i) % voice.Spec.Data.Length : dataIndex + i] * gain;
                    if (dataIndex + n >= voice.Length) initial.RemoveAt(v);
                }
                framePosition = end;
                return;
            }
            var table = current!;
            int muted = MutedLanes;
            float clickGain = ClickGain;
            int left = 0;
            for (int v = table.PlayingCount - 1; v >= 0; v--)
            {
                var voice = table.Playing[v];
                var spec = voice.Spec;
                float target = spec.Click ? clickGain : (spec.LaneMask & muted) != 0 ? 0 : 1;
                if (target != voice.GateTarget)
                {
                    voice.GateTarget = target;
                    voice.GateRemaining = RampFrames;
                    voice.GateStep = (target - voice.Gate) / RampFrames;
                }
                if (voice.StartFrame >= end)
                {
                    AdvanceGate(voice, frames);
                    continue;
                }
                long from = Math.Max(framePosition, voice.StartFrame);
                AdvanceGate(voice, (int)(from - framePosition));
                long dataIndex = (from - voice.StartFrame) * 2;
                int outIndex = offset + (int)(from - framePosition) * 2;
                int n = (int)Math.Max(0, Math.Min((end - from) * 2, voice.Length - dataIndex));
                float gain = spec.Volume * (spec.HitSound ? HitSoundVolume : 1);
                for (int i = 0; i < n; i += 2)
                {
                    AdvanceGate(voice, 1);
                    if (voice.EnvelopeRemaining > 0)
                    {
                        voice.Envelope += voice.EnvelopeStep;
                        if (--voice.EnvelopeRemaining == 0) voice.Envelope = voice.Removing ? 0 : 1;
                    }
                    float scale = gain * voice.Gate * voice.Envelope;
                    long at = spec.Loop ? (dataIndex + i) % spec.Data.Length : dataIndex + i;
                    buffer[outIndex + i] += spec.Data[at] * scale;
                    buffer[outIndex + i + 1] += spec.Data[at + 1] * scale;
                }
                // Nothing more can be heard from it in this table: it is over, or faded out to silence.
                if (dataIndex + n >= voice.Length || (voice.Removing && voice.EnvelopeRemaining == 0))
                {
                    voice.PlayingTable = null;
                    left++;
                }
            }
            if (left > 0)
            {
                // Take them out keeping the others' order, and so the order they are added in. Done here rather
                // than in a method of its own: called this seldom, that would only be recompiled for speed some
                // way into playback, and the runtime pausing every thread to switch to it stalled one block.
                var playing = table.Playing;
                int count = table.PlayingCount, kept = 0;
                for (int i = 0; i < count; i++)
                    if (playing[i].PlayingTable == table) playing[kept++] = playing[i];
                Array.Clear(playing, kept, count - kept);
                table.PlayingCount = kept;
            }
            framePosition = end;
        }

    }
}
