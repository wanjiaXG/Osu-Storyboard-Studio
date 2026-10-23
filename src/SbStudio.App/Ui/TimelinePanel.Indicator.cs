using System.Globalization;

namespace SbStudio.App.Ui;

public sealed partial class TimelinePanel
{
    private double scrubViewStart, scrubPixelsPerMs;
    private float scrubLeft;
    private float snapWheelRemainder;

    private void StepFixedWheel(float delta)
    {
        snapWheelRemainder -= delta;
        int ticks = (int)Math.Truncate(snapWheelRemainder);
        if (ticks == 0) return;
        snapWheelRemainder -= ticks;
        app.StepTime(ticks);
    }
    private float PlayheadX => app.Settings.OsuStyleTimelineIndicator
        ? trackX + (float)((trackRight - trackX) * TimelineViewport.OsuAnchor) : X(app.Time);

    private void BeginTimelineScrub()
    {
        scrubWasPlaying = app.Playing;
        app.Pause();
        scrubViewStart = viewStart;
        scrubPixelsPerMs = pxPerMs;
        scrubLeft = trackX;
    }
    private double ScrubTimeAt(float mouseX) => app.Settings.OsuStyleTimelineIndicator
        ? TimelineViewport.ScrubTime(scrubViewStart, scrubPixelsPerMs, scrubLeft, mouseX) : TimeAt(mouseX);

    private void SeekFixedView()
    {
        if (app.Settings.OsuStyleTimelineIndicator) app.SeekSnapped(TimeAt(PlayheadX));
    }

    // Automation can inspect the visible geometry without writing settings or project data.
    internal string ViewportState => string.Create(CultureInfo.InvariantCulture,
        $"fixed={app.Settings.OsuStyleTimelineIndicator}\ntime={app.Time:R}\nleft={trackX:R}\nright={trackRight:R}\nviewStart={viewStart:R}\npixelsPerMs={pxPerMs:R}\nplayheadX={PlayheadX:R}\ntimeX={X(app.Time):R}\nrowScroll={bodyScroll:R}\nrowHeight={bodyRowHeight:R}\nrowViewHeight={bodyViewHeight:R}\noptionsExpanded={optionsExpanded}\nshowingOnly={showingOnly}\nownOnly={ownOnly}\nthumbnails={app.Settings.ShowTimelineThumbnails}\nmouseCursor={ImGuiNET.ImGui.GetMouseCursor()}\nedgePlugin={edgeGroup?.Plugin?.Instance ?? 0}\nedgeGroup={edgeGroup?.Name}\nedgePart={edgePart}\ntrimmingEffect={effectTrim?.Plugin?.Instance ?? 0}\n");
}
