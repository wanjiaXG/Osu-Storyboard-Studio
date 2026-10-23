namespace SbStudio.App.Ui;

public sealed partial class MainUi
{
    private readonly TimelineClipboard timelineClipboard = new();
    private long nextClipboardPoll;
    internal Func<uint> ClipboardRevision { get; set; } = TimelineClipboard.GetClipboardSequenceNumber;
    internal Func<string?> ClipboardText { get; set; } = TimelineClipboard.ReadText;
    private uint scriptedClipboardRevision;

    internal void SetScriptedClipboard(string text)
    {
        scriptedClipboardRevision++;
        ClipboardRevision = () => scriptedClipboardRevision;
        ClipboardText = () => text;
        nextClipboardPoll = 0;
    }

    private void PollTimelineClipboard()
    {
        if (!OperatingSystem.IsWindows() || Environment.TickCount64 < nextClipboardPoll) return;
        nextClipboardPoll = Environment.TickCount64 + 150;
        bool enabled = app.Settings.TimelineAutoJump && app.Project != null && !app.IsLoading && !app.Exporting && !app.PreviewingEdit;
        if (timelineClipboard.Poll(enabled, ClipboardRevision(), ClipboardText) is { } time)
        {
            app.Seek(time);
            timeline.RevealPlayhead();
        }
    }

    private void ResetTimelineClipboard()
    {
        if (!OperatingSystem.IsWindows()) return;
        timelineClipboard.Poll(false, ClipboardRevision(), ClipboardText);
        nextClipboardPoll = 0;
        PollTimelineClipboard();
    }
}
