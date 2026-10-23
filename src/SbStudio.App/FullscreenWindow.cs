using System.Numerics;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace SbStudio.App;

public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public Vector2 Center => new(X + Width / 2f, Y + Height / 2f);
}

/// <summary>The window operations needed by a borderless preview.</summary>
public interface IFullscreenWindow
{
    WindowState State { get; set; }
    WindowBorder Border { get; set; }
    Vector2D<int> Position { get; set; }
    Vector2D<int> Size { get; set; }
    IReadOnlyList<ScreenRect> Screens { get; }
}

public sealed class SilkFullscreenWindow(IWindow window) : IFullscreenWindow
{
    public WindowState State { get => window.WindowState; set => window.WindowState = value; }
    public WindowBorder Border { get => window.WindowBorder; set => window.WindowBorder = value; }
    public Vector2D<int> Position { get => window.Position; set => window.Position = value; }
    public Vector2D<int> Size { get => window.Size; set => window.Size = value; }
    public IReadOnlyList<ScreenRect> Screens => Silk.NET.Windowing.Monitor.GetMonitors(window)
        .Select(m => new ScreenRect(m.Bounds.Origin.X, m.Bounds.Origin.Y, m.Bounds.Size.X, m.Bounds.Size.Y)).ToArray();
}

/// <summary>Remembers and restores the main window without switching the display mode.</summary>
public sealed class FullscreenWindow(IFullscreenWindow window, bool automated)
{
    private WindowState state;
    private WindowBorder border;
    private Vector2D<int> position, size;
    public bool Active { get; private set; }

    public void Enter(ScreenRect? preview)
    {
        if (Active) return;
        Active = true;
        if (automated) return;
        state = window.State;
        border = window.Border;
        position = window.Position;
        size = window.Size;
        var reference = preview ?? new ScreenRect(position.X, position.Y, size.X, size.Y);
        var screen = Nearest(window.Screens, reference.Center);
        window.State = WindowState.Normal;
        window.Border = WindowBorder.Hidden;
        if (screen is { } bounds)
        {
            window.Position = new(bounds.X, bounds.Y);
            window.Size = new(bounds.Width, bounds.Height);
        }
    }

    public void Exit()
    {
        if (!Active) return;
        Active = false;
        if (automated) return;
        window.Border = border;
        window.Position = position;
        window.Size = size;
        window.State = state;
    }

    public static ScreenRect? Nearest(IReadOnlyList<ScreenRect> screens, Vector2 point)
    {
        ScreenRect? best = null;
        double distance = double.PositiveInfinity;
        foreach (var screen in screens)
        {
            double dx = Math.Max(Math.Max(screen.X - point.X, 0), point.X - (screen.X + screen.Width));
            double dy = Math.Max(Math.Max(screen.Y - point.Y, 0), point.Y - (screen.Y + screen.Height));
            double candidate = dx * dx + dy * dy;
            if (candidate >= distance) continue;
            distance = candidate;
            best = screen;
        }
        return best;
    }
}
