using System.Numerics;
using SbStudio.Core;

namespace SbStudio.App.Gfx;

/// <summary>Desktop layout and scrolling from lazer's ruleset adjustment containers.</summary>
internal static class RulesetPreviewLayout
{
    internal readonly record struct Taiko(float Left, float Right, float Top, float Scale, float TimeRange)
    {
        public float Height => 200 * Scale;
        public float HitX => Left + 256 * Scale;
        public float Y => Top + Height / 2;
        public float X(OsuBeatmap map, double time, double objectTime, double? originTime = null)
        {
            var point = map.ScrollAt(originTime ?? objectTime);
            double multiplier = map.SliderMultiplier * point.Velocity * 1000 / point.BeatLength;
            return HitX + (float)((objectTime - time) / TimeRange * multiplier * (Right - HitX));
        }
    }

    public static Taiko TaikoField(bool widescreen)
    {
        // The gamefield is the storyboard frame, not the editor render target (which includes
        // letterboxing, zoom and pan). Its geometry must be transformed together with the storyboard.
        float left = widescreen ? -107 : 0, right = widescreen ? 747 : 640;
        float aspect = (right - left) / 480;
        float aspectAdjustment = aspect > 16f / 9 ? aspect / (16f / 9) : aspect < 5f / 4 ? aspect / (5f / 4) : 1;
        float relativeHeight = Math.Min(200f / 768 * aspectAdjustment, 1f / 3);
        float scale = 480 * relativeHeight / 200;
        float inLength = Math.Clamp(aspect, 5f / 4, 16f / 9) * 480 - 160;
        return new(left, right, 135, scale, inLength * 10 / 1.4f);
    }

    internal readonly record struct Catch(float Left, float HitY, float Scale)
    {
        public float ScrollLength => 440 * Scale;
        public Vector2 Position(float x, double delta, double preempt) => new(Left + x * Scale, HitY - (float)(delta / preempt) * ScrollLength);
    }

    public static Catch CatchField(int width, int height, ViewTransform view)
    {
        var min = view.ToStoryboard(Vector2.Zero);
        var max = view.ToStoryboard(new(width, height));
        // 1024x768 playable area is 80% of the gamefield, vertically placed at 3/4 of
        // the spare space. The catcher is at stable Y=340, fruits enter at Y=-100.
        float nativeScale = Math.Min((max.X - min.X) / 1024, (max.Y - min.Y) / 768);
        float scale = nativeScale * 1.6f;
        return new(320 - 256 * scale, 240 + (-384 + 115.2f) * nativeScale + 340 * scale, scale);
    }
}
