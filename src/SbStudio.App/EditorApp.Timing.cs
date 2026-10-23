using SbStudio.App.Audio;
using SbStudio.Core;

namespace SbStudio.App;

public sealed partial class EditorApp
{
    private (double Offset, double BeatLength, int Meter)[] timingSnapshot = Array.Empty<(double, double, int)>();
    private (double Start, double End) timingBounds;
    private int timingSnapDivisor = 1;
    private TimingEventIndex timingEvents = TimingEventIndex.Empty;
    private TimingEventIndex scheduledTimingIndex = TimingEventIndex.Empty;
    internal bool TimingLaneHidden => !Settings.TimingAssistSound;

    internal double? TimingOffsetTarget(int direction) => Project == null ? null
        : TimingUtil.AdjacentOffset(Project.Settings.Timing, Time, direction);

    public void JumpTimingOffset(int direction)
    {
        if (TimingOffsetTarget(direction) is { } offset) Seek(offset);
    }

    internal TimingPoint? CurrentTimingOffset => Project == null ? null
        : TimingPointEditing.AtOrBefore(Project.Settings.Timing, Time);

    internal TimingPoint? TimingOffsetAtPlayhead => Project == null ? null
        : TimingPointEditing.AtPlayhead(Project.Settings.Timing, Time);

    public bool AddTimingOffset(double offset, double bpm, int meter)
    {
        if (Project == null || TimingPointEditing.Validate(Project.Settings.Timing, offset, bpm, meter) != null) return false;
        return ChangeTiming(HistoryAction.AddTiming, () =>
        {
            Project.Settings.Timing.Add(new TimingPoint { Offset = offset, Bpm = bpm, Meter = meter });
            Project.Settings.Timing.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            return true;
        });
    }

    /// <summary>
    /// Adds red lines found by the analysis as one step of the history: offsets in whole milliseconds, BPMs to three
    /// decimals, in <paramref name="meter"/>. With <paramref name="replace"/> the red lines already between <paramref name="from"/> and
    /// <paramref name="to"/> go first, so old and new do not interleave. Returns how many were added; a red line at an
    /// offset that is already taken is skipped.
    /// </summary>
    internal int AddAnalysedTiming(IReadOnlyList<(double Offset, double Bpm)> sections, double from, double to, bool replace, int meter = 4)
    {
        if (Project == null) return 0;
        int added = 0;
        ChangeTiming(HistoryAction.AnalyseTiming, () =>
        {
            var timing = Project.Settings.Timing;
            if (replace) timing.RemoveAll(p => p.Offset >= from && p.Offset < to);
            foreach (var (offset, bpm) in sections)
            {
                double o = Math.Round(offset, MidpointRounding.AwayFromZero), b = Math.Round(bpm, 3);
                if (TimingPointEditing.Validate(timing, o, b, meter) != null) continue;
                timing.Add(new TimingPoint { Offset = o, Bpm = b, Meter = meter });
                added++;
            }
            timing.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            return added > 0 || replace;
        });
        return added;
    }

    internal bool DeleteTimingOffset(TimingPoint point)
    {
        if (Project == null || point != TimingOffsetAtPlayhead) return false;
        return ChangeTiming(HistoryAction.DeleteTiming, () => Project.Settings.Timing.Remove(point));
    }

    internal bool ApplyTimingBpm(TimingBpmResult result)
    {
        if (Project == null) return false;
        // The BPM may be the suggestion or one typed by hand; it need not land exactly on the target snap.
        var timing = Project.Settings.Timing;
        var verified = TimingBpmCalculator.Evaluate(timing, result.Target, result.Steps, result.Divisor, result.Bpm, result.Direction);
        if (verified == null || verified.Reference != result.Reference) return false;
        var point = result.Direction == TimingBpmDirection.Next ? timing.FirstOrDefault(p => p.Offset == result.Target) : verified.Reference;
        if (point?.Bpm == verified.Bpm) return true;
        if (point == null)
        {
            int meter = TimingBpmCalculator.StartPoint(timing, result.Target, result.Reference, result.Direction).Meter;
            if (TimingPointEditing.Validate(timing, result.Target, result.Bpm, meter) != null) return false;
            return ChangeTiming(HistoryAction.CalibrateTiming, () =>
            {
                timing.Add(new TimingPoint { Offset = result.Target, Bpm = result.Bpm, Meter = meter });
                timing.Sort((a, b) => a.Offset.CompareTo(b.Offset));
                return true;
            });
        }
        return ChangeTiming(HistoryAction.CalibrateTiming, () => { point.Bpm = verified.Bpm; return true; });
    }

    internal bool ApplyTimingList(TimingListEdit edit) => edit.Project == Project && ChangeTiming(HistoryAction.EditTiming, edit.Apply);

    internal bool AdjustCurrentTimingBpm(int direction, bool largeStep)
        => AdjustTimingBpm(CurrentTimingOffset, direction, largeStep);

    internal bool AdjustTimingBpm(TimingPoint? point, int direction, bool largeStep)
    {
        if (Project == null || point == null || !Project.Settings.Timing.Contains(point)
            || direction is not (-1 or 1) || !double.IsFinite(point.Bpm)) return false;
        double bpm = Math.Clamp(Math.Round(point.Bpm + direction * (largeStep ? 1 : 0.1), 3), 0.001, 1000);
        if (bpm == point.Bpm) return false;
        return ChangeTiming(HistoryAction.AdjustTiming, () => { point.Bpm = bpm; return true; });
    }

    internal bool AdjustTimingOffset(TimingPoint? point, int direction, bool largeStep)
    {
        if (Project == null || point == null
            || TimingPointEditing.OffsetAfterAdjustment(Project.Settings.Timing, point, direction, largeStep) is not { } offset) return false;
        return ChangeTiming(HistoryAction.AdjustTimingOffset, () => { point.Offset = offset; return true; });
    }

    internal bool SetTimingOffset(TimingPoint point, string text, out string? error, out string neighbours)
    {
        error = null;
        neighbours = "";
        if (Project == null || !Project.Settings.Timing.Contains(point)) return false;
        error = TimingPointEditing.ValidateOffsetInput(Project.Settings.Timing, point, text, out double offset, out neighbours);
        if (error != null || offset == point.Offset) return false;
        return ChangeTiming(HistoryAction.AdjustTimingOffset, () => { point.Offset = offset; return true; });
    }

    internal bool SetTimingBpm(TimingPoint point, double bpm)
    {
        if (Project == null || !Project.Settings.Timing.Contains(point) || TimingPointEditing.ValidateBpm(bpm) != null || point.Bpm == bpm)
            return false;
        return ChangeTiming(HistoryAction.AdjustTiming, () => { point.Bpm = bpm; return true; });
    }

    private bool ChangeTiming(HistoryAction action, Func<bool> change)
    {
        if (Project == null || PreviewingEdit) return false;
        CommitEdits();
        if (!change()) return false;
        Touch();
        CommitEdits();
        if (committed != null) committed = committed with { HistoryLabel = new(action) };
        RefreshPlayback();
        return true;
    }

    public void SetTimingRecheck(bool enabled)
    {
        if (Settings.ShowTimingRecheck == enabled && (!enabled || !Settings.ShowHitSoundRecheck)) return;
        Settings.ShowTimingRecheck = enabled;
        if (enabled) Settings.ShowHitSoundRecheck = false;
        RefreshPlayback();
    }

    public void SetTimingAssistSound(bool enabled)
    {
        if (Settings.TimingAssistSound == enabled) return;
        Settings.TimingAssistSound = enabled;
        if (Settings.SeamlessPlaybackRefresh)
        {
            Audio.ClickGain = enabled ? 1 : 0;
            if (Settings.ShowTimingRecheck && !enabled) Audio.StopAudition();
            return;
        }
        if (Settings.ShowTimingRecheck) RefreshPlayback();
    }

    public void SetTimingSnapSound(bool enabled)
    {
        if (Settings.TimingSnapSound == enabled) return;
        Settings.TimingSnapSound = enabled;
        bool wasHeld = timingSnapSoundHeld;
        timingSnapSoundHeld = false;
        // Held with Ctrl it already sounded on every snap: turning the setting on changes nothing that is heard.
        if (Settings.ShowTimingRecheck && !(enabled && wasHeld)) RefreshPlayback();
    }

    public void SetTimingSoundFollowsTempo(bool enabled)
    {
        if (Settings.TimingSoundFollowsTempo == enabled) return;
        Settings.TimingSoundFollowsTempo = enabled;
        if (Settings.ShowTimingRecheck) RefreshPlayback();
    }

    public void SetTimingSlowPlayback(TimingSlowPlaybackStyle style)
    {
        if (!Enum.IsDefined(style)) style = TimingSlowPlaybackStyle.PlaybackClock;
        if (Settings.TimingSlowPlayback == style) return;
        Settings.TimingSlowPlayback = style;
        if (Settings.ShowTimingRecheck && Rate < 1) RefreshPlayback();
    }

    /// <summary>Ctrl held: sounds on every snap for as long as it is held, while Timing check is on and the setting is off.</summary>
    private bool timingSnapSoundHeld;

    /// <summary>Whether ticks sound on every snap now: by the setting, or for as long as Ctrl is held.</summary>
    public bool TimingSnapSoundActive => Settings.TimingSnapSound || timingSnapSoundHeld;

    /// <summary>
    /// Called every frame with whether Ctrl is held. It counts only while Timing check is on and the setting is off;
    /// letting go goes back to ticks on the beats alone. The setting itself is not touched or saved.
    /// </summary>
    public void HoldTimingSnapSound(bool held)
    {
        held = held && Settings.ShowTimingRecheck && !Settings.TimingSnapSound && Project != null;
        if (held == timingSnapSoundHeld) return;
        timingSnapSoundHeld = held;
        if (Settings.ShowTimingRecheck) RefreshPlayback();
    }

    internal void SetTimingLaneHidden(bool hidden)
    {
        SetTimingAssistSound(!hidden);
    }

    private void UpdateTimingPlayback()
    {
        Audio.SetTimingMode(Settings.ShowTimingRecheck, Settings.TimingSoundFollowsTempo, Settings.TimingSlowPlayback);
        if (Playing && Settings.ShowTimingRecheck && TimingRecheckEvents != scheduledTimingIndex)
            RefreshPlayback();
    }

    internal TimingEventIndex TimingRecheckEvents
    {
        get
        {
            if (Project == null || !HasTiming) return TimingEventIndex.Empty;
            var points = Project.Settings.Timing;
            var bounds = TimeBounds;
            int snapDivisor = TimingSnapSoundActive ? Divisor : 1;
            // Timing points can be edited in place; compare values rather than list identity. A new index also
            // makes playing audio pick up a changed divisor (UpdateTimingPlayback).
            if (bounds != timingBounds || snapDivisor != timingSnapDivisor || !points.Select(p => (p.Offset, p.BeatLength, p.Meter)).SequenceEqual(timingSnapshot))
            {
                timingSnapshot = points.Select(p => (p.Offset, p.BeatLength, p.Meter)).ToArray();
                timingBounds = bounds;
                timingSnapDivisor = snapDivisor;
                timingEvents = TimingEventIndex.Build(points, bounds.Start, bounds.End, snapDivisor);
            }
            return timingEvents;
        }
    }
}
