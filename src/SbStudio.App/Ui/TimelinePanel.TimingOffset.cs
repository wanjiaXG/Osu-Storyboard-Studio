using System.Numerics;
using ImGuiNET;
using SbStudio.Core;

namespace SbStudio.App.Ui;

public sealed partial class TimelinePanel
{
    private TimingPoint? offsetAdjustedPoint;
    private Project? offsetAdjustedProject;
    private double offsetAdjustedTime;

    // A red line can move past a paused playhead. Keep editing that same line until
    // time/project/history changes, so repeated clicks cannot silently edit its neighbour.
    private TimingPoint? TimingControlPoint
    {
        get
        {
            if (offsetAdjustedPoint != null)
            {
                if (offsetAdjustedProject == app.Project && app.Time == offsetAdjustedTime && !app.PreviewingEdit
                    && app.Project?.Settings.Timing.Contains(offsetAdjustedPoint) == true) return offsetAdjustedPoint;
                offsetAdjustedPoint = null;
            }
            return app.CurrentTimingOffset;
        }
    }

    private void DrawTimingOffsetControls(ImDrawListPtr dl, Vector2 pos, Vector2 max, float textY, bool toolbar = false)
    {
        var point = TimingControlPoint;
        // In the toolbar the group starts right at its edge: the gap before it is the toolbar's group spacing.
        float size = ImGui.GetFrameHeight(), left = pos.X + (toolbar ? 0 : Theme.Px(54));
        if (!toolbar && max.X - pos.X >= Theme.Px(250))
        {
            // Same colour as the BPM row's label: dimmed only while the Timing lane is hidden.
            dl.AddText(new(left, textY), app.TimingLaneHidden ? Theme.TextDim : ImGui.GetColorU32(ImGuiCol.Text), L.T("timing.offsetControl"));
            left += ImGui.CalcTextSize("Timing").X + Theme.Px(6);
        }
        float right = max.X - size - (toolbar ? 0 : Theme.Px(8)), y = (pos.Y + max.Y - size) / 2;
        bool large = ImGui.GetIO().KeyShift;
        for (int direction = -1; direction <= 1; direction += 2)
        {
            ImGui.SetCursorScreenPos(new(direction < 0 ? left : right, y));
            bool enabled = !app.PreviewingEdit && app.Project != null
                && TimingPointEditing.OffsetAfterAdjustment(app.Project.Settings.Timing, point, direction, large) != null;
            ImGui.BeginDisabled(!enabled);
            if (ImGui.Button(direction < 0 ? "-##decreaseTimingOffset" : "+##increaseTimingOffset", new(size, size))
                && app.AdjustTimingOffset(point, direction, large))
            {
                offsetAdjustedPoint = point;
                offsetAdjustedProject = app.Project;
                offsetAdjustedTime = app.Time;
                rowsStale = true;
            }
            ImGui.EndDisabled();
            Theme.Tooltip(direction < 0 ? L.T("timing.adjustOffset.tip", "-1", "-5") : L.T("timing.adjustOffset.tip", "+1", "+5"));
        }
        float from = left + size + Theme.Px(4), to = Math.Max(from, right - Theme.Px(4));
        string text = point != null ? $"{point.Offset:0.###} ms" : "— ms";
        DrawTimingValue(dl, new(from, y), to - from, size, textY, ImGui.GetColorU32(ImGuiCol.Text), text, toolbar, TimingField.Offset);
    }
}
