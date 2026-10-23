using System.Globalization;
using ImGuiNET;
using SbStudio.Core;

namespace SbStudio.App.Ui;

/// <summary>
/// Time fields. Dragging one moves the value from snap tick to snap tick. Clicking it lets a time be typed,
/// which is the only way to get one that is off the grid; typed text is checked like any other number.
/// </summary>
public static class TimeInput
{
    // Only one field can be dragged at a time, so one remembered start value is enough.
    private static double dragStart;

    /// <param name="offset">Absolute time of value 0, for fields that hold times relative to a loop or trigger.</param>
    /// <param name="min">Smallest value the field may hold (in its own, possibly relative, terms).</param>
    /// <param name="name">What the time is, for the message shown when typed input is rejected.</param>
    /// <param name="caption">What the field says it is inside its own box, ahead of the time: "Time", "Start"…</param>
    /// <returns>True when the value changed.</returns>
    public static bool Field(EditorApp app, string id, ref int value, float width, int offset = 0, string format = "%d",
        int min = int.MinValue, int max = int.MaxValue, string? name = null, string caption = "")
    {
        uint itemId = ImGui.GetID(id);
        if (TextEntry.IsEditing(itemId))
        {
            string? text = TextEntry.Draw(id, width);
            if (text == null) return false;
            // No limit on a side is "unbounded", so the message only mentions the limit that exists.
            var spec = new NumberSpec(name ?? L.T("value.time"), 1, "%d",
                min == int.MinValue ? double.NegativeInfinity : min,
                max == int.MaxValue ? double.PositiveInfinity : max,
                true, "ms");
            if (!NumberInput.TryAccept(app, text, value, spec, out double typed) || (int)typed == value) return false;
            value = (int)typed;
            return true;
        }

        ImGui.SetNextItemWidth(width);
        int shown = value;
        // The widget's own drag result is ignored: it would creep by single milliseconds.
        bool captioned = FieldCaption.Begin(caption);
        ImGui.DragInt(id, ref shown, 1f, int.MinValue / 4, int.MaxValue / 4, format, ImGuiSliderFlags.NoInput);
        FieldCaption.End(captioned);
        // Room is kept for a time of six digits, which is as long as the times of a map get.
        if (captioned) FieldCaption.Draw(caption, FieldCaption.Printf(format, value), FieldCaption.Printf(format, 888888));
        if (ImGui.IsItemActivated()) dragStart = value;
        bool dragging = ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 3f);
        TextEntry.TrackClick(itemId, value.ToString(CultureInfo.InvariantCulture));
        if (!dragging) return false;

        float pixels = ImGui.GetMouseDragDelta(ImGuiMouseButton.Left, 3f).X;
        // Dragging cannot produce an invalid time: it stops at the limits.
        int target = Math.Clamp(app.DragTime(dragStart + offset, pixels) - offset, min, max);
        if (target == value) return false;
        value = target;
        return true;
    }
}
