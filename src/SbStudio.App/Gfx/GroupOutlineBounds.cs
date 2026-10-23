using System.Numerics;
using SbStudio.Core;

namespace SbStudio.App.Gfx;

internal readonly record struct GroupOutlineBounds(Vector2 Min, Vector2 Max)
{
    /// <summary>The union of image quads as currently shown, including all nested members.</summary>
    internal static GroupOutlineBounds? Compute(IEnumerable<SbSprite> members, double time, RenderOptions options,
        Func<SbSprite, (int Width, int Height)?> imageSize)
    {
        bool fail = options.Simulation is { Stream.HasStateRules: true } simulation
            ? simulation.FailStateAt(time) : options.FailState;
        Vector2 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        bool any = false;
        Span<Vector2> corners = stackalloc Vector2[4];
        foreach (var sprite in members)
        {
            if ((sprite.Hidden && !options.IncludeHidden) || !sprite.Timeline.IsActive(time) ||
                (sprite.Layer == SbLayer.Fail && !fail) || (sprite.Layer == SbLayer.Pass && fail)) continue;
            var state = options.Simulation?.Evaluate(sprite, time) ?? sprite.ShownState(time);
            if (!(state.Opacity > 0) || state.Scale == 0 || state.ScaleX == 0 || state.ScaleY == 0) continue;
            if (imageSize(sprite) is not { Width: > 0, Height: > 0 } size) continue;
            SpriteGeometry.Corners(state, sprite.Origin, size.Width, size.Height, corners);
            bool finite = true;
            foreach (var point in corners) finite &= float.IsFinite(point.X) && float.IsFinite(point.Y);
            if (!finite) continue;
            foreach (var point in corners)
            {
                min = Vector2.Min(min, point);
                max = Vector2.Max(max, point);
            }
            any = true;
        }
        return any ? new(min, max) : null;
    }
}
