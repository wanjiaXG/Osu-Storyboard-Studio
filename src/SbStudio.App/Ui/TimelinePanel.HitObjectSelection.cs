using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using SbStudio.App.Gfx;
using SbStudio.Core;

namespace SbStudio.App.Ui;

public sealed partial class TimelinePanel
{
    private readonly TimelineHitObjectSelection hitObjectSelection = new();
    private bool hitObjectSelectionFocused;
    private int doubleClickObject = -1;
    private int[]? doubleClickSelection;
    internal string? LastOsuEditLink { get; private set; }
    internal string? CopiedHitObjectTimestamp { get; private set; }
    internal Action<string> WriteHitObjectClipboard { get; set; } = ImGui.SetClipboardText;
    internal Action<string> OpenOsuEditLink { get; set; } = uri => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    private bool HitObjectSelectionContext => Open && hitObjectSelectionFocused && ShowReferenceObjects && !collapsed[HitObjectsSection]
        && referenceMap != null && referenceMap == app.Beatmap;
    internal string HitObjectSelectionState => $"selected={string.Join(',', hitObjectSelection.Objects.Select(e => e.Index))}\n"
        + $"timestamp={(referenceMap == null ? "" : OsuEditorTimestamp.Format(referenceMap, hitObjectSelection.Objects, app.Time))}\nlink={LastOsuEditLink}\nclipboard={CopiedHitObjectTimestamp}\n";

    internal bool HandleHitObjectShortcuts(ImGuiIOPtr io)
    {
        UpdateReferenceRows();
        if (!HitObjectSelectionContext) return false;
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.C, false))
        {
            CopiedHitObjectTimestamp = OsuEditorTimestamp.Format(referenceMap!, hitObjectSelection.Objects, app.Time);
            WriteHitObjectClipboard(CopiedHitObjectTimestamp);
            return true;
        }
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.A, false)) { CancelHitObjectMarquee(); hitObjectSelection.SelectAll(); doubleClickSelection = null; return true; }
        if (ImGui.IsKeyPressed(ImGuiKey.Escape, false)) { CancelHitObjectMarquee(); hitObjectSelection.Clear(); doubleClickSelection = null; return true; }
        // Read-only reference selection must not cause an edit to a previously selected storyboard object.
        return ImGui.IsKeyPressed(ImGuiKey.Delete, false) || io.KeyCtrl &&
            (ImGui.IsKeyPressed(ImGuiKey.X, false) || ImGui.IsKeyPressed(ImGuiKey.V, false) || ImGui.IsKeyPressed(ImGuiKey.D, false)
             || ImGui.IsKeyPressed(ImGuiKey.G, false) || ImGui.IsKeyPressed(ImGuiKey.LeftBracket, true) || ImGui.IsKeyPressed(ImGuiKey.RightBracket, true));
    }

    private (float Centre, float Radius) HitObjectGeometry(TimelineHitObject entry, int ordinal, float y, float height)
    {
        var renderer = app.HitObjects;
        if (referenceMap?.Mode is 0 or 2 && !renderer.TimelineLazerStyle)
        {
            int stack = 0;
            for (int i = ordinal - 1; i >= 0 && referenceObjects.Objects[i].Object.StartTime == entry.Object.StartTime; i--) stack++;
            return (LegacyTimelineStyle.Centre(y, height, Theme.Scale) - Theme.Px(2 * stack * LegacyTimelineStyle.DisplayScale), Theme.Px(24));
        }
        float radius = Theme.Px(renderer.TimelineLazerStyle ? 16 : 19);
        float centre = y + height / 2 + Theme.Px(2);
        centre -= Math.Min(Theme.Px((renderer.TimelineLazerStyle ? 5 : 2) * entry.Stack), centre - y - radius - 1);
        return (centre, radius);
    }

    private void DrawHitObjectSelection(ImDrawListPtr dl, float y, float height, float right, int first, int last)
    {
        for (int i = first; i <= last; i++)
        {
            var entry = referenceObjects.Objects[i];
            if (!hitObjectSelection.Contains(entry.Index)) continue;
            var (centre, radius) = HitObjectGeometry(entry, i, y, height);
            float start = X(entry.Object.StartTime), end = X(entry.Object.EndTime);
            if (end > start)
                dl.AddRect(new(Math.Max(trackX - radius, start - radius - 2), centre - radius - 2),
                    new(Math.Min(right + radius, end + radius + 2), centre + radius + 2), Theme.Accent, radius, ImDrawFlags.None, Theme.Px(2));
            dl.AddCircle(new(start, centre), radius + Theme.Px(2), Theme.Accent, 32, Theme.Px(2));
        }
        DrawHitObjectMarquee(dl, y, height, right);
    }

    private TimelineHitObject? HitReferenceObject(Vector2 mouse, float y, float height, out double seek)
    {
        double time = TimeAt(mouse.X), margin = Theme.Px(26) / pxPerMs;
        TimelineHitObject? hit = null;
        float nearest = float.PositiveInfinity;
        seek = time;
        for (int i = referenceObjects.FirstVisible(time - margin); i < referenceObjects.Objects.Length && referenceObjects.Objects[i].Object.StartTime <= time + margin; i++)
        {
            var entry = referenceObjects.Objects[i];
            var obj = entry.Object;
            var (centre, radius) = HitObjectGeometry(entry, i, y, height);
            float dy = Math.Abs(mouse.Y - centre);
            if (dy > radius + 2) continue;
            double candidate = Math.Abs(time - obj.StartTime) <= Math.Abs(time - obj.EndTime) ? obj.StartTime : obj.EndTime;
            if (obj is Slider slider)
            {
                int edge = Math.Clamp((int)Math.Round((time - slider.StartTime) / slider.SpanDuration), 0, slider.SpanCount);
                candidate = slider.StartTime + edge * slider.SpanDuration;
            }
            float dx = Math.Abs(mouse.X - X(candidate)), distance = MathF.Sqrt(dx * dx + dy * dy);
            if (obj.StartTime < time && obj.EndTime > time && distance > radius) { candidate = app.SnapMs(time); distance = radius + dy; }
            else if (distance > radius + 2) continue;
            if (distance >= nearest) continue;
            nearest = distance;
            hit = entry;
            seek = candidate;
        }
        return hit;
    }

    private void SelectReferenceObject(TimelineHitObject? hit, double seek, ImGuiIOPtr io)
    {
        if (!ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return;
        BeginHitObjectMarquee(io);
        if (hit == null)
        {
            if (!io.KeyCtrl && !io.KeyShift) hitObjectSelection.Clear();
            doubleClickSelection = null;
            return;
        }
        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            // A first click selects one object; a double click inside an existing group opens the group.
            if (!io.KeyCtrl && !io.KeyShift && doubleClickObject == hit.Index && doubleClickSelection != null)
            {
                hitObjectSelection.Clear();
                foreach (int index in doubleClickSelection) hitObjectSelection.Select(index, toggle: true);
            }
            doubleClickSelection = null;
            if (!hitObjectSelection.Contains(hit.Index)) hitObjectSelection.Select(hit.Index);
            LastOsuEditLink = OsuEditorTimestamp.Link(OsuEditorTimestamp.Format(referenceMap!, hitObjectSelection.Objects, app.Time));
            try { OpenOsuEditLink(LastOsuEditLink); }
            catch (Exception ex) { app.SetStatus(L.T("timeline.hitObject.openFailed", ex.Message)); }
        }
        else
        {
            doubleClickObject = hit.Index;
            doubleClickSelection = !io.KeyCtrl && !io.KeyShift && hitObjectSelection.Count > 1 && hitObjectSelection.Contains(hit.Index)
                ? hitObjectSelection.Objects.Select(e => e.Index).ToArray() : null;
            hitObjectSelection.Select(hit.Index, io.KeyCtrl, io.KeyShift);
        }
        if (!app.Settings.OsuStyleTimelineIndicator) hitObjectClickSeek = seek;
    }
}
