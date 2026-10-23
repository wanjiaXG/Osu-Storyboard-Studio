using System.Numerics;
using ImGuiNET;
using SbStudio.App.Gfx;
using SbStudio.Core;

namespace SbStudio.App.Ui;

public sealed partial class TimelinePanel
{
    private readonly record struct LegacyMarker(double Time, HitObject Object, int Index, int Stack, bool Head, bool Repeat);
    private readonly List<LegacyMarker> legacyMarkers = new();

    private void DrawLegacyObjectTrack(ImDrawListPtr dl, float top, float height, float right, int first, int last)
    {
        var renderer = app.HitObjects;
        float scale = Theme.Scale, radius = LegacyTimelineStyle.BodyRadius(scale);
        float centre = LegacyTimelineStyle.Centre(top, height, scale);
        float bodyCentre = LegacyTimelineStyle.Centre(top, height, scale, body: true);
        legacyMarkers.Clear();
        // EditorModeCompose.DrawTimelineLow draws all duration strips before any sprites.
        for (int i = first; i <= last; i++)
        {
            var entry = referenceObjects.Objects[i];
            var obj = entry.Object;
            float start = X(obj.StartTime), end = X(obj.EndTime);
            if (end > start)
            {
                float fade = start > (trackX + right) / 2 ? LegacyTimelineStyle.EdgeOpacity(start, trackX, right, scale)
                    : end < (trackX + right) / 2 ? LegacyTimelineStyle.EdgeOpacity(end, trackX, right, scale) : 1;
                float left = Math.Max(start, trackX + radius), limit = Math.Min(end, right - radius);
                if (left <= limit) DrawLegacyBody(dl, left, limit, bodyCentre, radius, renderer.TimelineColour(referenceMap!, obj), fade);
            }
            // Stable offsets simultaneous heads only, rather than stacking all overlapping durations.
            int stack = 0;
            for (int previous = i - 1; previous >= 0 && referenceObjects.Objects[previous].Object.StartTime == obj.StartTime; previous--) stack++;
            legacyMarkers.Add(new(obj.StartTime, obj, entry.Index, stack, true, false));
            if (obj.EndTime > obj.StartTime) legacyMarkers.Add(new(obj.EndTime, obj, entry.Index, stack, false, false));
            if (obj is Slider slider)
                for (int repeat = 1; repeat < slider.SpanCount; repeat++)
                {
                    double time = slider.StartTime + repeat * slider.SpanDuration;
                    if (time >= TimeAt(trackX) && time <= TimeAt(right)) legacyMarkers.Add(new(time, obj, entry.Index, stack, false, true));
                }
        }
        // Stable orders heads, repeats and tails by their own time, independently of the parent object.
        legacyMarkers.Sort((a, b) => a.Time != b.Time ? b.Time.CompareTo(a.Time) : b.Index.CompareTo(a.Index));
        var circle = renderer.TimelineImage("hitcircle");
        var overlay = renderer.TimelineImage("hitcircleoverlay");
        var arrow = renderer.TimelineImage("reversearrow");
        Vector2 circleSize = LegacyTimelineStyle.ImageSize(renderer.TimelineImageSize("hitcircle"), scale);
        Vector2 overlaySize = LegacyTimelineStyle.ImageSize(renderer.TimelineImageSize("hitcircleoverlay"), scale);
        Vector2 arrowSize = LegacyTimelineStyle.ImageSize(renderer.TimelineImageSize("reversearrow"), scale);
        foreach (var marker in legacyMarkers)
        {
            float x = X(marker.Time), opacity = LegacyTimelineStyle.EdgeOpacity(x, trackX, right, scale);
            if (opacity <= 0) continue;
            Vector2 pos = new(x, centre - Theme.Px(2 * marker.Stack * LegacyTimelineStyle.DisplayScale));
            Vector3 tint = renderer.TimelineColour(referenceMap!, marker.Object);
            uint colour = ImGui.ColorConvertFloat4ToU32(new Vector4(tint, opacity));
            uint white = Theme.Rgba(255, 255, 255, (int)(opacity * 255));
            if (circle != null) TimelineImage(dl, circle, pos, circleSize, colour);
            else dl.AddCircleFilled(pos, Theme.Px(24), colour);
            // The editor always places the overlay before the number/arrow, independently of gameplay settings.
            if (overlay != null) TimelineImage(dl, overlay, pos, overlaySize, white);
            if (marker.Head)
            {
                Vector2 numberPos = new(x, centre - Theme.Px(3 * marker.Stack * LegacyTimelineStyle.DisplayScale));
                DrawTimelineNumber(dl, marker.Object.IndexInCurrentCombo + 1, numberPos, circleSize.X, white,
                    LegacyTimelineStyle.NumberScale * LegacyTimelineStyle.DisplayScale * scale);
            }
            else if (marker.Repeat && arrow != null) TimelineImage(dl, arrow, pos, arrowSize, white);
        }
        DrawObjectTicks(dl, top, height, right);
    }

    private static void DrawLegacyBody(ImDrawListPtr dl, float left, float right, float y, float radius, Vector3 tint, float fade)
    {
        uint edge = ImGui.ColorConvertFloat4ToU32(new Vector4(tint, LegacyTimelineStyle.BodyAlpha * fade));
        uint middle = ImGui.ColorConvertFloat4ToU32(new Vector4(tint, (170 - 30) / 255f * fade));
        dl.AddRectFilledMultiColor(new(left, y - radius), new(right, y), edge, edge, middle, middle);
        dl.AddRectFilledMultiColor(new(left, y), new(right, y + radius), middle, middle, edge, edge);
        var uv = ImGui.GetIO().Fonts.TexUvWhitePixel;
        // LineManager's Standard technique shades 15-segment round caps from alpha 170 to 140.
        for (int cap = 0; cap < 2; cap++)
        {
            Vector2 centre = new(cap == 0 ? left : right, y);
            float start = cap == 0 ? MathF.PI / 2 : -MathF.PI / 2;
            for (int i = 0; i < 15; i++)
            {
                float a = start + MathF.PI * i / 15, b = start + MathF.PI * (i + 1) / 15;
                uint Colour(float angle) => ImGui.ColorConvertFloat4ToU32(new Vector4(tint, (140 + 30 * Math.Abs(MathF.Sin(angle))) / 255f * fade));
                dl.PrimReserve(3, 3);
                dl.PrimVtx(centre, uv, middle);
                dl.PrimVtx(centre + new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius, uv, Colour(a));
                dl.PrimVtx(centre + new Vector2(MathF.Cos(b), MathF.Sin(b)) * radius, uv, Colour(b));
            }
        }
    }

    private void DrawObjectTicks(ImDrawListPtr dl, float top, float height, float right)
    {
        float bottom = top + height - 1;
        dl.AddLine(new(trackX, bottom), new(right, bottom), Theme.Rgba(255, 255, 255, 90));
        foreach (var tick in ticks)
        {
            float x = X(tick.Time), size = Theme.Px(tick.Measure ? 8 : tick.Divisor == 1 ? 5 : 3);
            dl.AddLine(new(x, bottom - size), new(x, bottom), Theme.SnapColor(tick.Divisor));
        }
    }
}
