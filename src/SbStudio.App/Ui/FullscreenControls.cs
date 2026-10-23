using System.Numerics;
using SbStudio.App.Gfx;

namespace SbStudio.App.Ui;

/// <summary>Visibility uses elapsed real time, independently of playback and frame rate.</summary>
public sealed class FullscreenControls
{
    private double lastActivity, lastUpdate;
    public bool Visible { get; private set; }
    public float Alpha { get; private set; }

    public void Enter(double now)
    {
        lastActivity = lastUpdate = now;
        Visible = true;
        Alpha = 1;
    }

    public void Update(double now, bool movedInside, bool onBar, bool interacting)
    {
        if (movedInside || onBar || interacting) lastActivity = now;
        Visible = now - lastActivity < 2;
        float change = (float)Math.Max(0, now - lastUpdate) / 0.18f;
        Alpha = Visible ? Math.Min(1, Alpha + change) : Math.Max(0, Alpha - change);
        lastUpdate = now;
    }

    public static ViewTransform Fit(Vector2 size, bool wide, float zoom = 1, Vector2 pan = default)
    {
        float scale = MathF.Max(0.01f, MathF.Min(size.X / (wide ? 854 : 640), size.Y / 480) * zoom);
        return new ViewTransform { Scale = scale, Offset = size / 2 - new Vector2(320, 240) * scale + pan };
    }
}
