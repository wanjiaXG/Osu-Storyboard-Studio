namespace SbStudio.App.Audio;

internal enum HitSoundKind { Normal, Whistle, Finish, Clap, Custom, SliderTick, SliderSlide, SliderWhistle, SpinnerSpin, Banana }
internal enum HitSoundOrigin { Circle, SliderHead, SliderRepeat, SliderTail, SliderTick, SliderBody, SpinnerTail, SpinnerBody, ManiaNote, HoldHead, TaikoRoll, TaikoSwell, CatchFruit, CatchDroplet, CatchBanana }
internal enum HitSoundSource { Beatmap, Skin, Default, Missing }

internal sealed record HitSoundEvent(double Time, double Duration, HitSoundKind Kind, int Bank, int CustomIndex,
    float Volume, bool ExplicitBank, string SampleName, string? File, HitSoundSource Source, HitSoundOrigin Origin, int ObjectIndex, bool Loop)
{
    /// <summary>A source flag represented by another event's combined gameplay sample; still auditionable.</summary>
    public bool TimelineOnly { get; init; }
    public double End => Time + Duration;
    public int Lane => Kind switch { HitSoundKind.Whistle or HitSoundKind.SliderWhistle => 1, HitSoundKind.Finish => 2, HitSoundKind.Clap => 3, HitSoundKind.Custom => 4, _ => 0 };
    public ScheduledSample? Playback => File == null ? null : new(Time, File, Volume, true, Duration, Loop);
}

/// <summary>Immutable, time-indexed snapshot shared by audio playback and the read-only timeline.</summary>
internal sealed class HitSoundEventIndex
{
    public static readonly HitSoundEventIndex Empty = new(Array.Empty<HitSoundEvent>());
    public HitSoundEvent[] Events { get; }
    public HitSoundEvent[][] Lanes { get; }
    private readonly double[][] prefixEnds;
    public HitSoundEventIndex(IEnumerable<HitSoundEvent> events)
    {
        Events = events.OrderBy(e => e.Time).ToArray();
        Lanes = Enumerable.Range(0, 5).Select(lane => Events.Where(e => e.Lane == lane).ToArray()).ToArray();
        prefixEnds = Lanes.Append(Events).Select(lane =>
        {
            var ends = new double[lane.Length];
            double end = double.NegativeInfinity;
            for (int i = 0; i < lane.Length; i++) ends[i] = end = Math.Max(end, lane[i].End);
            return ends;
        }).ToArray();
    }
    public IEnumerable<ScheduledSample> Playback => Events.Where(e => e.File != null && !e.TimelineOnly).Select(e => e.Playback!.Value);

    public IEnumerable<ScheduledSample> PlaybackWithLaneMasks
    {
        get
        {
            var masks = new Dictionary<(int, ScheduledSample?), int>();
            foreach (var sound in Events)
                if (sound.File != null)
                {
                    var key = (sound.ObjectIndex, sound.Playback);
                    masks[key] = masks.GetValueOrDefault(key) | (1 << sound.Lane);
                }
            foreach (var sound in Events)
                if (sound.File != null && !sound.TimelineOnly)
                    yield return sound.Playback!.Value with { LaneMask = masks[(sound.ObjectIndex, sound.Playback)] };
        }
    }

    public IEnumerable<ScheduledSample> PlaybackWithMutedLanes(int mutedLanes) =>
        AudibleEvents(Events.Where(e => !e.TimelineOnly), mutedLanes).Select(e => e.Playback!.Value);

    public IEnumerable<ScheduledSample> AuditionWithMutedLanes(IEnumerable<HitSoundEvent> sounds, int mutedLanes) =>
        AudibleEvents(sounds, mutedLanes).DistinctBy(e => (e.ObjectIndex, e.Playback)).Select(e => e.Playback!.Value);

    private IEnumerable<HitSoundEvent> AudibleEvents(IEnumerable<HitSoundEvent> sounds, int mutedLanes)
    {
        if (mutedLanes == 0) return sounds.Where(e => e.File != null);
        // Argon taiko can show several source flags for one physical drum sample. Muting any of its
        // lanes must also suppress that shared sample when reached through another lane or audition.
        var muted = Events.Where(e => e.File != null && (mutedLanes & (1 << e.Lane)) != 0)
            .Select(e => (e.ObjectIndex, e.Playback)).ToHashSet();
        return sounds.Where(e => e.File != null && (mutedLanes & (1 << e.Lane)) == 0
            && !muted.Contains((e.ObjectIndex, e.Playback)));
    }
    public int FirstVisible(int lane, double time)
    {
        var ends = prefixEnds[lane < 0 ? 5 : lane];
        int low = 0, high = ends.Length;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (ends[middle] < time) low = middle + 1; else high = middle;
        }
        return low;
    }

    public static int FirstAtOrAfter(IReadOnlyList<HitSoundEvent> events, double time)
    {
        int low = 0, high = events.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (events[middle].Time < time) low = middle + 1; else high = middle;
        }
        return low;
    }
}
