using System.Numerics;

namespace SbStudio.App.Gfx;

/// <summary>Geometry and time-based animation from lazer's Skinning/Argon components.</summary>
internal static class ArgonPreview
{
    public const float Border = 128 * 2f / 58;
    public const float Gradient = Border * 2.5f;
    public const float OuterSize = 128 - Border * 4;
    public const float InnerSize = OuterSize - Gradient * 2;
    public const float FillSize = InnerSize - Gradient * 2;
    public const float FollowArea = 2.4f;
    public static readonly Vector3[] ComboColours = { new(241, 116, 0), new(0, 241, 53), new(0, 82, 241), new(241, 0, 0), new(232, 235, 0), new(92, 0, 241) };
    public static Vector3 Colour(int index) => ComboColours[index % ComboColours.Length] / 255;
    private static double Unit(double value) => Math.Clamp(value, 0, 1);
    private static double Quint(double value) => 1 - Math.Pow(1 - Unit(value), 5);

    public const float FlashCoreRadius = 32;
    public const float FlashGlowRadius = 64 * 1.2f;
    public const float FlashDiameter = (FlashCoreRadius + FlashGlowRadius) * 2;

    // CompositeDrawable's non-hollow EdgeEffect uses a linear falloff raised to AlphaExponent=2.
    // It has a solid centre, unlike a Gaussian sprite whose alpha falls from the very centre.
    public static float HitFlashCoverage(float distance)
    {
        float alpha = Math.Clamp((FlashCoreRadius + FlashGlowRadius - distance) / FlashGlowRadius, 0, 1);
        return alpha * alpha;
    }

    internal readonly record struct HitState(float ParentAlpha, float FillAlpha, float NumberAlpha, float FlashAlpha,
        float BorderDiameter, float BorderProgress, float OuterDiameter, float OuterWhite, float OuterAlpha);

    private static double ElasticHalf(double value)
    {
        double x = Unit(value);
        if (x == 0 || x == 1) return x;
        return Math.Pow(2, -10 * x) * Math.Sin((0.5 * x - 0.075) * (2 * Math.PI / 0.3)) + 1;
    }

    /// <summary>ArgonMainCirclePiece's hit transforms, evaluated directly so seeking also works backwards.</summary>
    public static HitState CircleHit(double elapsed)
    {
        double fade = 1 - Unit(elapsed / 800);
        return new((float)(fade * fade), (float)(1 - Quint(elapsed / 150)), (float)(1 - Unit(elapsed / 75)),
            (float)Quint(elapsed / 150), (float)(128 + (128 * 0.8 + Border - 128) * ElasticHalf(elapsed / 400)),
            (float)Unit(elapsed / 800), (float)(OuterSize * (1 - 0.2 * ElasticHalf((elapsed - 12.5) / 400))),
            (float)Unit((elapsed - 12.5) / 80), (float)(1 - Unit((elapsed - 92.5) / 150)));
    }

    public static (Vector4 Top, Vector4 Bottom) HitBorderColours(Vector3 accent, float progress) =>
        (Vector4.Lerp(Vector4.One, new Vector4(accent, 0.5f), progress),
         Vector4.Lerp(Vector4.One, new Vector4(accent, 0), progress));

    public static (float Scale, float Alpha) FollowCircle(double elapsed, double duration, double lastTick)
    {
        double scale = 1 + (FollowArea - 1) * Quint(elapsed / 300);
        double alpha = Quint(elapsed / 300);
        double scaleAtTick = 1 + (FollowArea - 1) * Quint(lastTick / 300);
        if (scaleAtTick >= FollowArea * 0.98 && elapsed >= lastTick)
        {
            double pulse = elapsed - lastTick;
            if (pulse < 40) scale = scaleAtTick + (FollowArea * 1.08 - scaleAtTick) * Quint(pulse / 40);
            else if (pulse < 240) scale = FollowArea * (1 + 0.08 * (1 - Quint((pulse - 40) / 200)));
        }
        if (elapsed >= duration)
        {
            double endScale = 1 + (FollowArea - 1) * Quint(duration / 300);
            // Preserve a pulse which is still running when the slider ends.
            if (lastTick < duration && duration - lastTick < 240)
                endScale = FollowCircle(duration - 0.0001, double.PositiveInfinity, lastTick).Scale;
            scale = endScale + (1 - endScale) * Quint((elapsed - duration) / 300);
            alpha = Quint(duration / 300) * (1 - Quint((elapsed - duration) / 150));
        }
        return ((float)scale, (float)alpha);
    }

    public static float ConnectionRotation(Vector2 from, Vector2 to) => MathF.Atan2(to.Y - from.Y, to.X - from.X);
}
