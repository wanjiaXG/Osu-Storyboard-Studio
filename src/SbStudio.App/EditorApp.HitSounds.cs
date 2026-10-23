using SbStudio.App.Audio;
using SbStudio.Core;

namespace SbStudio.App;

public sealed partial class EditorApp
{
    private OsuBeatmap? hitSoundMap;
    private string hitSoundSkin = "";
    private HitSoundEventIndex hitSoundEvents = HitSoundEventIndex.Empty;
    private int hiddenHitSoundLanes;
    internal bool HitSoundRecheckSupported => Beatmap?.Mode != 3;
    internal string? HitSoundRecheckProblem => HitSoundRecheckSupported ? null : L.T("hitsound.maniaUnsupported");
    internal HitSoundEventIndex HitSoundRecheckEvents => HitSoundRecheckSupported ? HitSoundEvents : HitSoundEventIndex.Empty;

    internal bool IsHitSoundLaneHidden(int lane) => (hiddenHitSoundLanes & (1 << lane)) != 0;

    internal void SetHitSoundLaneHidden(int lane, bool hidden)
    {
        if ((uint)lane >= 5) throw new ArgumentOutOfRangeException(nameof(lane));
        if (IsHitSoundLaneHidden(lane) == hidden) return;
        if (hidden) hiddenHitSoundLanes |= 1 << lane;
        else hiddenHitSoundLanes &= ~(1 << lane);
        if (Settings.SeamlessPlaybackRefresh)
        {
            Audio.MutedLanes = hiddenHitSoundLanes;
            return;
        }
        // Rebuild at the current time, removing already-playing tails/loops as well as future samples.
        RefreshPlayback();
    }

    internal void InvalidateHitSoundEvents() => hitSoundMap = null;

    internal IEnumerable<ScheduledSample> HitSoundAudition(IEnumerable<HitSoundEvent> sounds) =>
        HitSoundEvents.AuditionWithMutedLanes(sounds, hiddenHitSoundLanes);

    public void SetHitSoundRecheck(bool enabled)
    {
        if (Settings.ShowHitSoundRecheck == enabled && (!enabled || !Settings.ShowTimingRecheck)) return;
        Settings.ShowHitSoundRecheck = enabled;
        if (enabled) Settings.ShowTimingRecheck = false;
        RefreshPlayback();
    }

    // Reads the difficulty even when hit-object visuals are disabled. Source-file refresh replaces
    // Beatmap, invalidating this snapshot without any edits to its objects or storyboard commands.
    internal HitSoundEventIndex HitSoundEvents
    {
        get
        {
            var map = Beatmap;
            if (map == null) return HitSoundEventIndex.Empty;
            if (map != hitSoundMap || hitSoundSkin != HitObjects.SkinId)
            {
                hitSoundEvents = HitSoundScheduler.Build(map, HitSoundScheduler.Skin(HitObjects.SkinLibrary, HitObjects.SkinId), Project?.BeatmapDirectory);
                hitSoundMap = map;
                hitSoundSkin = HitObjects.SkinId;
            }
            return hitSoundEvents;
        }
    }
}
