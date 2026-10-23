using System.Numerics;
using ImGuiNET;
using SbStudio.Core;

namespace SbStudio.App.Ui;

/// <summary>A floating action below the offset being typed, leaving keyboard focus in the field.</summary>
internal sealed class TimingOffsetPopup
{
    private uint fieldId;
    private TimingPoint? point;
    private int lastFrame;
    private bool editing;
    private Vector2 anchorMin, anchorMax;

    public void Reset()
    {
        fieldId = 0;
        point = null;
    }

    // Call immediately after the offset widget, within the row's ID scope.
    public void Track(TimingPoint target)
    {
        int frame = ImGui.GetFrameCount();
        if (frame - lastFrame > 1) Reset();
        uint id = ImGui.GetItemID();
        bool active = ImGui.IsItemActive() && ImGui.GetIO().WantTextInput;
        if (active)
        {
            fieldId = id;
            point = target;
        }
        if (fieldId != id || point != target) return;
        editing = active;
        anchorMin = ImGui.GetItemRectMin();
        anchorMax = ImGui.GetItemRectMax();
        lastFrame = frame;
    }

    // Draw outside the scrolling table so the action belongs to the containing modal.
    public bool Draw(double currentTime)
    {
        if (fieldId == 0 || point == null || lastFrame != ImGui.GetFrameCount()) return false;
        var viewport = ImGui.GetWindowViewport();
        float margin = Theme.Px(2), padding = Theme.Px(4);
        float width = Math.Max(anchorMax.X - anchorMin.X, ImGui.CalcTextSize(L.T("simulation.current")).X + padding * 4);
        float height = ImGui.GetFrameHeight() + padding * 2;
        float left = Math.Clamp(anchorMin.X, viewport.WorkPos.X, Math.Max(viewport.WorkPos.X, viewport.WorkPos.X + viewport.WorkSize.X - width));
        float top = anchorMax.Y + margin;
        if (top + height > viewport.WorkPos.Y + viewport.WorkSize.Y) top = anchorMin.Y - height - margin;
        Vector2 afterTable = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(new(left, top));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(padding));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, ImGui.GetStyle().PopupRounding);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, ImGui.GetStyle().Colors[(int)ImGuiCol.PopupBg]);
        bool changed = false;
        if (ImGui.BeginChild("##timingOffsetCurrentTime", new(width, height), ImGuiChildFlags.Border | ImGuiChildFlags.AlwaysUseWindowPadding,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNavFocus))
        {
            if (ImGui.Button(L.T("simulation.current"), new(width - padding * 2, 0)))
            {
                point.Offset = TimingPointEditing.CurrentOffset(currentTime);
                changed = true;
                Reset();
            }
            else if (!editing && !ImGui.IsItemActive() && !ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem))
            {
                Reset();
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
        ImGui.SetCursorScreenPos(afterTable);
        return changed;
    }
}
