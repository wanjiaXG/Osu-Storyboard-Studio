using System.Numerics;
using SbStudio.Core;

namespace SbStudio.App.Gfx;

public sealed unsafe partial class HitObjectRenderer
{
    private readonly List<Texture> argonExtras = new();
    private Texture? argonOuterGradient, argonInnerGradient, argonHitFlash, argonGlow, argonBallBorder, argonBallIcon, argonFollowBorder, argonRepeatEdge, argonWhite, argonSpinnerTick;
    private Texture? argonSpinnerInner, argonSpinnerOuter;
    private ArgonCircleShape? argonShapes;
    private Vector2 argonChevronSize, argonBallIconSize, argonReverseIconSize;

    private Texture ArgonImage(OsuPreviewSkin.Bitmap image) => new(gl, image.Width, image.Height, image.Pixels);
    private Texture Extra(Texture texture) { argonExtras.Add(texture); return texture; }

    private Texture Ring(float thickness, float diameter = 128, bool verticalGradient = false) => Paint(512, (x, y, px) =>
    {
        float d = MathF.Sqrt(x * x + y * y);
        float alpha = Cover((1 - d) * px) * Cover((d - (1 - thickness * 2 / diameter)) * px);
        float shade = verticalGradient ? 1 - (y + 1) / 6 : 1;
        return new(shade, shade, shade, alpha);
    });

    private void EnsureArgonPictures()
    {
        circle = Paint(512, (x, y, px) => new(1, 1, 1, Cover((1 - MathF.Sqrt(x * x + y * y)) * px)));
        overlay = Ring(ArgonPreview.Border);
        approach = ArgonImage(ArgonPreviewAssets.Read("approachcircle"));
        ball = Paint(512, (x, y, px) =>
        {
            float shade = 1 - (y + 1) / 6; // accent -> accent.Darken(0.5)
            return new(shade, shade, shade, Cover((1 - MathF.Sqrt(x * x + y * y)) * px));
        });
        argonOuterGradient = Extra(Paint(512, (x, y, px) =>
        {
            float shade = 1 - (y + 1) / 22; // accent -> accent.Darken(0.1)
            return new(shade, shade, shade, Cover((1 - MathF.Sqrt(x * x + y * y)) * px));
        }));
        argonInnerGradient = Extra(Paint(512, (x, y, px) =>
        {
            float shade = 2f / 3 + (0.625f - 2f / 3) * (y + 1) / 2;
            return new(shade, shade, shade, Cover((1 - MathF.Sqrt(x * x + y * y)) * px));
        }));
        argonHitFlash = Extra(Paint(512, (x, y, _) => new(1, 1, 1,
            ArgonPreview.HitFlashCoverage(MathF.Sqrt(x * x + y * y) * ArgonPreview.FlashDiameter / 2))));
        argonGlow = Extra(Paint(512, (x, y, px) => new(1, 1, 1, MathF.Exp(-5 * (x * x + y * y)) * Cover((1 - MathF.Sqrt(x * x + y * y)) * px))));
        argonBallBorder = Extra(Ring(ArgonPreview.Gradient, ArgonPreview.OuterSize));
        followCircle = Paint(512, (x, y, px) =>
        {
            float shade = 1 - (y + 1) / 6;
            return new(shade, shade, shade, Cover((1 - MathF.Sqrt(x * x + y * y)) * px) * 0.3f);
        });
        argonFollowBorder = Extra(Ring(4, 128, true));
        tick = Ring(3, 12);
        var icons = ArgonPreviewAssets.ReadIcons();
        var chevron = icons[0].Image;
        for (int y = 0; y < chevron.Height; y++)
        {
            Vector3 shade = Vector3.Lerp(new(252, 97, 143), new(187, 26, 65), y / (float)Math.Max(1, chevron.Height - 1)) / 255;
            for (int x = 0; x < chevron.Width; x++)
            {
                int p = (y * chevron.Width + x) * 4;
                chevron.Pixels[p] = (byte)(chevron.Pixels[p] * shade.X);
                chevron.Pixels[p + 1] = (byte)(chevron.Pixels[p + 1] * shade.Y);
                chevron.Pixels[p + 2] = (byte)(chevron.Pixels[p + 2] * shade.Z);
            }
        }
        followPoint = ArgonImage(chevron);
        argonChevronSize = icons[0].Image.Size / MathF.Max(icons[0].Image.Width, icons[0].Image.Height) * 8;
        argonBallIcon = Extra(ArgonImage(icons[1].Image));
        argonBallIconSize = icons[1].Image.Size / MathF.Max(icons[1].Image.Width, icons[1].Image.Height) * new Vector2(48 * 0.6f, 48 * 0.8f);
        arrow = ArgonImage(icons[2].Image);
        argonReverseIconSize = icons[2].Image.Size / MathF.Max(icons[2].Image.Width, icons[2].Image.Height) * 16;
        argonRepeatEdge = Extra(ArgonImage(ArgonPreviewAssets.Read("repeat-edge-piece")));
        argonWhite = Extra(new Texture(gl, 1, 1, new byte[] { 255, 255, 255, 255 }));
        argonSpinnerInner = Extra(Ring(10, 32));
        argonSpinnerOuter = Extra(Ring(3, 40));
        argonSpinnerTick = Extra(Paint(256, (x, y, px) =>
        {
            float d = Vector2.Distance(new(x, y), new(Math.Clamp(x, -5f / 6, 5f / 6), 0));
            return new(1, 1, 1, Cover((1f / 6 - d) * px) * Cover((d - 1f / 30) * px));
        }));
    }

    private void DrawArgonCircle(OsuBeatmap map, double hit, Vector2 at, Vector3 colour, double time, double appear, double fadeIn, double dimUntil, int number, float whole, bool sliderHead)
    {
        if (time < appear || time >= hit + 800) return;
        var state = ArgonPreview.CircleHit(time - hit);
        float alpha = (float)Along(time, appear, fadeIn) * whole * state.ParentAlpha;
        float tint = Dim(time, dimUntil);
        float fills = state.FillAlpha;
        float size = map.Scale;
        var accent = colour * tint;
        if (!sliderHead) Sprite(circle!, at, new Vector2(127 * size), 0, accent / 5, alpha * fills);
        // The separated concentric fills and white border are ArgonMainCirclePiece's geometry.
        if (time < hit)
            Sprite(argonOuterGradient!, at, new Vector2(ArgonPreview.OuterSize * size), 0, accent, alpha);
        else
            argonShapes!.Draw(at, state.OuterDiameter * size, -1,
                new Vector4(Vector3.Lerp(accent, new Vector3(tint), state.OuterWhite), 1),
                new Vector4(Vector3.Lerp(accent / 1.1f, new Vector3(tint), state.OuterWhite), 1), alpha * state.OuterAlpha);
        Sprite(argonInnerGradient!, at, new Vector2(ArgonPreview.InnerSize * size), 0, accent, alpha * fills);
        Sprite(circle!, at, new Vector2(ArgonPreview.FillSize * size), 0, accent / 5, alpha * fills);
        if (number > 0)
        {
            float numberAlpha = alpha * state.NumberAlpha;
            DrawNumber(number, at + new Vector2(0, -2 * size), size, tint, numberAlpha);
        }
        if (time >= hit)
        {
            Sprite(argonHitFlash!, at, new Vector2(ArgonPreview.FlashDiameter * size), 0, accent, alpha * state.FlashAlpha, true);
            var border = ArgonPreview.HitBorderColours(colour, state.BorderProgress);
            border.Top *= new Vector4(tint, tint, tint, 1);
            border.Bottom *= new Vector4(tint, tint, tint, 1);
            argonShapes!.Draw(at, state.BorderDiameter * size, ArgonPreview.Border * size, border.Top, border.Bottom, alpha);
        }
        else Sprite(overlay!, at, new Vector2(128 * size), 0, new Vector3(tint), alpha);
    }

    private void DrawArgonBall(OsuBeatmap map, Vector2 at, Vector3 colour, float rotation, double elapsed, double afterEnd)
    {
        float alpha = (float)EaseOutQuint(Along(elapsed, 0, 200));
        if (afterEnd >= 0) alpha *= 1 - (float)EaseOutQuint(Along(afterEnd, 0, 50));
        Vector2 size = new(ArgonPreview.OuterSize * map.Scale);
        Sprite(ball!, at, size, 0, colour, alpha);
        Sprite(argonBallBorder!, at, size, 0, Vector3.One, alpha);
        float iconScale = (float)EaseOutElasticHalf(Along(elapsed, 0, 200));
        if (afterEnd >= 0) iconScale *= 1 - 0.1f * (float)EaseOutQuint(Along(afterEnd, 0, 200));
        Sprite(argonBallIcon!, at, argonBallIconSize * map.Scale * iconScale, rotation, Vector3.One, alpha);
    }

    private void DrawArgonFollowCircle(OsuBeatmap map, Slider slider, double time, Vector2 at)
    {
        double lastTick = double.NegativeInfinity;
        foreach (var tickPoint in slider.Ticks)
            if (tickPoint.Time <= time && tickPoint.Time < slider.EndTime) lastTick = Math.Max(lastTick, tickPoint.Time - slider.StartTime);
        for (int i = 1; i <= slider.RepeatCount; i++)
        {
            double elapsed = i * slider.SpanDuration;
            if (elapsed <= time - slider.StartTime && elapsed < slider.Duration) lastTick = Math.Max(lastTick, elapsed);
        }
        var state = ArgonPreview.FollowCircle(time - slider.StartTime, slider.Duration, lastTick);
        Vector2 size = new(128 * map.Scale * state.Scale);
        var colour = Colour(map, slider);
        Sprite(followCircle!, at, size, 0, colour, state.Alpha, true);
        Sprite(argonFollowBorder!, at, size, 0, colour, state.Alpha, true);
    }

    private void DrawArgonReverse(OsuBeatmap map, Vector2 at, Vector3 colour, double time, double appear, double hit, double hitDuration, float rotation, float whole)
    {
        float alpha = (float)Along(time, appear, 150) * whole * (1 - (float)EaseOut(Along(time, hit, hitDuration)));
        float phase = (float)((time - appear) % 300);
        float bounce = phase < 35 ? (float)EaseOut(phase / 35) : 1 - (float)EaseOut(Along(phase, 35, 250));
        float size = map.Scale * (time >= hit ? 1 + 0.5f * (float)EaseOut(Along(time, hit, hitDuration)) : 1);
        Vector2 side = new(MathF.Cos(rotation), MathF.Sin(rotation));
        Sprite(argonRepeatEdge!, at - side * (12 * bounce * size), new Vector2(ArgonPreview.OuterSize * size), rotation, Vector3.One, alpha);
        float pulse = time >= hit ? 1 : 1 + 0.3f * bounce;
        // The white oval behind the two chevrons is 40 x 20, not a legacy circular end cap.
        Sprite(circle!, at, new Vector2(40, 20) * size * pulse, rotation, Vector3.One, alpha);
        Sprite(arrow!, at, argonReverseIconSize * size * pulse, rotation, colour / 5, alpha);
    }

    private void DrawArgonFollowPoint(Vector2 at, float rotation, float scale, float alpha)
    {
        Vector2 direction = new(MathF.Cos(rotation), MathF.Sin(rotation));
        // ArgonFollowPoint: two ChevronRight glyphs, the second offset 4 px, additive pink gradient.
        Sprite(followPoint!, at - direction * (2 * scale), argonChevronSize * scale, rotation, new Vector3(0.2f), alpha, true);
        Sprite(followPoint!, at + direction * (2 * scale), argonChevronSize * scale, rotation, Vector3.One, alpha, true);
    }

    private void Arc(Vector2 centre, float radius, float begin, float end, float thickness, Vector3 colour, float alpha)
    {
        int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(end - begin) * radius / 5));
        for (int i = 0; i < steps; i++)
        {
            float a = begin + (end - begin) * i / steps, b = begin + (end - begin) * (i + 1) / steps;
            Vector2 from = centre + new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius;
            Vector2 to = centre + new Vector2(MathF.Cos(b), MathF.Sin(b)) * radius;
            Sprite(argonWhite!, (from + to) / 2, new Vector2(Vector2.Distance(from, to) + 0.5f, thickness), MathF.Atan2(to.Y - from.Y, to.X - from.X), colour, alpha);
        }
        foreach (float angle in new[] { begin, end })
            Sprite(circle!, centre + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius, new Vector2(thickness), 0, colour, alpha);
    }

    private void DrawArgonSpinner(OsuBeatmap map, Spinner spinner, double time)
    {
        double start = spinner.StartTime, end = spinner.EndTime;
        float alpha = (float)Along(time, start - map.TimeFadeIn, map.TimeFadeIn) * (1 - (float)Along(time, end, 240));
        if (alpha <= 0) return;
        double turns = Math.Clamp(time - start, 0, spinner.Duration) * AutoSpinsPerMinute / 60000;
        double needed = (int)(OsuBeatmap.DifficultyRange(map.OverallDifficulty, 90, 150, 225) / 60 * spinner.Duration / 1000 + 0.0001);
        float progress = needed <= 0 ? 1 : (float)Unit(turns / needed);
        double preempt = map.TimePreempt;
        float entry = time < start ? 0.2f * (float)EaseOutQuint(Along(time, start - preempt / 2, preempt / 4)) : 0.2f + 0.8f * (float)EaseOutQuint(Along(time, start, preempt / 2));
        float discScale = entry * (time >= end ? 1 + 0.2f * (float)EaseOut(Along(time, end, 320)) : 1);
        Vector2 centre = OsuBeatmap.ToStoryboard(new(256, 192));
        Vector3 pink = new(252 / 255f, 97 / 255f, 143 / 255f);
        Sprite(argonGlow!, centre, new Vector2(384 * discScale * (0.1f + 0.88f * progress)), 0, pink, alpha * (time >= start ? 0.4f : 0.2f));
        float arc = progress >= 1 ? 0.5f : 0.31f;
        // CircularProgress uses half the drawable diameter for InnerRadius's stroke width.
        float thickness = 368 * discScale * (progress >= 1 ? 0.044f : 0.02f) / 2;
        float radius = 184 * discScale - thickness / 2;
        Arc(centre, radius, -MathF.PI / 2 - arc * MathF.PI, -MathF.PI / 2 + arc * MathF.PI, thickness, Vector3.One, alpha);
        Arc(centre, radius, MathF.PI / 2 - arc * MathF.PI, MathF.PI / 2 + arc * MathF.PI, thickness, Vector3.One, alpha);
        if (progress < 1)
            for (int side = 0; side < 2; side++)
            {
                float angle = side * MathF.PI;
                float sideThickness = 384 * discScale * 0.12f / 2;
                float sideRadius = 192 * discScale - sideThickness / 2;
                Arc(centre, sideRadius, angle - 0.15f * MathF.PI, angle + 0.15f * MathF.PI, sideThickness, Vector3.One, alpha * 0.25f);
                Arc(centre, sideRadius, angle - 0.15f * progress * MathF.PI, angle + 0.15f * progress * MathF.PI, sideThickness, new(171 / 255f, 1, 1), alpha);
            }
        float rotation = (float)(turns * Math.PI * 2 + (time - start + preempt / 2) / (preempt + spinner.Duration) * (25 * spinner.Duration / 2000) * Math.PI / 180);
        for (int i = 0; i < 25; i++)
        {
            float angle = i * MathF.PI * 2 / 25 + rotation;
            Vector2 at = centre + new Vector2(MathF.Sin(angle), MathF.Cos(angle)) * (144 * discScale);
            Sprite(argonSpinnerTick!, at, new Vector2(30 * discScale), -angle - MathF.PI * 2 / 3, Vector3.One, alpha);
        }
        float centreScale = time < start ? 80 : 40;
        Sprite(argonSpinnerInner!, centre, new Vector2(centreScale * 0.64f), 0, Vector3.One, alpha);
        Sprite(argonSpinnerOuter!, centre, new Vector2(centreScale * 0.8f), 0, Vector3.One, alpha);
    }

    private void ReleaseArgonPictures()
    {
        foreach (var texture in argonExtras) texture.Dispose();
        argonExtras.Clear();
        argonOuterGradient = argonInnerGradient = argonHitFlash = argonGlow = argonBallBorder = argonBallIcon = argonFollowBorder = argonRepeatEdge = argonWhite = argonSpinnerTick = null;
        argonSpinnerInner = argonSpinnerOuter = null;
    }
}
