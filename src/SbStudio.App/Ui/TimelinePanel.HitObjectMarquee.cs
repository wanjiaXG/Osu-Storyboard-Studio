using System.Numerics;
using ImGuiNET;

namespace SbStudio.App.Ui;

public sealed partial class TimelinePanel
{
    private bool hitObjectMarqueePending, hitObjectMarqueeActive;
    private double hitObjectMarqueeTime;
    private Vector2 hitObjectMarqueeStart, hitObjectMarqueeMouse;
    private int[] hitObjectMarqueeBase = Array.Empty<int>();
    private double? hitObjectClickSeek;
    private void CancelHitObjectMarquee()
    {
        hitObjectMarqueePending = hitObjectMarqueeActive = false;
        hitObjectClickSeek = null;
    }

    private void BeginHitObjectMarquee(ImGuiIOPtr io)
    {
        hitObjectMarqueePending = true;
        hitObjectMarqueeActive = false;
        hitObjectMarqueeStart = hitObjectMarqueeMouse = io.MousePos;
        hitObjectMarqueeTime = TimeAt(io.MousePos.X);
        hitObjectMarqueeBase = io.KeyCtrl || io.KeyShift ? hitObjectSelection.Objects.Select(e => e.Index).ToArray() : Array.Empty<int>();
        hitObjectClickSeek = null;
    }

    private void UpdateHitObjectMarquee(ImGuiIOPtr io, float contentTop, float rowHeight)
    {
        if (!hitObjectMarqueePending) return;
        int row = rows.FindIndex(r => r.Kind == RowKind.HitObjects);
        if (row < 0) { hitObjectMarqueePending = hitObjectMarqueeActive = false; hitObjectClickSeek = null; return; }
        hitObjectMarqueeMouse = io.MousePos;
        if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            if (!ImGui.IsMouseDragging(ImGuiMouseButton.Left, Theme.Px(4))) return;
            hitObjectMarqueeActive = true;
            hitObjectClickSeek = null;
            double from = Math.Min(hitObjectMarqueeTime, TimeAt(io.MousePos.X)), until = Math.Max(hitObjectMarqueeTime, TimeAt(io.MousePos.X));
            float minY = Math.Min(hitObjectMarqueeStart.Y, io.MousePos.Y), maxY = Math.Max(hitObjectMarqueeStart.Y, io.MousePos.Y);
            float y = contentTop + rowLayout.Top(row) * rowHeight, height = rowLayout.Height(row) * rowHeight;
            var selected = new List<int>(hitObjectMarqueeBase);
            double margin = Theme.Px(26) / pxPerMs;
            for (int i = referenceObjects.FirstVisible(from - margin); i < referenceObjects.Objects.Length && referenceObjects.Objects[i].Object.StartTime <= until + margin; i++)
            {
                var entry = referenceObjects.Objects[i];
                var (centre, radius) = HitObjectGeometry(entry, i, y, height);
                if (centre + radius < minY || centre - radius > maxY || entry.Object.EndTime < from - radius / pxPerMs) continue;
                selected.Add(entry.Index);
            }
            hitObjectSelection.Replace(selected);
        }
        else
        {
            if (!hitObjectMarqueeActive && hitObjectClickSeek is { } time && !app.Settings.OsuStyleTimelineIndicator) app.Seek(time);
            hitObjectMarqueePending = hitObjectMarqueeActive = false;
            hitObjectClickSeek = null;
        }
    }

    private void DrawHitObjectMarquee(ImDrawListPtr dl, float y, float height, float right)
    {
        if (!hitObjectMarqueeActive) return;
        var start = new Vector2(X(hitObjectMarqueeTime), hitObjectMarqueeStart.Y);
        var min = Vector2.Max(Vector2.Min(start, hitObjectMarqueeMouse), new(trackX, y));
        var max = Vector2.Min(Vector2.Max(start, hitObjectMarqueeMouse), new(right, y + height));
        if (max.X < min.X || max.Y < min.Y) return;
        dl.AddRectFilled(min, max, Theme.WithAlpha(Theme.Accent, .2f));
        dl.AddRect(min, max, Theme.Accent, 0, ImDrawFlags.None, Theme.Px(1));
    }
}
