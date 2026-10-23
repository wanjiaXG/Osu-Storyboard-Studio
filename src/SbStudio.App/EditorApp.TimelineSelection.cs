using SbStudio.Core;

namespace SbStudio.App;

/// <summary>Section headers share one selection model, including sections that are not OSB layers.</summary>
public enum TimelineSection
{
    Background = (int)SbLayer.Background,
    Fail = (int)SbLayer.Fail,
    Pass = (int)SbLayer.Pass,
    Foreground = (int)SbLayer.Foreground,
    Overlay = (int)SbLayer.Overlay,
    Samples = SbInfo.LayerCount,
    Groups,
    Videos,
    HitSounds,
    Waveform,
    HitObjects,
    BeatmapBackground,
    Timing,
}

public sealed partial class EditorApp
{
    public TimelineSection? SelectedTimelineSection { get; private set; }

    public void SelectTimelineSection(TimelineSection section)
    {
        if (!Enum.IsDefined(section)) return;
        ClearSelection();
        if ((int)section < SbInfo.LayerCount) insertionLayer = (SbLayer)section;
        SelectedTimelineSection = section;
    }
}
