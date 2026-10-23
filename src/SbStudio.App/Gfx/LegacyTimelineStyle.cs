using System.Numerics;

namespace SbStudio.App.Gfx;

/// <summary>Stable EditorModeCompose proportions, converted from its 768 px sprite space to 480 px UI space.</summary>
internal static class LegacyTimelineStyle
{
    // A 1.5x timeline gives the reference strip enough room for stable's circles and bottom tick marks.
    public const float DisplayScale = 1.5f;
    public const float SpriteScale = .4f * 480 / 768;
    public const float NumberScale = .32f * 480 / 768;
    public const float BodyAlpha = 170f / 255;
    public static Vector2 ImageSize(Vector2 logicalSize, float uiScale) => logicalSize * SpriteScale * DisplayScale * uiScale;
    public static float BodyRadius(float uiScale) => 128 / 8f * DisplayScale * uiScale - 1;
    public static float Centre(float top, float height, float uiScale, bool body = false) =>
        top + (height - 35 * DisplayScale * uiScale) / 2 + (body ? 20.5f : 20) * DisplayScale * uiScale;
    public static float EdgeOpacity(float x, float left, float right, float uiScale) =>
        Math.Clamp(Math.Min(x - left, right - x) / (40 * DisplayScale * uiScale), 0, 1);
}
