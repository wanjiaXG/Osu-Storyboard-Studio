using System.Numerics;
using ImGuiNET;

namespace SbStudio.App.Ui;

/// <summary>A percentage control drawn like the player's seek bar.</summary>
internal static class ProgressSlider
{
    public static bool Percent(string id, ref float value, float width)
        => Range(id, ref value, 0, 100, width);

    /// <summary>A whole-number range, with its minimum and maximum at the track's endpoints.</summary>
    public static bool Range(string id, ref float value, float min, float max, float width)
    {
        Vector2 pos = ImGui.GetCursorScreenPos(), size = new(MathF.Max(1, width), ImGui.GetFrameHeight());
        ImGui.InvisibleButton(id, size);
        bool active = ImGui.IsItemActive(), hovered = ImGui.IsItemHovered();
        float previous = value;
        if (active)
        {
            float radius = Theme.Px(6), span = MathF.Max(1, size.X - radius * 2);
            float along = Math.Clamp((ImGui.GetIO().MousePos.X - pos.X - radius) / span, 0, 1);
            value = Math.Clamp(MathF.Round(min + along * (max - min)), min, max);
        }
        float fraction = float.IsFinite(value) ? (value - min) / (max - min) : 0;
        Theme.DrawProgressTrack(ImGui.GetWindowDrawList(), pos, size, fraction, active || hovered);
        return value != previous;
    }
}
