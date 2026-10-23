using System.Numerics;
using ImGuiNET;

namespace SbStudio.App.Ui;

public sealed partial class TimelinePanel
{
    // Draw with the same lines as the other timeline icons, independent of skin or emoji fonts.
    private static void DrawHitSoundIcon(ImDrawListPtr dl, int lane, Vector2 centre, uint colour)
    {
        float scale = Theme.Px(1), stroke = scale * 1.3f;
        Vector2 P(float x, float y) => centre + new Vector2(x, y) * scale;
        switch (lane)
        {
            case 0: // Drum, with a top rim and tension cords.
                dl.AddEllipse(P(0, 5), new Vector2(7, 2.4f) * scale, colour, 0, 20, stroke);
                dl.AddLine(P(-7, -3), P(-7, 5), colour, stroke);
                dl.AddLine(P(7, -3), P(7, 5), colour, stroke);
                dl.AddEllipse(P(0, -3), new Vector2(7, 2.7f) * scale, colour, 0, 20, stroke);
                dl.AddLine(P(-5, -1), P(-2, 6), colour, stroke);
                dl.AddLine(P(-2, 6), P(1, -1), colour, stroke);
                dl.AddLine(P(1, -1), P(4, 6), colour, stroke);
                break;
            case 1: // Whistle bowl, mouthpiece and air vent.
                dl.AddCircle(P(-3, 2), 4.8f * scale, colour, 20, stroke);
                dl.AddQuadFilled(P(-1, -2), P(7, -4), P(8, -1), P(1, 3), colour);
                dl.AddCircle(P(-3, 2), 1.7f * scale, colour, 12, stroke);
                dl.AddLine(P(5, -7), P(7, -8), colour, stroke);
                dl.AddLine(P(9, -5), P(11, -5), colour, stroke);
                break;
            case 2: // Hi-hat cymbals and their stand.
                dl.AddLine(P(0, -8), P(0, 7), colour, stroke);
                dl.AddTriangleFilled(P(-3, -3), P(0, -5), P(3, -3), colour);
                dl.AddEllipse(P(0, -2), new Vector2(8, 2) * scale, colour, 0, 20, stroke);
                dl.AddEllipse(P(0, 1), new Vector2(8, 2) * scale, colour, 0, 20, stroke);
                dl.AddLine(P(0, 5), P(-4, 8), colour, stroke);
                dl.AddLine(P(0, 5), P(4, 8), colour, stroke);
                break;
            case 3: // Two hands coming together, with short clap rays.
                DrawClappingHand(dl, P(-3, 1), scale * 0.65f, -0.7f, colour);
                DrawClappingHand(dl, P(3, 2), scale * 0.75f, 0.25f, colour);
                dl.AddLine(P(-2, -8), P(-3, -11), colour, stroke);
                dl.AddLine(P(3, -8), P(4, -11), colour, stroke);
                dl.AddLine(P(7, -6), P(10, -8), colour, stroke);
                break;
        }
    }

    private static readonly Vector2[] ClappingHandOutline =
    [
        new(-2, 7), new(-5, 3), new(-7, 0), new(-7, -2), new(-6, -3), new(-3, 0),
        new(-3, -6), new(-2, -7), new(-1, -6), new(-1, -1), new(-1, -8), new(0, -9),
        new(1, -8), new(1, -1), new(1, -7), new(2, -8), new(3, -7), new(3, -1),
        new(3, -5), new(4, -6), new(5, -5), new(5, 3), new(2, 7)
    ];

    private static void DrawClappingHand(ImDrawListPtr dl, Vector2 centre, float scale, float angle, uint colour)
    {
        Span<Vector2> points = stackalloc Vector2[ClappingHandOutline.Length];
        float cos = MathF.Cos(angle), sin = MathF.Sin(angle);
        for (int i = 0; i < points.Length; i++)
        {
            Vector2 p = ClappingHandOutline[i] * scale;
            points[i] = centre + new Vector2(p.X * cos - p.Y * sin, p.X * sin + p.Y * cos);
        }
        dl.AddPolyline(ref points[0], points.Length, colour, ImDrawFlags.Closed, Theme.Px(1.15f));
    }
}
