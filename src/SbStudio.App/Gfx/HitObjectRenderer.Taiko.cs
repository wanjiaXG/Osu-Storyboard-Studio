using System.Numerics;
using SbStudio.Core;

namespace SbStudio.App.Gfx;

public sealed unsafe partial class HitObjectRenderer
{
    private Texture? taikoAngle;
    private Vector2 taikoAngleSize;

    private void DrawTaiko(OsuBeatmap map, double time, RenderTarget target, ViewTransform view, bool widescreen)
    {
        var field = RulesetPreviewLayout.TaikoField(widescreen);
        float left = field.Left, right = field.Right, top = field.Top, height = field.Height, y = field.Y, hitX = field.HitX, scale = field.Scale;
        bool legacy = !lazerStyle;
        argonShapes ??= new(gl, batch);
        argonShapes.SetView(target, view);
        argonManiaRectangles ??= new(gl, batch);
        argonManiaRectangles.SetView(target, view);
        if (taikoAngle == null)
        {
            var icon = ArgonPreviewAssets.ReadIcon(0xf104); // FontAwesome.Solid.AngleLeft
            taikoAngle = ArgonImage(icon.Image);
            taikoAngleSize = icon.Image.Size / MathF.Max(icon.Image.Size.X, icon.Image.Size.Y);
            taikoAngleSize.X *= 0.8f;
        }

        // BackgroundRight occupies the entire playfield, under the separate 180-wide left overlay.
        if (!LegacyComponent("taiko-bar-right") || !RulesetSprite("taiko-bar-right", new((left + right) / 2, y), new(right - left, height), Vector3.One))
            ModeRectangle(left, top, right - left, height, new(0, 0, 0, 0.7f));
        bool legacyDrum = LegacyComponent("taiko-bar-left") && RulesetImage("taiko-bar-left") is { };
        if (legacyDrum)
        {
            var image = RulesetImage("taiko-bar-left")!;
            // LegacyInputDrum preserves native sprite dimensions (no stretching across the band).
            ModeSprite(image.Texture, new(left + image.Size.X * scale / 2, top + image.Size.Y * scale / 2), image.Size * scale, 0, Vector3.One, 1);
        }
        else
        {
            float width = 180 * scale, diameter = width * 0.9f;
            ModeRectangle(left, top, width, height, new(0, 0, 0, 1));
            Vector2 drum = new(left + width / 2, y);
            argonShapes.Draw(drum, diameter, -1, new(0.2f, 0.2f, 0.2f, 1), new(0.2f, 0.2f, 0.2f, 1), 1);
            argonShapes.Draw(drum, diameter * 0.7f, -1, new(64 / 255f, 64 / 255f, 64 / 255f, 1), new(64 / 255f, 64 / 255f, 64 / 255f, 1), 1);
            ModeRectangle(drum.X - 3 * scale, drum.Y - diameter / 2, 6 * scale, diameter, new(38 / 255f, 38 / 255f, 38 / 255f, 1));
        }
        if (LegacyComponent("taikobigcircle") && RulesetImage("taikobigcircle") != null)
        {
            NativeRulesetSprite("approachcircle", new(hitX, y), scale * 0.83f, Vector3.One, alpha: 0.47f);
            NativeRulesetSprite("taikobigcircle", new(hitX, y), scale * 0.8f, Vector3.One, alpha: 0.22f);
        }
        else
        {
            float barHeight = height * (1 - 0.475f / 0.65f);
            ModeRectangle(hitX - 2 * scale, top, 4 * scale, barHeight - 4 * scale, Vector4.One);
            ModeRectangle(hitX - 2 * scale, top + height - barHeight + 4 * scale, 4 * scale, barHeight - 4 * scale, Vector4.One);
            ModeSprite(rulesetDisc!, new(hitX, y), new(height * 0.475f), 0, Vector3.One, 0.1f, true);
            ModeSprite(rulesetDisc!, new(hitX, y), new(height * 0.475f * 0.85f), 0, Vector3.One, 0.1f, true);
        }
        // DrawableBarLine tracks are two native pixels wide, aligned with the object's start-time velocity.
        ClipRuleset(target, view, new(hitX, top), new(right, top + height));
        foreach (var bar in map.BarLines)
        {
            float barX = field.X(map, time, bar.Time);
            if (barX < hitX || barX > right) continue;
            if (LegacyComponent("taiko-barline") && RulesetSprite("taiko-barline", new(barX, y), new(2 * scale, height * 0.88f), Vector3.One)) continue;
            ModeRectangle(barX - scale, top, 2 * scale, height, new(1, 1, 1, bar.Major ? 1 : 0.5f));
        }
        EndRulesetClip();
        // All notes are masked at the hit origin, not half a note before it. Hit animations are proxied above the mask.
        ClipRuleset(target, view, new(hitX, top), new(right, top + height));
        for (int i = map.Objects.Count - 1; i >= 0; i--)
        {
            var obj = map.Objects[i];
            if (obj is Spinner) continue;
            if (time >= obj.EndTime) continue;
            float x = field.X(map, time, obj.StartTime);
            float endX = field.X(map, time, obj.EndTime, obj.StartTime);
            if (x > right + height || endX < hitX) continue;
            bool strong = (obj.HitSound & 4) != 0;
            float diameter = height * 0.475f * (strong ? 1 / 0.65f : 1);
            if (obj is Slider roll)
            {
                bool legacyRoll = LegacyComponent("taiko-roll-middle") && RulesetImage("taiko-roll-middle") != null;
                x = MathF.Max(x, hitX);
                Vector3 gold = new(238 / 255f, 170 / 255f, 0);
                float length = MathF.Max(0, endX - x);
                if (legacyRoll)
                {
                    RulesetSprite("taiko-roll-middle", new(x + length / 2, y), new(length, diameter), gold);
                    if (RulesetImage("taiko-roll-end") is { } tail)
                    {
                        Vector2 size = tail.Size * (diameter / tail.Size.Y);
                        ModeSprite(tail.Texture, new(endX + size.X / 2, y), size, 0, gold, 1);
                    }
                }
                else
                {
                    Vector4 capsule = new(x - diameter / 2, y - diameter / 2, length + diameter, diameter);
                    argonManiaRectangles.Draw(capsule, new(0, 0, 0, 190 / 255f), new(0, 0, 0, 190 / 255f), diameter / 2);
                    argonManiaRectangles.Draw(capsule, new(241 / 255f, 161 / 255f, 0, 0.5f), new(167 / 255f, 111 / 255f, 0, 0.5f), diameter / 2, diameter * 20 / 70);
                    argonManiaRectangles.Draw(capsule, new(241 / 255f, 161 / 255f, 0, 1), new(167 / 255f, 111 / 255f, 0, 1), diameter / 2, diameter * 5 / 70);
                }
                double spacing = map.ScrollAt(roll.StartTime).BeatLength / (map.SliderTickRate == 3 ? 3 : 4);
                double first = roll.StartTime + Math.Max(0, Math.Ceiling((time - roll.StartTime) / spacing)) * spacing;
                for (double tick = first; tick < roll.EndTime + spacing / 2; tick += spacing)
                {
                    float tickX = field.X(map, time, tick, roll.StartTime);
                    if (tickX > right) break;
                    if (legacy && NativeRulesetSprite("sliderscorepoint", new(tickX, y), scale, Vector3.One)) continue;
                    float tickSize = height * 20 / 70 * (strong ? 1 / 0.65f : 1);
                    if (tick == roll.StartTime) ModeSprite(rulesetDisc!, new(tickX, y), new(tickSize), 0, Vector3.One, 1);
                    else ModeSprite(taikoAngle!, new(tickX, y), taikoAngleSize * tickSize, 0, Vector3.One, 1);
                }
                if (legacyRoll) Circle(obj, new(x, y), diameter, 1, true);
                continue;
            }
            Circle(obj, new(x, y), diameter, 1);
        }
        EndRulesetClip();

        // DrawableHit: 300 ms rise and 600 ms fall, easing Out/In; scale .8 over 600 ms and fade over 800 ms.
        foreach (var obj in map.Objects)
        {
            double age = time - obj.StartTime;
            if (obj is not HitCircle || age < 0 || age >= 800) continue;
            float t = (float)Math.Clamp(age / 300, 0, 1);
            float offset = age < 300 ? -200 * (1 - (1 - t) * (1 - t))
                : -200 + 600 * MathF.Pow((float)Math.Clamp((age - 300) / 600, 0, 1), 2);
            float shrink = 1 - 0.2f * (float)EaseOut(Along(age, 0, 600));
            float diameter = height * 0.475f * ((obj.HitSound & 4) != 0 ? 1 / 0.65f : 1) * shrink;
            Circle(obj, new(hitX, y + offset * scale), diameter, (float)(1 - age / 800));
        }
        foreach (var obj in map.Objects.OfType<Spinner>())
        {
            if (time < obj.StartTime - 600 || time > obj.EndTime + 200) continue;
            float progress = (float)Along(time, obj.StartTime, obj.Duration);
            float alpha = (float)(1 - Along(time, obj.EndTime, 200));
            if (time < obj.StartTime)
            {
                NativeRulesetSprite("spinner-warning", new(hitX, y), scale, Vector3.One, alpha: alpha);
                continue;
            }
            bool legacySwell = LegacyComponent("spinner-circle");
            Vector2 centre = legacySwell ? new(hitX + 250 * scale, y + 100 * scale) : new(hitX, y);
            if (!legacySwell || !NativeRulesetSprite("spinner-circle", centre, scale * 0.8f, Vector3.One, alpha: alpha))
            {
                argonShapes.Draw(centre, height * 0.85f, height * 5 / 70, new(1, 0.8f, 0, 1), new(1, 0.55f, 0, 1), alpha);
            }
            if (legacySwell) NativeRulesetSprite("spinner-approachcircle", centre, scale * 0.8f * 1.86f * (1 - progress), Vector3.One, alpha: alpha * 0.8f);
        }

        void Circle(HitObject obj, Vector2 position, float diameter, float alpha, bool forceLegacy = false)
        {
            bool strong = (obj.HitSound & 4) != 0, rim = (obj.HitSound & 10) != 0;
            Vector3 colour = obj is Slider ? new(238 / 255f, 170 / 255f, 0) : rim ? new(67 / 255f, 142 / 255f, 172 / 255f) : new(235 / 255f, 69 / 255f, 44 / 255f);
            string prefix = strong ? "taikobig" : "taikohit";
            if ((forceLegacy || legacy) && RulesetImage("taikohitcircle") != null)
            {
                if (!NativeRulesetSprite(prefix + "circle", position, diameter / 128, colour, alpha: alpha, maximum: 160))
                    NativeRulesetSprite("taikohitcircle", position, diameter / 128, colour, alpha: alpha, maximum: 160);
                if (!NativeRulesetSprite(prefix + "circleoverlay", position, diameter / 128, Vector3.One, alpha: alpha, maximum: 160))
                    NativeRulesetSprite("taikohitcircleoverlay", position, diameter / 128, Vector3.One, alpha: alpha, maximum: 160);
                return;
            }
            Vector4 upper = obj is Slider ? new(1, 0.8f, 0, 1) : rim ? new(0, 161 / 255f, 241 / 255f, 1) : new(241 / 255f, 0, 0, 1);
            Vector4 lower = obj is Slider ? new(1, 0.55f, 0, 1) : rim ? new(0, 111 / 255f, 167 / 255f, 1) : new(167 / 255f, 0, 0, 1);
            argonShapes.Draw(position, diameter, -1, new(0, 0, 0, 190 / 255f), new(0, 0, 0, 190 / 255f), alpha);
            argonShapes.Draw(position, diameter, diameter * 20 / 70, upper, lower, alpha * 0.5f);
            argonShapes.Draw(position, diameter, diameter * 5 / 70, upper, lower, alpha);
            ModeSprite(taikoAngle!, position, taikoAngleSize * (diameter * 20 / 70), 0, Vector3.One, alpha);
        }
    }
}
