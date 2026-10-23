using System.Numerics;
using ImGuiNET;

namespace SbStudio.App.Ui;

/// <summary>Native slider interaction and text over a filled track and a contrasting handle.</summary>
internal static unsafe class FilledSlider
{
    public static bool Float(string label, ref float value, float min, float max, string format)
    {
        float width = ImGui.CalcItemWidth();
        var draw = ImGui.GetWindowDrawList();
        var layers = Begin(draw);
        bool changed = ImGui.SliderFloat(label, ref value, min, max, format);
        End(draw, layers, width, max > min ? (value - min) / (max - min) : 0);
        return changed;
    }

    public static bool Int(string label, ref int value, int min, int max, string format)
    {
        float width = ImGui.CalcItemWidth();
        var draw = ImGui.GetWindowDrawList();
        var layers = Begin(draw);
        bool changed = ImGui.SliderInt(label, ref value, min, max, format);
        End(draw, layers, width, max > min ? ((float)value - min) / ((float)max - min) : 0,
            MathF.Max(1, (float)max - min + 1));
        return changed;
    }

    private static ImDrawListSplitterPtr Begin(ImDrawListPtr draw)
    {
        // An independent splitter also works inside a table or another split draw list.
        var layers = new ImDrawListSplitterPtr(ImGuiNative.ImDrawListSplitter_ImDrawListSplitter());
        layers.Split(draw, 2);
        layers.SetCurrentChannel(draw, 1);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabMinSize, Theme.Px(12));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, 0u);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, 0u);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, 0u);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, 0u);
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, 0u);
        return layers;
    }

    private static void End(ImDrawListPtr draw, ImDrawListSplitterPtr layers, float width, float fraction, float steps = 0)
    {
        var style = ImGui.GetStyle();
        Vector2 min = ImGui.GetItemRectMin(), max = min + new Vector2(width, ImGui.GetFrameHeight());
        bool active = ImGui.IsItemActive(), hovered = ImGui.IsItemHovered();
        float usable = MathF.Max(0, width - 4);
        float grab = MathF.Min(usable, MathF.Max(style.GrabMinSize, steps > 0 ? usable / steps : 0));
        float t = float.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
        float x = min.X + 2 + grab / 2 + (usable - grab) * t;
        ImGui.PopStyleColor(5);
        ImGui.PopStyleVar();

        // Draw behind the native value text, leaving its formatting, focus and editing behaviour intact.
        layers.SetCurrentChannel(draw, 0);
        draw.AddRectFilled(min, max, ImGui.GetColorU32(active ? ImGuiCol.FrameBgActive :
            hovered ? ImGuiCol.FrameBgHovered : ImGuiCol.FrameBg), style.FrameRounding);
        if (t > 0)
        {
            uint blue = active ? Theme.Rgba(44, 114, 187) : hovered ? Theme.Rgba(41, 108, 178) : Theme.Rgba(35, 95, 157);
            draw.AddRectFilled(min, new Vector2(t >= 1 ? max.X : x, max.Y), Theme.WithAlpha(blue, style.Alpha),
                style.FrameRounding, t >= 1 ? ImDrawFlags.RoundCornersAll : ImDrawFlags.RoundCornersLeft);
        }
        draw.AddRect(min, max, ImGui.GetColorU32(hovered || active ? ImGuiCol.SliderGrab : ImGuiCol.Border), style.FrameRounding);
        if (grab > 0)
        {
            Vector2 handleMin = new(x - grab / 2, min.Y + 2), handleMax = new(x + grab / 2, max.Y - 2);
            float rounding = Theme.Px(3);
            draw.AddRectFilled(handleMin + new Vector2(0, 1), handleMax + new Vector2(0, 1),
                Theme.WithAlpha(Theme.Rgba(0, 0, 0, 110), style.Alpha), rounding);
            draw.AddRectFilled(handleMin, handleMax, Theme.WithAlpha(Theme.Rgba(23, 48, 76), style.Alpha), rounding);
            draw.AddRect(handleMin, handleMax,
                Theme.WithAlpha(active || hovered ? Theme.Rgba(255, 255, 255) : Theme.Rgba(220, 235, 250), style.Alpha),
                rounding, ImDrawFlags.RoundCornersAll, Theme.Px(1.5f));
            draw.AddLine(new Vector2(x, handleMin.Y + Theme.Px(5)), new Vector2(x, handleMax.Y - Theme.Px(5)),
                Theme.WithAlpha(Theme.Rgba(128, 183, 236), style.Alpha), Theme.Px(1));
        }
        layers.Merge(draw);
        layers.Destroy();
    }
}
