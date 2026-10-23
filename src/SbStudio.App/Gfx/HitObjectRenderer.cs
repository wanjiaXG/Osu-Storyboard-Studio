using System.Numerics;
using System.Runtime.InteropServices;
using SbStudio.Core;
using Silk.NET.OpenGL;

namespace SbStudio.App.Gfx;

/// <summary>One digit as the interface font draws it: how much of each pixel it covers, and its place on the line.</summary>
/// <param name="Coverage">One byte per pixel, rows from the top.</param>
/// <param name="Advance">How far the next digit stands to the right, in pixels.</param>
/// <param name="FontSize">The font size used to rasterize the glyph, including its font metrics rather than just its visible ink.</param>
public sealed record DigitGlyph(int Width, int Height, byte[] Coverage, float Advance, float FontSize);

/// <summary>
/// Draws the hit objects of a beatmap the way the game shows them at a moment, between the storyboard's
/// Foreground and Overlay layers, so that an Overlay can be judged against what it lies on.
///
/// Timing and geometry follow osu!lazer's legacy and Argon drawables, with every object hit exactly
/// on time. Stable and custom skins use legacy skin bitmaps; lazer uses Argon.
/// </summary>
public sealed unsafe partial class HitObjectRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BodyVertex
    {
        public Vector2 Position;
        /// <summary>0 on the line the slider follows, 1 at the rim of its body.</summary>
        public float Distance;
    }

    private const string BodyVertexSource = @"#version 330 core
layout(location = 0) in vec2 aPos;
layout(location = 1) in float aDist;
uniform vec4 uView;
out float vDist;
void main()
{
    vDist = aDist;
    // The nearer to the middle line the nearer to the eye: where pieces of the body lie over each other
    // the depth test keeps the one that is nearest to the line.
    gl_Position = vec4(aPos * uView.xy + uView.zw, aDist * 2.0 - 1.0, 1.0);
}";

    // The colours across the body as the classic skin has them: a faint shadow at the rim, the border, and
    // the track from its darker edge to its lighter middle.
    private const string BodyFragmentSource = @"#version 330 core
in float vDist;
uniform vec4 uOuter;
uniform vec4 uInner;
uniform vec4 uBorder;
uniform vec3 uTint;
uniform float uAlpha;
uniform int uArgon;
out vec4 oColor;
void main()
{
    const float shadowPortion = 0.078125;
    const float borderPortion = 0.1875;
    float p = 1.0 - vDist;
    float w = fwidth(p) * 0.75;
    if (uArgon == 1)
    {
        vec4 c = mix(uInner, uBorder, smoothstep(0.8 - w, 0.8 + w, vDist));
        oColor = vec4(c.rgb * uTint * c.a, c.a) * uAlpha * smoothstep(-w, w, p);
        return;
    }
    vec4 shadow = vec4(0.0, 0.0, 0.0, 0.25 * clamp(p / shadowPortion, 0.0, 1.0));
    vec4 track = mix(uOuter, uInner, clamp((p - borderPortion) / (1.0 - borderPortion), 0.0, 1.0));
    vec4 c = mix(shadow, uBorder, smoothstep(shadowPortion - w, shadowPortion + w, p));
    c = mix(c, track, smoothstep(borderPortion - w, borderPortion + w, p));
    oColor = vec4(c.rgb * uTint * c.a, c.a) * uAlpha;
}";

    /// <summary>How much darker an object is until shortly before it can be hit.</summary>
    private const float Dimmed = 195f / 255f;
    /// <summary>How long before its time an object can be hit at all; it stops being dimmed then.</summary>
    private const double MissWindow = 400;
    /// <summary>How long a hit circle takes to burst and fade once it is hit.</summary>
    private const double HitFade = 240;
    /// <summary>The radius of the classic skin's circle in a picture whose half is 64.</summary>
    private const float LegacyRadius = 59f / 64f;
    private const int FollowPointSpacing = 32;
    /// <summary>What autoplay spins a spinner at, in turns per minute.</summary>
    private const double AutoSpinsPerMinute = 477;

    private readonly GL gl;
    private readonly SpriteBatch batch;
    private readonly ShaderProgram bodyShader;
    private readonly uint bodyVao, bodyVbo;
    private readonly int viewLocation, outerLocation, innerLocation, borderLocation, tintLocation, alphaLocation, argonLocation;
    private BodyVertex[] bodyVertices = new BodyVertex[4096];
    private int bodyCount, bodyCapacity;

    private Texture? circle, overlay, approach, arrow, ball, ballHighlight, followCircle, tick, followPoint, spinnerDisc, spinnerGlow;
    private Texture[] digits = Array.Empty<Texture>();
    private DigitGlyph[] digitGlyphs = Array.Empty<DigitGlyph>();
    internal SkinLibrary SkinLibrary { get; } = new();
    private OsuPreviewSkin skin = new(SbStudio.Resources.ResourceAssets.DefaultSkin);
    private bool lazerStyle;
    public string SkinId { get; private set; } = "stable";
    private readonly Dictionary<Texture, Vector2> skinSizes = new();
    private readonly Dictionary<Texture, SkinSprite[]> skinAnimations = new();
    private double skinAnimationTime;
    private Vector2[] skinDigitSizes = Array.Empty<Vector2>();
    private sealed record SkinSprite(Texture Texture, Vector2 Size);
    private SkinSprite[] skinBallFrames = Array.Empty<SkinSprite>();
    private SkinSprite? skinBallBackground, skinBallHighlight;

    private OsuBeatmap? measured;
    private double longest;
    private readonly List<Vector2> curve = new();
    private readonly List<HitObject> visible = new();

    /// <summary>Gives the ten digits for the numbers on the circles; asked once, when the first number is drawn.</summary>
    public Func<DigitGlyph[]>? Digits;

    public HitObjectRenderer(GL gl, SpriteBatch batch)
    {
        this.gl = gl;
        this.batch = batch;
        bodyShader = new ShaderProgram(gl, BodyVertexSource, BodyFragmentSource);
        viewLocation = bodyShader.Uniform("uView");
        outerLocation = bodyShader.Uniform("uOuter");
        innerLocation = bodyShader.Uniform("uInner");
        borderLocation = bodyShader.Uniform("uBorder");
        tintLocation = bodyShader.Uniform("uTint");
        alphaLocation = bodyShader.Uniform("uAlpha");
        argonLocation = bodyShader.Uniform("uArgon");

        bodyVao = gl.GenVertexArray();
        bodyVbo = gl.GenBuffer();
        gl.BindVertexArray(bodyVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, bodyVbo);
        uint stride = (uint)sizeof(BodyVertex);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 1, VertexAttribPointerType.Float, false, stride, (void*)8);
        gl.BindVertexArray(0);
    }

    public void SelectSkin(string id)
    {
        if (id == "beatmap") id = "beatmap:" + SkinLibrary.BeatmapBase(id);
        beatmapSkinRequested = global::SbStudio.App.Gfx.SkinLibrary.IsBeatmapSkin(id) ? id : null;
        var selected = SkinLibrary.Find(id);
        if (beatmapSkinRequested != null && !global::SbStudio.App.Gfx.SkinLibrary.IsBeatmapSkin(selected.Id)) selected = SkinLibrary.Find(SkinLibrary.BeatmapBase(id));
        if (beatmapSkinRequested == null) SkinLibrary.BeatmapFallbackId = selected.Id;
        var next = SkinLibrary.CreateSkin(selected.Id);
        ReleaseTextures();
        skin = next;
        lazerStyle = selected.Id == "lazer" || global::SbStudio.App.Gfx.SkinLibrary.IsBeatmapSkin(selected.Id) && SkinLibrary.BeatmapBase(selected.Id) == "lazer"
            && !SkinLibrary.ProvidesLegacy(SkinLibrary.BeatmapMode, SkinLibrary.BeatmapKeys, selected.Id);
        // Catch's editor timeline uses osu! circles, independently of its gameplay fruit skin.
        timelineLazerStyle = SkinLibrary.BeatmapMode == 2
            ? selected.Id == "lazer" || global::SbStudio.App.Gfx.SkinLibrary.IsBeatmapSkin(selected.Id) && SkinLibrary.BeatmapBase(selected.Id) == "lazer"
                && !SkinLibrary.ProvidesLegacy(0, SkinLibrary.BeatmapKeys, selected.Id)
            : lazerStyle;
        SkinId = selected.Id;
    }

    private string? beatmapSkinRequested;
    private OsuBeatmap? skinBeatmap;

    public void SetBeatmapSkin(string? file, OsuBeatmap? map)
    {
        SkinLibrary.BeatmapMode = map?.Mode ?? 0;
        SkinLibrary.BeatmapKeys = map?.Columns ?? 4;
        bool changed = SkinLibrary.SetBeatmap(file);
        if (!changed && map == skinBeatmap) return;
        skinBeatmap = map;
        if (!changed) SkinLibrary.Refresh();
        string selected = beatmapSkinRequested ?? SkinId;
        SelectSkin(selected);
    }

    // ---------------------------------------------------------------- easing

    private static double Unit(double x) => double.IsNaN(x) ? 1 : Math.Clamp(x, 0, 1);

    /// <summary>How far along a stretch of time a moment is, from 0 to 1. A stretch without length is over at once.</summary>
    private static double Along(double time, double start, double duration) => duration <= 0 ? (time >= start ? 1 : 0) : Unit((time - start) / duration);

    private static double EaseOut(double x) => x * (2 - x);

    private static double EaseOutQuint(double x) => 1 - Math.Pow(1 - x, 5);

    private static double EaseOutElasticHalf(double x) =>
        Math.Pow(2, -10 * x) * Math.Sin((0.5 * x - 0.075) * (2 * Math.PI / 0.3)) + 1;

    // ---------------------------------------------------------------- drawing

    /// <summary>
    /// Draws the objects that are on screen at a time. The batch has been begun and is drawing into
    /// <paramref name="target"/>; it is left ready to go on.
    /// </summary>
    public void Draw(OsuBeatmap map, double time, RenderTarget target, ViewTransform view, bool widescreen = false)
    {
        if (map.Objects.Count == 0) return;
        if (map.Mode != 0)
        {
            DrawOtherRuleset(map, time, target, view, widescreen);
            return;
        }
        EnsureTextures();
        if (lazerStyle)
        {
            argonShapes ??= new(gl, batch);
            argonShapes.SetView(target, view);
        }
        if (measured != map)
        {
            measured = map;
            longest = 0;
            foreach (var obj in map.Objects) longest = Math.Max(longest, obj.EndTime - obj.StartTime);
        }

        // Whatever began to show by now and may not be gone yet. An object stays for as long as it lasts
        // and a little after, so nothing that began earlier than the longest of them ago is still there.
        int end = map.FirstAfter(time);
        double earliest = time - longest - 1000;
        int first = end;
        while (first > 0 && map.Objects[first - 1].StartTime >= earliest) first--;

        visible.Clear();
        for (int i = first; i < end; i++)
        {
            var obj = map.Objects[i];
            if (time <= obj.EndTime + 800) visible.Add(obj);
        }

        // From the bottom up: spinners, the dots between objects, the objects with the earliest on top,
        // and over everything the approach circles.
        foreach (var obj in visible)
            if (obj is Spinner spinner) DrawSpinner(map, spinner, time);

        DrawFollowPoints(map, time, first);

        for (int i = visible.Count - 1; i >= 0; i--)
        {
            switch (visible[i])
            {
                case HitCircle hitCircle:
                    DrawCircle(map, hitCircle.StartTime, Place(map, hitCircle, Vector2.Zero), Colour(map, hitCircle), time,
                        hitCircle.StartTime - map.TimePreempt, map.TimeFadeIn, hitCircle.StartTime, hitCircle.IndexInCurrentCombo + 1, 1);
                    break;
                case Slider slider:
                    DrawSlider(map, slider, time, target, view);
                    break;
            }
        }

        for (int i = visible.Count - 1; i >= 0; i--)
            if (visible[i] is not Spinner) DrawApproachCircle(map, visible[i], time);
    }

    private Vector3 Colour(OsuBeatmap map, HitObject obj)
    {
        return PreviewComboColours.Resolve(map, obj.ComboIndexWithOffsets);
    }

    /// <summary>Where a point of an object is in the storyboard, the object's place in a stack counted in.</summary>
    private static Vector2 Place(OsuBeatmap map, HitObject obj, Vector2 offset) =>
        OsuBeatmap.ToStoryboard(obj.Position + map.StackOffset(obj) + offset);

    /// <summary>Dimmed until 400 ms before the time the object is to be hit, then bright within 100 ms.</summary>
    private static float Dim(double time, double hitTime) =>
        Dimmed + (1 - Dimmed) * (float)Along(time, hitTime - MissWindow, 100);

    private void Sprite(Texture texture, Vector2 centre, float half, float rotation, Vector3 colour, float alpha, bool additive = false)
    {
        if (skinAnimations.TryGetValue(texture, out var frames))
            texture = frames[OsuPreviewSkin.AnimationFrame(skinAnimationTime, skin.AnimationRate, frames.Length)].Texture;
        Sprite(texture, centre, skinSizes.TryGetValue(texture, out var nativeSize) ? nativeSize * (half / 64) : new Vector2(half * 2), rotation, colour, alpha, additive);
    }

    private void Sprite(Texture texture, Vector2 centre, Vector2 size, float rotation, Vector3 colour, float alpha, bool additive = false, bool mirrorY = false)
    {
        if (alpha <= 0 || size.X <= 0 || size.Y <= 0) return;
        Span<Vector2> corners = stackalloc Vector2[4];
        float cos = MathF.Cos(rotation), sin = MathF.Sin(rotation);
        Vector2 across = new Vector2(cos, sin) * size.X / 2, down = new Vector2(-sin, cos) * size.Y / 2;
        corners[0] = centre - across - down;
        corners[1] = centre + across - down;
        corners[2] = centre + across + down;
        corners[3] = centre - across + down;
        batch.Draw(texture, corners, new Vector4(colour, Math.Min(1, alpha)), additive, mirrorY: mirrorY);
    }

    /// <summary>
    /// A circle with its ring: a hit circle, or the head, the far end or a turning point of a slider.
    /// </summary>
    /// <param name="hitTime">When it is hit and bursts.</param>
    /// <param name="dimUntil">The time whose approach ends the dimming: its own, or that of the slider it belongs to.</param>
    /// <param name="number">The number on it; 0 for none.</param>
    /// <param name="alpha">How visible whatever it is part of is as a whole.</param>
    private void DrawCircle(OsuBeatmap map, double hitTime, Vector2 centre, Vector3 colour, double time, double appear, double fadeIn, double dimUntil, int number, float alpha, bool sliderHead = false)
    {
        skinAnimationTime = time - appear;
        if (lazerStyle)
        {
            DrawArgonCircle(map, hitTime, centre, colour, time, appear, fadeIn, dimUntil, number, alpha, sliderHead);
            return;
        }
        if (time < appear) return;
        float half = OsuBeatmap.ObjectRadius * map.Scale;
        float tint = Dim(time, dimUntil);
        float piece = (float)Along(time, appear, fadeIn) * alpha, text = piece, scale = 1;
        if (time >= hitTime)
        {
            double burst = Along(time, hitTime, HitFade);
            piece *= (float)(1 - burst);
            scale = 1 + 0.4f * (float)EaseOut(burst);
            // The number does not grow with the rest; it is gone in a quarter of the time.
            text *= (float)(1 - Along(time, hitTime, HitFade / 4));
        }
        if (piece <= 0 && text <= 0) return;

        Sprite(circle!, centre, half * scale, 0, colour * tint, piece);
        if (!lazerStyle && !skin.OverlayAboveNumber) Sprite(overlay!, centre, half * scale, 0, new Vector3(tint), piece);
        if (number > 0) DrawNumber(number, centre, map.Scale, tint, text);
        if (lazerStyle || skin.OverlayAboveNumber) Sprite(overlay!, centre, half * scale, 0, new Vector3(tint), piece);
    }

    private void DrawNumber(int number, Vector2 centre, float scale, float tint, float alpha)
    {
        if (alpha <= 0 || digits.Length < 10) return;
        if (skinDigitSizes.Length == 10)
        {
            DrawSkinNumber(number, centre, scale, tint, alpha);
            return;
        }
        // Argon specifies a font size of 52, not a visible glyph height of 52. Keep the
        // font's proportions instead of stretching its cropped digits to that height.
        float unit = lazerStyle ? 52 * scale / digitGlyphs[0].FontSize : 41.6f * scale / digitGlyphs[0].Height;
        Span<int> shown = stackalloc int[10];
        int count = 0;
        for (int n = number; n > 0 && count < shown.Length; n /= 10) shown[count++] = n % 10;

        float width = 0;
        for (int i = 0; i < count; i++) width += digitGlyphs[shown[i]].Advance * unit;
        float x = centre.X - width / 2;
        Span<Vector2> corners = stackalloc Vector2[4];
        for (int i = count - 1; i >= 0; i--)
        {
            var glyph = digitGlyphs[shown[i]];
            // The picture is larger than the digit by the outline around it.
            float w = (glyph.Width + DigitOutline * 2) * unit, h = (glyph.Height + DigitOutline * 2) * unit;
            float left = x + (glyph.Advance * unit - w) / 2, top = centre.Y - h / 2;
            corners[0] = new Vector2(left, top);
            corners[1] = new Vector2(left + w, top);
            corners[2] = new Vector2(left + w, top + h);
            corners[3] = new Vector2(left, top + h);
            batch.Draw(digits[shown[i]], corners, new Vector4(tint, tint, tint, Math.Min(1, alpha)), false);
            x += glyph.Advance * unit;
        }
    }

    private void DrawSkinNumber(int number, Vector2 centre, float scale, float tint, float alpha)
    {
        // Legacy skins use their native bitmap dimensions, a blanket 0.8 scale and skin.ini overlap.
        float unit = 0.8f * scale;
        string text = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        float width = text.Sum(c => skinDigitSizes[c - '0'].X) - skin.NumberOverlap * (text.Length - 1);
        float height = text.Max(c => skinDigitSizes[c - '0'].Y);
        float x = centre.X - width * unit / 2, top = centre.Y - height * unit / 2;
        foreach (char c in text)
        {
            int digit = c - '0';
            Vector2 size = skinDigitSizes[digit] * unit;
            Sprite(digits[digit], new Vector2(x + size.X / 2, top + size.Y / 2), size, 0, new Vector3(tint), alpha);
            x += (skinDigitSizes[digit].X - skin.NumberOverlap) * unit;
        }
    }

    private void DrawApproachCircle(OsuBeatmap map, HitObject obj, double time)
    {
        double appear = obj.StartTime - map.TimePreempt;
        skinAnimationTime = time - appear;
        // Gone the moment the object is hit.
        if (time < appear || time >= obj.StartTime) return;
        float alpha = 0.9f * (float)Along(time, appear, Math.Min(map.TimeFadeIn * 2, map.TimePreempt));
        float scale = 4 - 3 * (float)Along(time, appear, map.TimePreempt);
        Sprite(approach!, Place(map, obj, Vector2.Zero), OsuBeatmap.ObjectRadius * map.Scale * scale * (lazerStyle ? 128 / 118f : 1), 0, Colour(map, obj), alpha);
    }

    // ---------------------------------------------------------------- sliders

    private void DrawSlider(OsuBeatmap map, Slider slider, double time, RenderTarget target, ViewTransform view)
    {
        double start = slider.StartTime, end = slider.EndTime, appear = start - map.TimePreempt;
        skinAnimationTime = time - appear;
        double span = slider.SpanDuration;
        var colour = Colour(map, slider);
        float tint = Dim(time, start);
        // The slider as a whole fades once its end has been reached.
        float whole = (float)(1 - Along(time, end, lazerStyle ? 800 : HitFade));
        if (whole <= 0) return;

        // The body grows out of the head while the slider appears, and in its last pass shrinks away behind the ball.
        double completion = time < start ? 0 : slider.Duration > 0 ? Unit((time - start) / slider.Duration) : 1;
        int spanIndex = slider.SpanAt(completion);
        double spanProgress = slider.ProgressAt(completion);
        double snakeStart = 0, snakeEnd = Along(time, appear, map.TimePreempt / 3);
        if (spanIndex >= slider.SpanCount - 1)
        {
            if (Math.Min(spanIndex, slider.SpanCount - 1) % 2 == 1) snakeEnd = spanProgress;
            else snakeStart = spanProgress;
        }
        if (snakeStart > snakeEnd) (snakeStart, snakeEnd) = (snakeEnd, snakeStart);
        slider.Path.GetPathToProgress(curve, snakeStart, snakeEnd);

        float bodyAlpha = (float)Along(time, appear, map.TimeFadeIn) * whole * (float)(1 - Along(time, end, 40));
        if (bodyAlpha > 0) DrawBody(map, slider, colour, tint, bodyAlpha, target, view);

        Vector2 curveStart = curve.Count > 0 ? curve[0] : Vector2.Zero, curveEnd = curve.Count > 0 ? curve[^1] : Vector2.Zero;

        // The circle at the far end, which is where the last pass ends: at the head again when the passes are even.
        int last = slider.SpanCount - 1;
        if (!lazerStyle)
        {
            Vector2 at = Place(map, slider, slider.Path.PositionAt(last % 2 == 0 ? 1 : 0));
            var (endAppear, endFade) = EndCircleTimes(map, slider, last);
            DrawCircle(map, end, at, colour, time, endAppear, endFade, start, 0, whole);
        }

        float half = OsuBeatmap.ObjectRadius * map.Scale;
        foreach (var point in slider.Ticks)
        {
            // Later passes show their points sooner, so that they are not missed.
            double tickPreempt = (point.Time - point.SpanStartTime) / 2 + (point.SpanIndex > 0 ? 200 : map.TimePreempt * 0.66f);
            double tickAppear = point.Time - tickPreempt;
            if (time < tickAppear) continue;
            float alpha = (float)Along(time, tickAppear, 150), scale = 0.5f + 0.5f * (float)EaseOutElasticHalf(Along(time, tickAppear, 600));
            if (time >= point.Time)
            {
                double gone = Along(time, point.Time, 150);
                alpha *= (float)(1 - EaseOutQuint(gone));
                scale *= 1 + 0.5f * (float)EaseOut(gone);
            }
            Sprite(tick!, Place(map, slider, slider.Path.PositionAt(point.PathProgress)), half * (lazerStyle ? 12f / 128 : skinSizes.ContainsKey(tick!) ? 1 : 0.125f) * scale, 0, lazerStyle ? colour * tint : new Vector3(tint), alpha * whole);
        }

        // The turning points, each with its arrow. The arrows lie over the circles of all of them.
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < slider.RepeatCount; i++)
            {
                double hit = start + (i + 1) * span;
                var (repeatAppear, repeatFade) = EndCircleTimes(map, slider, i);
                if (time < repeatAppear) continue;
                bool atEnd = i % 2 == 0;
                // While the body is still growing the arrow sits at the tip of what there is of it.
                Vector2 at = Place(map, slider, atEnd ? curveEnd : curveStart);
                double fade = Math.Min(300, span);
                float gone = time >= hit ? (float)(1 - EaseOut(Along(time, hit, fade))) : 1;
                if (gone <= 0) continue;

                if (pass == 0)
                {
                    if (!lazerStyle) DrawCircle(map, hit, at, colour, time, repeatAppear, repeatFade, start, 0, whole * gone);
                    continue;
                }

                if (lazerStyle)
                {
                    DrawArgonReverse(map, at, colour, time, repeatAppear, hit, fade, ArrowRotation(atEnd), whole);
                    continue;
                }

                float arrowAlpha = (float)Along(time, repeatAppear, i > 0 ? Math.Min(span, 150) : 150) * whole * gone;
                float arrowScale = time >= hit
                    ? 1 + 0.4f * (float)EaseOut(Along(time, hit, fade))
                    // Before it is hit it beats: large, and back to its size within 300 ms.
                    : 1.3f - 0.3f * (float)EaseOut((time - repeatAppear) % 300 / 300);
                // Black on a light circle, as the classic skin has it.
                bool dark = colour.X + colour.Y + colour.Z > 600 / 255f;
                Sprite(arrow!, at, half * arrowScale, ArrowRotation(atEnd), dark ? Vector3.Zero : new Vector3(tint), arrowAlpha);
            }
        }

        // The head is a hit circle of its own, hit when the slider begins.
        DrawCircle(map, start, Place(map, slider, Vector2.Zero), colour, time, appear, map.TimeFadeIn, start, slider.IndexInCurrentCombo + 1, whole, true);

        if (time >= start)
        {
            Vector2 ballAt = Place(map, slider, slider.CurvePositionAt(completion));
            DrawFollowCircle(map, slider, time, ballAt, whole);
            if (time < end + (lazerStyle ? 50 : 0))
            {
                // Its stripes run across the way it is going.
                double check = slider.Path.Distance > 0 ? 0.1 / slider.Path.Distance : 0;
                Vector2 heading = slider.CurvePositionAt(Math.Min(1, completion + check)) - slider.CurvePositionAt(Math.Min(1 - check, completion));
                float rotation = heading.LengthSquared() > 1e-8f ? MathF.Atan2(heading.Y, heading.X) : 0;
                if (lazerStyle)
                {
                    DrawArgonBall(map, ballAt, colour, rotation, time - start, time - end);
                }
                else if (skinBallFrames.Length > 0)
                {
                    if (skinBallBackground is { } background)
                        Sprite(background.Texture, ballAt, background.Size * map.Scale, 0, new Vector3(5 / 255f), 1);
                    var frame = skinBallFrames[OsuPreviewSkin.BallFrame(time - appear, slider.Velocity, skinBallFrames.Length)];
                    Sprite(frame.Texture, ballAt, frame.Size * map.Scale, rotation, skin.TintBall ? colour : skin.BallColour, 1);
                    if (skinBallHighlight is { } highlight)
                        Sprite(highlight.Texture, ballAt, highlight.Size * map.Scale, 0, Vector3.One, 1, true);
                }
                else
                {
                    Sprite(ball!, ballAt, half, rotation, colour, 1);
                    Sprite(ballHighlight!, ballAt, half, 0, Vector3.One, 1, true);
                }
            }
        }
    }

    /// <summary>
    /// When the circle at the end of a pass appears and how long it fades in for. That of the first pass
    /// comes with the slider, once the body has grown out to it; each later one appears the moment the one
    /// before it at the same end is hit.
    /// </summary>
    private static (double Appear, double FadeIn) EndCircleTimes(OsuBeatmap map, Slider slider, int index)
    {
        if (index > 0) return (slider.StartTime + (index + 1) * slider.SpanDuration - slider.SpanDuration * 2, 0);
        return (slider.StartTime - map.TimePreempt + map.TimePreempt / 3, map.TimeFadeIn);
    }

    /// <summary>The way an arrow points: from the tip of the body it sits on back along the body.</summary>
    private float ArrowRotation(bool atEnd)
    {
        if (curve.Count < 2) return 0;
        Vector2 at = atEnd ? curve[^1] : curve[0];
        for (int i = atEnd ? curve.Count - 1 : 0; i >= 0 && i < curve.Count; i += atEnd ? -1 : 1)
        {
            if (PathApproximator.AlmostEquals(curve[i], at)) continue;
            return MathF.Atan2(curve[i].Y - at.Y, curve[i].X - at.X);
        }
        return 0;
    }

    /// <summary>
    /// The ring around the ball. It opens as the slider begins, widens for a moment at every point that
    /// scores, and closes in again as the slider ends.
    /// </summary>
    private void DrawFollowCircle(OsuBeatmap map, Slider slider, double time, Vector2 at, float whole)
    {
        skinAnimationTime = time - slider.StartTime;
        if (lazerStyle)
        {
            DrawArgonFollowCircle(map, slider, time, at);
            return;
        }
        double start = slider.StartTime, end = slider.EndTime, length = Math.Max(0, end - start);
        float scale, alpha;
        if (time < end)
        {
            scale = 1 + (float)EaseOut(Along(time, start, Math.Min(180, length)));
            alpha = (float)Along(time, start, Math.Min(60, length));
            // The last point to score, if the ring was fully open by then.
            double pulse = double.NegativeInfinity;
            foreach (var point in slider.Ticks)
                if (point.Time <= time && point.Time > pulse) pulse = point.Time;
            for (int i = 0; i < slider.RepeatCount; i++)
            {
                double hit = start + (i + 1) * slider.SpanDuration;
                if (hit <= time && hit > pulse) pulse = hit;
            }
            if (pulse - start >= Math.Min(180, length) && time - pulse < 200) scale = 2.2f - 0.2f * (float)((time - pulse) / 200);
        }
        else
        {
            double closing = Along(time, end, 200);
            scale = 2 - 0.4f * (float)EaseOut(closing);
            alpha = (float)(1 - closing * closing);
        }
        // Legacy follow-circle bitmaps are twice the hit-circle resolution and are scaled down by half.
        float bitmapScale = skinSizes.ContainsKey(followCircle!) ? 0.5f : 1;
        Sprite(followCircle!, at, OsuBeatmap.ObjectRadius * map.Scale * scale * bitmapScale, 0, Vector3.One, alpha * whole);
    }

    // ---------------------------------------------------------------- slider body

    private void BodyPoint(Vector2 position, float distance)
    {
        if (bodyCount == bodyVertices.Length) Array.Resize(ref bodyVertices, bodyVertices.Length * 2);
        bodyVertices[bodyCount].Position = position;
        bodyVertices[bodyCount++].Distance = distance;
    }

    /// <summary>Part of a disc around a point, as a cone that rises to the point: its middle is 0, its rim 1.</summary>
    private void BodyFan(Vector2 centre, float radius, float from, float to)
    {
        if (to < from) (from, to) = (to, from);
        int steps = Math.Max(1, (int)MathF.Ceiling((to - from) / (MathF.PI / 24)));
        Vector2 previous = centre + new Vector2(MathF.Cos(from), MathF.Sin(from)) * radius;
        for (int i = 1; i <= steps; i++)
        {
            float angle = from + (to - from) * i / steps;
            Vector2 next = centre + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
            BodyPoint(centre, 0);
            BodyPoint(previous, 1);
            BodyPoint(next, 1);
            previous = next;
        }
    }

    /// <summary>
    /// The body of a slider along <see cref="curve"/>: a ridge along every stretch of the line and a cone at
    /// every corner and at both ends. Where they lie over each other, as they do all along, the depth test
    /// lets the one that is nearest to the line through. So each pixel is drawn once, in the colour of its
    /// distance from the line, however the slider winds over itself, and the see-through body does not get
    /// darker where it does.
    /// </summary>
    private void DrawBody(OsuBeatmap map, Slider slider, Vector3 colour, float tint, float alpha, RenderTarget target, ViewTransform view)
    {
        if (curve.Count == 0) return;
        float radius = (lazerStyle ? ArgonPreview.OuterSize / 2 : OsuBeatmap.ObjectRadius) * map.Scale;
        bodyCount = 0;

        Vector2 previous = Place(map, slider, curve[0]);
        float previousAngle = float.NaN;
        bool any = false;
        for (int i = 1; i < curve.Count; i++)
        {
            Vector2 next = Place(map, slider, curve[i]);
            Vector2 along = next - previous;
            if (along.LengthSquared() < 1e-6f) continue;
            float angle = MathF.Atan2(along.Y, along.X);
            Vector2 side = new Vector2(-along.Y, along.X) / along.Length() * radius;

            for (int s = -1; s <= 1; s += 2)
            {
                BodyPoint(previous, 0);
                BodyPoint(next, 0);
                BodyPoint(next + side * s, 1);
                BodyPoint(previous, 0);
                BodyPoint(next + side * s, 1);
                BodyPoint(previous + side * s, 1);
            }

            if (float.IsNaN(previousAngle))
            {
                // The round end behind the first stretch.
                BodyFan(previous, radius, angle + MathF.PI / 2, angle + MathF.PI * 1.5f);
            }
            else
            {
                // At a corner the ridges leave a gap on the outside of the turn; on the inside they overlap.
                // Filling both sides needs no telling which is which.
                float turn = angle - previousAngle;
                while (turn > MathF.PI) turn -= MathF.PI * 2;
                while (turn < -MathF.PI) turn += MathF.PI * 2;
                BodyFan(previous, radius, previousAngle + MathF.PI / 2, previousAngle + MathF.PI / 2 + turn);
                BodyFan(previous, radius, previousAngle - MathF.PI / 2, previousAngle - MathF.PI / 2 + turn);
            }
            previous = next;
            previousAngle = angle;
            any = true;
        }
        // The round end ahead of the last stretch; a body without length is all round.
        if (any) BodyFan(previous, radius, previousAngle - MathF.PI / 2, previousAngle + MathF.PI / 2);
        else BodyFan(previous, radius, 0, MathF.PI * 2);

        batch.Suspend();
        gl.UseProgram(bodyShader.Handle);
        gl.Uniform4(viewLocation,
            view.Scale * 2f / target.Width, -view.Scale * 2f / target.Height,
            view.Offset.X * 2f / target.Width - 1f, 1f - view.Offset.Y * 2f / target.Height);
        // The track is the combo's colour, seven tenths opaque: a little darker at its edge and lighter in the middle.
        // A beatmap may set one colour for all tracks, and one for the border.
        if (map.SliderTrackOverride is { } track) colour = new Vector3(track.R, track.G, track.B) / 255f;
        Vector3 outer = colour / 1.1f, inner = Vector3.Min(Vector3.One, colour * 1.125f + new Vector3(0.25f));
        Vector3 border = map.SliderBorder is { } set ? new Vector3(set.R, set.G, set.B) / 255f : Vector3.One;
        gl.Uniform4(outerLocation, outer.X, outer.Y, outer.Z, 0.7f);
        gl.Uniform4(innerLocation, inner.X, inner.Y, inner.Z, 0.7f);
        gl.Uniform4(borderLocation, border.X, border.Y, border.Z, 1f);
        gl.Uniform1(argonLocation, lazerStyle ? 1 : 0);
        if (lazerStyle)
        {
            var accent = Colour(map, slider);
            gl.Uniform4(innerLocation, accent.X / 5, accent.Y / 5, accent.Z / 5, 0.98f);
            gl.Uniform4(borderLocation, accent.X, accent.Y, accent.Z, 0.98f);
        }
        gl.Uniform3(tintLocation, tint, tint, tint);
        gl.Uniform1(alphaLocation, alpha);

        gl.BindVertexArray(bodyVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, bodyVbo);
        if (bodyCount > bodyCapacity)
        {
            bodyCapacity = bodyVertices.Length;
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(bodyCapacity * sizeof(BodyVertex)), null, BufferUsageARB.StreamDraw);
        }
        fixed (BodyVertex* p = bodyVertices)
            gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(bodyCount * sizeof(BodyVertex)), p);

        // First where the body is and how near the line each pixel is, then the colour of exactly that.
        gl.Enable(EnableCap.DepthTest);
        gl.DepthMask(true);
        gl.ClearDepth(1);
        gl.Clear(ClearBufferMask.DepthBufferBit);
        gl.DepthFunc(DepthFunction.Less);
        gl.ColorMask(false, false, false, false);
        gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)bodyCount);

        gl.ColorMask(true, true, true, true);
        gl.DepthMask(false);
        gl.DepthFunc(DepthFunction.Equal);
        gl.BlendFuncSeparate(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha, BlendingFactor.Zero, BlendingFactor.One);
        gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)bodyCount);

        gl.DepthFunc(DepthFunction.Less);
        gl.DepthMask(true);
        gl.Disable(EnableCap.DepthTest);
        batch.Resume();
    }

    // ---------------------------------------------------------------- follow points

    /// <summary>The dots that lead from one object to the next of the same combo.</summary>
    private void DrawFollowPoints(OsuBeatmap map, double time, int first)
    {
        double preempt = 800 * Math.Min(1, map.TimePreempt / 450);
        for (int i = Math.Max(0, first - 1); i + 1 < map.Objects.Count; i++)
        {
            HitObject from = map.Objects[i], to = map.Objects[i + 1];
            // Later pairs begin later still.
            if (from.StartTime - preempt > time) break;
            if (to.NewCombo || from is Spinner || to is Spinner) continue;
            double startTime = from.EndTime, duration = to.StartTime - startTime;
            if (time < startTime - preempt || time > to.StartTime + 400) continue;

            Vector2 startPosition = from.EndPosition + map.StackOffset(from), endPosition = to.Position + map.StackOffset(to);
            Vector2 distanceVector = endPosition - startPosition;
            int distance = (int)distanceVector.Length();
            for (int d = (int)(FollowPointSpacing * 1.5); d < distance - FollowPointSpacing; d += FollowPointSpacing)
            {
                float fraction = (float)d / distance;
                double fadeOut = startTime + fraction * duration, fadeIn = fadeOut - preempt;
                if (time < fadeIn || time > fadeOut + 400) continue;
                // Each dot slides the last tenth of the way into its place while it appears.
                double arriving = Along(time, fadeIn, 400);
                float moved = (float)EaseOut(arriving);
                Vector2 at = startPosition + (fraction - 0.1f + 0.1f * moved) * distanceVector;
                float alpha = (float)(arriving * (1 - Along(time, fadeOut, 400)));
                float scale = map.Scale * (1.5f - 0.5f * moved);
                skinAnimationTime = time - fadeIn;
                if (lazerStyle) DrawArgonFollowPoint(OsuBeatmap.ToStoryboard(at), ArgonPreview.ConnectionRotation(startPosition, endPosition), scale, alpha);
                else Sprite(followPoint!, OsuBeatmap.ToStoryboard(at), (skinSizes.ContainsKey(followPoint!) ? 64 : 12) * scale, ArgonPreview.ConnectionRotation(startPosition, endPosition), Vector3.One, alpha);
            }
        }
    }

    // ---------------------------------------------------------------- spinner

    private void DrawSpinner(OsuBeatmap map, Spinner spinner, double time)
    {
        if (lazerStyle) { DrawArgonSpinner(map, spinner, time); return; }
        double start = spinner.StartTime, end = spinner.EndTime;
        float alpha = (float)(Along(time, start - map.TimeFadeIn, map.TimeFadeIn) * (1 - Along(time, end, HitFade)));
        if (alpha <= 0) return;

        // Autoplay spins it at a steady rate from the moment it begins. How far it has got decides how
        // large it is and how much it glows.
        double turns = Math.Clamp(time - start, 0, spinner.Duration) * AutoSpinsPerMinute / 60000;
        double needed = (int)(OsuBeatmap.DifficultyRange(map.OverallDifficulty, 90, 150, 225) / 60 * spinner.Duration / 1000 + 0.0001);
        float progress = needed <= 0 ? 1 : (float)Unit(turns / needed);
        float rotation = (float)(turns * Math.PI * 2);
        Vector2 centre = OsuBeatmap.ToStoryboard(new Vector2(256, 192));
        float half = 210 * (0.8f + 0.2f * (float)EaseOut(progress));

        Sprite(spinnerGlow!, centre, half * 1.25f, 0, new Vector3(3 / 255f, 151 / 255f, 1), alpha * progress, true);
        // The disc turns at half the speed of the mouse, as the skin's does.
        Sprite(spinnerDisc!, centre, half, rotation / 2, Vector3.One, alpha);
        // The dot in the middle goes from white to red as the time runs out.
        float left = 1 - (float)Along(time, start, spinner.Duration);
        Sprite(tick!, centre, half * 0.06f, 0, new Vector3(1, left, left), alpha);
    }

    // ---------------------------------------------------------------- pictures

    private void EnsureTextures()
    {
        if (circle != null) return;
        if (lazerStyle)
        {
            EnsureArgonPictures();
            // Use our own font atlas, independently of installed games.
            EnsureFallbackDigits();
            return;
        }
        const float r = LegacyRadius;

        // The disc that takes the colour of the combo: lighter in the middle, with a faint shadow around it.
        circle = Paint(512, (x, y, px) =>
        {
            float d = MathF.Sqrt(x * x + y * y);
            float inside = Cover((r - d) * px);
            float shade = 1 - 0.24f * (d / r) * (d / r);
            float shadow = d > r ? 0.22f * MathF.Pow(Math.Clamp(1 - (d - r) / (1 - r), 0, 1), 2) : 0;
            return Over(new Vector4(shade, shade, shade, inside), new Vector4(0, 0, 0, shadow));
        });

        // The white ring over it, with a touch of light on the upper half of the disc.
        overlay = Paint(512, (x, y, px) =>
        {
            float d = MathF.Sqrt(x * x + y * y);
            float ring = Cover((r - d) * px) * Cover((d - (r - 0.115f)) * px);
            float gloss = 0.16f * Cover((r - 0.115f - d) * px) * Math.Clamp(-y / r * 0.9f + 0.15f, 0, 1) * Math.Clamp(d / r, 0, 1);
            return Over(new Vector4(1, 1, 1, ring), new Vector4(1, 1, 1, gloss));
        });

        approach = Paint(512, (x, y, px) =>
        {
            float d = MathF.Sqrt(x * x + y * y);
            return new Vector4(1, 1, 1, Cover((0.985f - d) * px) * Cover((d - 0.93f) * px));
        });

        // An arrow pointing right, which is the way an angle of 0 points.
        arrow = Paint(256, (x, y, px) =>
        {
            float shaft = Cover((0.1f - MathF.Abs(y)) * px) * Cover((x + 0.4f) * px) * Cover((0.08f - x) * px);
            // The head: a triangle from x = 0 to its tip at 0.44.
            float head = Cover((x - 0.02f) * px) * Cover(((0.44f - x) * 0.72f - MathF.Abs(y)) * px * 0.81f);
            return new Vector4(1, 1, 1, Math.Max(shaft, head));
        });

        // The ball: a dark disc with stripes in the colour of the combo across the way it rolls.
        ball = Paint(256, (x, y, px) =>
        {
            float d = MathF.Sqrt(x * x + y * y);
            float inside = Cover((r - d) * px);
            float stripe = 0.5f + 0.5f * MathF.Cos(x * MathF.PI * 4.5f);
            float ring = Cover((d - (r - 0.1f)) * px);
            float light = Math.Max(ring, 0.35f + 0.65f * stripe * stripe);
            return new Vector4(light, light, light, inside * Math.Max(ring, 0.55f + 0.4f * stripe));
        });
        ballHighlight = Paint(256, (x, y, px) =>
        {
            float d = MathF.Sqrt((x + 0.28f) * (x + 0.28f) + (y + 0.32f) * (y + 0.32f));
            float inside = Cover((r - MathF.Sqrt(x * x + y * y)) * px);
            return new Vector4(1, 1, 1, 0.3f * inside * MathF.Pow(Math.Clamp(1 - d / 0.7f, 0, 1), 2));
        });

        // The ring that goes with the ball, in the orange of the classic skin, the disc inside it faintly filled.
        followCircle = Paint(512, (x, y, px) =>
        {
            float d = MathF.Sqrt(x * x + y * y);
            float ring = Cover((0.93f - d) * px) * Cover((d - 0.885f) * px);
            float fill = 0.12f * Cover((0.885f - d) * px);
            float glow = d > 0.93f ? 0.35f * MathF.Pow(Math.Clamp(1 - (d - 0.93f) / 0.07f, 0, 1), 2) : 0;
            return new Vector4(1, 0.72f, 0.25f, Math.Max(ring, Math.Max(fill, glow)));
        });

        tick = Paint(64, (x, y, px) => new Vector4(1, 1, 1, Cover((0.9f - MathF.Sqrt(x * x + y * y)) * px)));

        followPoint = Paint(64, (x, y, px) =>
        {
            float d = MathF.Sqrt(x * x + y * y);
            return new Vector4(1, 1, 1, 0.75f * MathF.Pow(Math.Clamp(1 - d / 0.9f, 0, 1), 1.5f) + 0.25f * Cover((0.33f - d) * px));
        });

        // The spinner: a dark disc with a white rim and marks around it that show it turning.
        spinnerDisc = Paint(1024, (x, y, px) =>
        {
            float d = MathF.Sqrt(x * x + y * y), angle = MathF.Atan2(y, x);
            float rim = Cover((0.98f - d) * px) * Cover((d - 0.955f) * px);
            float disc = 0.38f * Cover((0.955f - d) * px);
            // Twelve marks around the rim, every third one longer.
            float sector = angle / (MathF.PI * 2) * 12, nearest = MathF.Round(sector);
            bool major = (int)MathF.Abs(nearest) % 3 == 0;
            float across = MathF.Abs(sector - nearest) / 12 * MathF.PI * 2 * d;
            float mark = Cover(((major ? 0.016f : 0.009f) - across) * px) * Cover((0.955f - d) * px) * Cover((d - (major ? 0.62f : 0.8f)) * px);
            float inner = Cover((0.22f - d) * px) * Cover((d - 0.2f) * px);
            float white = Math.Max(rim, Math.Max(mark * 0.9f, inner * 0.8f));
            return Over(new Vector4(1, 1, 1, white), new Vector4(0.02f, 0.03f, 0.06f, disc));
        });
        spinnerGlow = Paint(256, (x, y, px) =>
        {
            float d = MathF.Sqrt(x * x + y * y);
            return new Vector4(1, 1, 1, 0.6f * MathF.Pow(Math.Clamp(1 - MathF.Abs(d - 0.74f) / 0.26f, 0, 1), 2));
        });

        if (!lazerStyle)
        {
            ReplaceSkinTexture(ref circle, "hitcircle");
            ReplaceSkinTexture(ref overlay, "hitcircleoverlay");
            ReplaceSkinTexture(ref approach, "approachcircle");
            ReplaceSkinTexture(ref arrow, "reversearrow");
            ReplaceSkinTexture(ref followCircle, "sliderfollowcircle");
            ReplaceSkinTexture(ref tick, "sliderscorepoint");
            ReplaceSkinTexture(ref followPoint, "followpoint");
            skinBallFrames = skin.ReadAnimation("sliderb", skin.BallFrames).Select(CreateSkinSprite).ToArray();
            skinBallBackground = LoadSkinSprite("sliderb-nd");
            skinBallHighlight = LoadSkinSprite("sliderb-spec");
        }

        var fontImages = Enumerable.Range(0, 10).Select(skin.ReadDigit).ToArray();
        if (!lazerStyle && fontImages.All(image => image != null))
        {
            skinDigitSizes = fontImages.Select(image => image!.Size).ToArray();
            digits = fontImages.Select(image => new Texture(gl, image!.Width, image.Height, image.Pixels)).ToArray();
            return;
        }
        EnsureFallbackDigits();
    }

    private void EnsureFallbackDigits()
    {
        digitGlyphs = Digits?.Invoke() ?? Array.Empty<DigitGlyph>();
        if (digitGlyphs.Length < 10) return;
        digits = new Texture[10];
        for (int i = 0; i < 10; i++)
        {
            // White with a soft dark outline, as the skin's numbers have: a white number alone is lost on a light circle.
            var glyph = digitGlyphs[i];
            int width = glyph.Width + DigitOutline * 2, height = glyph.Height + DigitOutline * 2;
            var pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // How near the digit is, as the strongest coverage around weighed by its distance.
                    float near = 0;
                    for (int dy = -DigitOutline; dy <= DigitOutline; dy++)
                    {
                        int sy = y - DigitOutline + dy;
                        if (sy < 0 || sy >= glyph.Height) continue;
                        for (int dx = -DigitOutline; dx <= DigitOutline; dx++)
                        {
                            int sx = x - DigitOutline + dx;
                            if (sx < 0 || sx >= glyph.Width) continue;
                            float weight = 1 - MathF.Sqrt(dx * dx + dy * dy) / DigitOutline;
                            if (weight > 0) near = MathF.Max(near, glyph.Coverage[sy * glyph.Width + sx] / 255f * MathF.Min(1, weight * 2));
                        }
                    }
                    int gx = x - DigitOutline, gy = y - DigitOutline;
                    float white = gx >= 0 && gy >= 0 && gx < glyph.Width && gy < glyph.Height ? glyph.Coverage[gy * glyph.Width + gx] / 255f : 0;
                    float alpha = white + 0.55f * near * (1 - white);
                    int p = (y * width + x) * 4;
                    // Premultiplied: the white part is all that adds colour.
                    pixels[p] = pixels[p + 1] = pixels[p + 2] = (byte)MathF.Round(white * 255);
                    pixels[p + 3] = (byte)MathF.Round(alpha * 255);
                }
            }
            digits[i] = new Texture(gl, width, height, pixels);
        }
    }

    private SkinSprite? LoadSkinSprite(string name)
    {
        var image = skin.ReadAnimation(name).FirstOrDefault();
        return image == null ? null : CreateSkinSprite(image);
    }

    private SkinSprite CreateSkinSprite(OsuPreviewSkin.Bitmap image) => new(new Texture(gl, image.Width, image.Height, image.Pixels), image.Size);

    private void ReplaceSkinTexture(ref Texture? texture, string name)
    {
        var frames = skin.ReadAnimation(name).Select(CreateSkinSprite).ToArray();
        if (frames.Length == 0) return;
        texture?.Dispose();
        texture = frames[0].Texture;
        foreach (var frame in frames) skinSizes[frame.Texture] = frame.Size;
        if (frames.Length > 1) skinAnimations[texture] = frames;
    }

    /// <summary>How far the dark outline of a digit reaches, in pixels of the digit's picture.</summary>
    private const int DigitOutline = 6;

    /// <summary>How much of a pixel a shape covers whose edge is that many pixels away: the inside counts as positive.</summary>
    private static float Cover(float pixels) => Math.Clamp(pixels + 0.5f, 0, 1);

    /// <summary>One colour laid over another, neither with its alpha multiplied in.</summary>
    private static Vector4 Over(Vector4 top, Vector4 bottom)
    {
        float a = top.W + bottom.W * (1 - top.W);
        if (a <= 0) return Vector4.Zero;
        Vector3 rgb = (new Vector3(top.X, top.Y, top.Z) * top.W + new Vector3(bottom.X, bottom.Y, bottom.Z) * bottom.W * (1 - top.W)) / a;
        return new Vector4(rgb, a);
    }

    /// <summary>
    /// A square picture worked out pixel by pixel. The function is given a place from -1 to 1 both ways and
    /// how many pixels one unit is, and returns the colour there with its alpha not multiplied in.
    /// </summary>
    private Texture Paint(int size, Func<float, float, float, Vector4> at)
    {
        var pixels = new byte[size * size * 4];
        float px = size / 2f;
        for (int row = 0; row < size; row++)
        {
            float y = (row + 0.5f) / px - 1;
            for (int column = 0; column < size; column++)
            {
                var c = at((column + 0.5f) / px - 1, y, px);
                float a = Math.Clamp(c.W, 0, 1);
                int i = (row * size + column) * 4;
                pixels[i] = (byte)MathF.Round(Math.Clamp(c.X, 0, 1) * a * 255);
                pixels[i + 1] = (byte)MathF.Round(Math.Clamp(c.Y, 0, 1) * a * 255);
                pixels[i + 2] = (byte)MathF.Round(Math.Clamp(c.Z, 0, 1) * a * 255);
                pixels[i + 3] = (byte)MathF.Round(a * 255);
            }
        }
        return new Texture(gl, size, size, pixels);
    }

    private void ReleaseTextures()
    {
        ReleaseTimelineImages();
        ReleaseRulesetTextures();
        ReleaseArgonPictures();
        foreach (var texture in new[] { circle, overlay, approach, arrow, ball, ballHighlight, followCircle, tick, followPoint, spinnerDisc, spinnerGlow })
            texture?.Dispose();
        foreach (var texture in digits) texture.Dispose();
        foreach (var frame in skinBallFrames) frame.Texture.Dispose();
        // The first animation frame is already disposed through its main sprite field above.
        foreach (var frames in skinAnimations.Values)
            foreach (var frame in frames.Skip(1)) frame.Texture.Dispose();
        skinBallBackground?.Texture.Dispose();
        skinBallHighlight?.Texture.Dispose();
        circle = overlay = approach = arrow = ball = ballHighlight = followCircle = tick = followPoint = spinnerDisc = spinnerGlow = null;
        digits = Array.Empty<Texture>();
        skinDigitSizes = Array.Empty<Vector2>();
        skinBallFrames = Array.Empty<SkinSprite>();
        skinBallBackground = skinBallHighlight = null;
        skinSizes.Clear();
        skinAnimations.Clear();
    }

    public void Dispose()
    {
        ReleaseTextures();
        argonShapes?.Dispose();
        argonManiaRectangles?.Dispose();
        argonCatchBlobs?.Dispose();
        bodyShader.Dispose();
        gl.DeleteBuffer(bodyVbo);
        gl.DeleteVertexArray(bodyVao);
    }
}
