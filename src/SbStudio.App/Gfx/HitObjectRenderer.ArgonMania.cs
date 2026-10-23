using System.Numerics;
using SbStudio.Core;

namespace SbStudio.App.Gfx;

public sealed unsafe partial class HitObjectRenderer
{
    private ArgonRectangleShape? argonManiaRectangles;
    private Texture? argonManiaArrow;
    private Vector2 argonManiaArrowSize;

    private void DrawArgonMania(OsuBeatmap map, double time, RenderTarget target, ViewTransform view, bool widescreen)
    {
        argonManiaRectangles ??= new(gl, batch);
        argonManiaRectangles.SetView(target, view);
        if (argonManiaArrow == null)
        {
            var icon = ArgonPreviewAssets.ReadIcon(0xf107); // FontAwesome.Solid.AngleDown
            argonManiaArrow = ArgonImage(icon.Image);
            argonManiaArrowSize = icon.Image.Size / MathF.Max(icon.Image.Width, icon.Image.Height) * (20 * ArgonManiaPreview.Scale);
            argonManiaArrowSize.Y *= 0.7f;
        }
        const float scale = ArgonManiaPreview.Scale, stageTop = ArgonManiaPreview.StageTop,
            stageBottom = ArgonManiaPreview.StageBottom, hit = ArgonManiaPreview.HitPosition,
            height = ArgonManiaPreview.NoteHeight, radius = ArgonManiaPreview.Radius;
        int keys = map.Columns;
        var x = new float[keys];
        var widths = new float[keys];
        var lastPress = new double[keys];
        var lastRelease = new double[keys];
        var pressing = new bool[keys];
        Array.Fill(lastPress, double.NegativeInfinity);
        Array.Fill(lastRelease, double.NegativeInfinity);
        float total = 0;
        for (int i = 0; i < keys; i++) total += ArgonManiaPreview.ColumnWidth(keys, i) + ArgonManiaPreview.ColumnMargin * 2;
        float fit = 1;
        float available = widescreen ? 854 : 640;
        int stages = keys > 10 ? 2 : 1, stageKeys = keys / stages;
        float stageWidth = total / stages;
        float left = 320 - (stages == 2 ? available / 4 : 0) - stageWidth / 2, cursor = left;
        foreach (var obj in map.Objects)
        {
            if (obj.StartTime > time) break;
            int column = map.ColumnOf(obj);
            lastPress[column] = Math.Max(lastPress[column], obj.StartTime);
            double released = obj.EndTime > obj.StartTime ? obj.EndTime : obj.StartTime + 80;
            if (time < released) pressing[column] = true;
            else lastRelease[column] = Math.Max(lastRelease[column], released);
        }
        for (int i = 0; i < keys; i++)
        {
            cursor += ArgonManiaPreview.ColumnMargin * fit;
            x[i] = cursor;
            widths[i] = ArgonManiaPreview.ColumnWidth(keys, i) * fit;
            Vector3 colour = ArgonManiaPreview.Colour(keys, i);
            // ArgonColumnBackground: Darken(3), opacity .8, with rounded corners.
            Fill(x[i], stageTop, widths[i], stageBottom - stageTop, new(colour / 4, 0.8f), radius: radius);
            float lighting = pressing[i] ? 0.5f + 0.5f * (float)(1 - Along(time, lastPress[i] + 50, 250))
                : 0.5f * (float)(1 - EaseOutQuint(Along(time, lastRelease[i], 250)));
            float half = (stageBottom - stageTop) / 2;
            Gradient(x[i], stageBottom - half, widths[i], half, new(colour, 0), new(colour, 0.6f * lighting), additive: true);
            DrawKeyArea(i, colour);
            cursor += widths[i] + ArgonManiaPreview.ColumnMargin * fit;
            if (stages == 2 && i == stageKeys - 1) cursor = 320 + available / 4 - stageWidth / 2;
        }

        float right = stages == 2 ? 320 + available / 4 + stageWidth / 2 : left + total;
        ClipRuleset(target, view, new(left, stageTop), new(right, hit));
        // Barlines use timing changes, never an inherited SV change as their beat origin.
        foreach (var bar in map.BarLines)
        {
            if (bar.Time < time) continue;
            float y = NoteY(bar.Time);
            if (y < stageTop) break;
            if (y <= hit)
                for (int stage = 0; stage < stages; stage++)
                    ModeRectangle(stage == 0 ? left : right - stageWidth, y, stageWidth, scale, new(1, 1, 1, 0.35f));
        }
        for (int index = map.Objects.Count - 1; index >= 0; index--)
        {
            var obj = map.Objects[index];
            if (time > obj.EndTime + 200 || obj.StartTime > time + map.TimePreempt * 12) continue;
            int column = map.ColumnOf(obj);
            float head = NoteY(obj.StartTime), tail = NoteY(obj.EndTime);
            if (MathF.Max(head, tail) < stageTop - height || MathF.Min(head, tail) > hit + height) continue;
            float alpha = time <= obj.EndTime ? 1 : (float)(1 - Along(time, obj.EndTime, 200));
            Vector3 accent = ArgonManiaPreview.Colour(keys, column);
            bool hold = obj.EndTime > obj.StartTime;
            bool holding = hold && time >= obj.StartTime && time < obj.EndTime;
            if (hold)
            {
                if (time >= obj.StartTime) head = hit;
                float bodyTop = tail - height / 2, bodyBottom = head - height / 2;
                // Full-width ArgonHoldBodyPiece, Darken(.6); a hitting layer pulses every 160 ms.
                Fill(x[column], bodyTop, widths[column], bodyBottom - bodyTop, new(accent / 1.6f, alpha), radius: radius);
                if (holding)
                {
                    float pulse = 0.75f + 0.25f * MathF.Cos((float)(time % 160 / 160 * Math.PI * 2));
                    Fill(x[column], bodyTop, widths[column], bodyBottom - bodyTop,
                        new(Vector3.Min(Vector3.One, accent * 1.2f), alpha * 0.3f * pulse), radius: radius);
                }
                DrawNote(column, tail, accent, alpha, true, true);
            }
            DrawNote(column, head, accent, alpha, hold, false);
        }
        EndRulesetClip();

        float NoteY(double noteTime) => hit - (float)(map.ScrollDistance(time, noteTime) / (map.TimePreempt * ArgonManiaPreview.ScrollRangeScale) * (hit - stageTop));

        void DrawKeyArea(int column, Vector3 colour)
        {
            float px = x[column], width = widths[column], centre = px + width / 2;
            float strength = pressing[column] ? 0.8f * (float)EaseOutQuint(Along(time, lastPress[column], 70))
                : 0.3f * (float)(1 - EaseOutQuint(Along(time, lastRelease[column], 800)));
            float areaTop = ArgonManiaPreview.KeyAreaTop;
            // ArgonHitTarget is a translucent white accent-height rectangle, above the key line.
            Fill(px, hit - ArgonManiaPreview.AccentHeight, width, ArgonManiaPreview.AccentHeight,
                new(1, 1, 1, 0.3f), radius: radius, additive: true);
            Fill(px, areaTop, width, stageBottom - areaTop, new(colour / 1.2f, strength), radius: radius);
            Vector3 line = Vector3.Lerp(new(196 / 255f), Vector3.One, strength);
            Fill(px, areaTop, width, radius * 2, new(line, 1), radius: radius);
            float iconY = areaTop + 30 * scale;
            float iconWidth = 22 * scale * (pressing[column] ? 0.9f : 1);
            float iconHeight = 14 * scale * (pressing[column] ? 0.9f : 1);
            Fill(centre - iconWidth / 2, iconY - iconHeight / 2, iconWidth, iconHeight, Vector4.One,
                radius: iconHeight / 2, border: 4 * scale);
            Vector4 dotColour = new(pressing[column] ? Vector3.One : colour, 1);
            float dotY = stageBottom - 28 * scale;
            Dot(centre, dotY);
            Dot(centre - 7 * scale, dotY + 7 * 1.2f * scale);
            Dot(centre + 7 * scale, dotY + 7 * 1.2f * scale);

            void Dot(float dx, float dy) => Fill(dx - 4 * scale, dy - 4 * scale, 8 * scale, 8 * scale, dotColour, radius: 4 * scale);
        }

        void DrawNote(int column, float y, Vector3 colour, float alpha, bool hold, bool tail)
        {
            float px = x[column], width = widths[column], top = y - height;
            // Black gradient behind the accent is part of the actual Argon note, not a stretched image.
            Gradient(px, top, width, height, new(0, 0, 0, 0), new(0, 0, 0, alpha), radius);
            float accentTop = y - ArgonManiaPreview.AccentHeight;
            if (tail)
            {
                Fill(px, accentTop, width, ArgonManiaPreview.AccentHeight, new(colour / 1.6f, alpha), radius: radius);
                Gradient(px, accentTop, width, ArgonManiaPreview.AccentHeight / 2,
                    new(colour, 0.4f * alpha), new(colour, 0), radius, additive: true);
                return;
            }
            Gradient(px, accentTop, width, ArgonManiaPreview.AccentHeight,
                new(Vector3.Min(Vector3.One, colour * 1.1f), alpha), new(colour, alpha), radius);
            Fill(px, y - radius * 2, width, radius * 2, new(1, 1, 1, alpha), radius: radius);
            Vector2 centre = new(px + width / 2, top + height / 2);
            if (hold)
                Fill(centre.X - 10 * scale, centre.Y + 2 * scale - 2.5f * scale, 20 * scale, 5 * scale,
                    new(1, 1, 1, alpha), radius: 2.5f * scale);
            else ModeSprite(argonManiaArrow!, centre + new Vector2(0, 4 * scale), argonManiaArrowSize, 0, Vector3.One, alpha);
        }

        void Fill(float px, float py, float width, float h, Vector4 colour, float radius = 0, float border = 0, bool additive = false) =>
            argonManiaRectangles.Draw(new(px, py, width, h), colour, colour, radius, border, additive);
        void Gradient(float px, float py, float width, float h, Vector4 top, Vector4 bottom, float radius = 0, bool additive = false) =>
            argonManiaRectangles.Draw(new(px, py, width, h), top, bottom, radius, additive: additive);
    }
}
