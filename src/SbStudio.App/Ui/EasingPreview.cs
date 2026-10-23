using System.Numerics;
using ImGuiNET;
using SbStudio.Core;

namespace SbStudio.App.Ui;

/// <summary>
/// What an easing looks like, shown while the mouse rests on its name the way the assets panel shows a
/// larger picture of a file: the curve of the value over the time of the transition, a dot that runs along
/// it, and beside the curve the same movement by itself, which is what the property does. Under it a line
/// or two in words, for those the names say little to.
///
/// The same picture is what a transition drawn by hand is drawn on, see <see cref="CurveEditor"/>.
/// </summary>
internal static class EasingPreview
{
    /// <summary>In place of an easing: no transition, see <see cref="Keyframe.Hold"/>.</summary>
    public const int Hold = Keyframe.Hold;

    /// <summary>How long the dot rests before it sets out, travels, and stays where it arrived, in seconds.</summary>
    private const double Rest = 0.35, Travel = 1.3, Stay = 0.75;
    private const int Steps = 96;

    /// <summary>The easings whose curves only differ in how much they bend, from the least to the most.</summary>
    private static readonly string[] ByStrength = { "Sine", "Quad", "Cubic", "Quart", "Quint", "Expo" };

    private static readonly Vector2[] Points = new Vector2[Steps + 1];
    /// <summary>
    /// When each of the pictures that are up came up, and the frame it was last drawn in. Every picture has
    /// a dot of its own, which sets out when the picture comes up: the curve under a keyframe keeps going
    /// while the list of easings shows another picture beside it, and so do the curves of two keyframes.
    /// </summary>
    private static readonly Dictionary<long, (double Since, int Frame)> Clocks = new();

    /// <summary>What tells the picture of a curve drawn by hand in a list of easings from those of the easings, which go by their numbers.</summary>
    private const long OwnCurve = 1000;

    /// <summary>What tells the picture a curve is drawn on from all others: the widget it is.</summary>
    public static long Editor(uint item) => 0x1_0000_0000L + item;

    private static float Width => Theme.Px(250);
    /// <summary>The room at the right of a picture for the rail the value moves on by itself.</summary>
    public static float Track => Theme.Px(30);

    /// <summary>
    /// Where a curve is drawn: the stretch from the start value up to the end value, with room above and
    /// below for the curves that go beyond the two, and the way between times and values and the screen.
    /// </summary>
    public readonly struct Area
    {
        public readonly Vector2 Origin;
        public readonly float Width, Plot, Over;

        public Area(Vector2 origin, float width)
        {
            Origin = origin;
            Width = width;
            Plot = Theme.Px(104);
            Over = Theme.Px(42);
        }

        public Vector2 Size => new(Width, Plot + Over * 2);
        public float Left => Origin.X;
        public float Right => Origin.X + Width;
        /// <summary>Where the end value is, and where the start value is.</summary>
        public float Top => Origin.Y + Over;
        public float Bottom => Origin.Y + Over + Plot;

        public float X(double time) => Left + (float)time * Width;
        public float Y(double value) => Bottom - (float)value * Plot;
        public double Time(float x) => (x - Left) / Width;
        public double Value(float y) => (Bottom - y) / Plot;
        /// <summary>How far below the start value and above the end value there is room to show a value.</summary>
        public double Beyond => (Over - Theme.Px(7)) / Plot;
    }

    /// <summary>
    /// The picture as a tooltip, with its name above it and what it does in words below.
    /// </summary>
    /// <param name="easing">The number of the easing, or <see cref="Hold"/>.</param>
    /// <param name="title">Its name as the list has it.</param>
    /// <param name="note">What else the tooltip has to say, such as what the list is for.</param>
    /// <param name="beside">
    /// The open list the name is in: the tooltip then stands next to it and stays put while the mouse
    /// goes down the list, where at the mouse it would cover the names still to come.
    /// </param>
    /// <param name="curve">A transition drawn by hand to show in place of the easing.</param>
    public static void Tooltip(int easing, string title, string? note = null, (Vector2 Min, Vector2 Max)? beside = null, EasingCurve? curve = null)
    {
        if (beside is { } list)
        {
            var viewport = ImGui.GetMainViewport();
            var area = new Area(Vector2.Zero, Width);
            float gap = Theme.Px(6), height = area.Size.Y + ImGui.GetTextLineHeightWithSpacing() * 5 + ImGui.GetStyle().WindowPadding.Y * 2;
            // To the right of the list where the window has room for it, otherwise to its left.
            bool right = list.Max.X + gap + Width + Track + ImGui.GetStyle().WindowPadding.X * 2 <= viewport.WorkPos.X + viewport.WorkSize.X;
            float y = MathF.Max(viewport.WorkPos.Y, MathF.Min(list.Min.Y, viewport.WorkPos.Y + viewport.WorkSize.Y - height));
            ImGui.SetNextWindowPos(new Vector2(right ? list.Max.X + gap : list.Min.X - gap, y), ImGuiCond.Always, new Vector2(right ? 0 : 1, 0));
        }

        ImGui.BeginTooltip();
        ImGui.TextUnformatted(title);
        Draw(easing, curve);
        if (curve != null)
        {
            Note(L.T("curve.about"));
            Note(L.T("curve.about.groups"));
        }
        else foreach (string line in Describe(easing)) Note(line);
        if (note != null)
        {
            ImGui.Spacing();
            Note(note);
        }
        ImGui.EndTooltip();
    }

    /// <summary>A dimmed line of the tooltip. Not wrapped: a tooltip is as wide as its longest line.</summary>
    private static void Note(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    /// <summary>The curve with the dot on its way, at the cursor of the window that is being drawn.</summary>
    public static void Draw(int easing, EasingCurve? curve = null)
    {
        var area = new Area(ImGui.GetCursorScreenPos(), Width);
        ImGui.Dummy(new Vector2(area.Width + Track, area.Size.Y));
        if (curve != null)
        {
            var shape = curve.Compile();
            Plot(ImGui.GetWindowDrawList(), area, shape.At, false, OwnCurve);
        }
        else
        {
            Plot(ImGui.GetWindowDrawList(), area, easing < 0 ? _ => 0 : t => Easing.Apply(easing, t), easing < 0, easing);
        }
    }

    /// <summary>
    /// Draws a curve into its area: the lines of the start and the end value with the straight way between
    /// them to compare with, the curve, the dot that runs along it, and at the right the rail the value
    /// moves on by itself.
    /// </summary>
    /// <param name="value">The value of the curve at a time of the transition, both from 0 to 1.</param>
    /// <param name="step">No transition: the value waits and is the next one all at once.</param>
    /// <param name="identity">
    /// What tells this picture from the others that may be up at the same time: the number of an easing,
    /// or <see cref="Editor"/>. Each has a dot that runs by itself.
    /// </param>
    public static void Plot(ImDrawListPtr dl, in Area area, Func<double, double> value, bool step, long identity)
    {
        // The dot sets out when its picture comes up, and anew when the picture comes up again after a while.
        double now = ImGui.GetTime();
        int frame = ImGui.GetFrameCount();
        if (!Clocks.TryGetValue(identity, out var clock) || frame - clock.Frame > 1) clock.Since = now;
        Clocks[identity] = (clock.Since, frame);
        if (Clocks.Count > 256)
        {
            // The pictures that are gone are forgotten.
            foreach (long gone in Clocks.Where(c => frame - c.Value.Frame > 1).Select(c => c.Key).ToList()) Clocks.Remove(gone);
        }
        double phase = (now - clock.Since) % (Rest + Travel + Stay);
        double at = Math.Clamp((phase - Rest) / Travel, 0, 1);

        Vector2 origin = area.Origin, size = area.Size;
        float left = area.Left, right = area.Right, bottom = area.Bottom, top = area.Top;
        dl.AddRectFilled(origin, origin + size, Theme.PanelDark, 4);
        dl.PushClipRect(origin, origin + size, true);
        // Where the value starts and where it ends up, and the straight way between the two to compare with.
        dl.AddLine(new Vector2(left, bottom), new Vector2(right, bottom), Theme.GridLineStrong);
        dl.AddLine(new Vector2(left, top), new Vector2(right, top), Theme.GridLineStrong);
        dl.AddLine(new Vector2(left, bottom), new Vector2(right, top), Theme.GridLine);
        float pad = Theme.Px(5), line = ImGui.GetTextLineHeight();
        uint dim = Theme.WithAlpha(Theme.TextDim, 0.75f);
        dl.AddText(new Vector2(left + pad, top - line - 2), dim, L.T("easing.axis.to"));
        dl.AddText(new Vector2(left + pad, bottom + 2), dim, L.T("easing.axis.from"));
        string time = L.T("easing.axis.time");
        dl.AddText(new Vector2(right - pad - ImGui.CalcTextSize(time).X, origin.Y + size.Y - line - 3), dim, time);

        int count;
        if (step)
        {
            Points[0] = new Vector2(left, bottom);
            Points[1] = new Vector2(right, bottom);
            Points[2] = new Vector2(right, top);
            count = 3;
        }
        else
        {
            for (int i = 0; i <= Steps; i++)
            {
                double t = i / (double)Steps;
                Points[i] = new Vector2(area.X(t), area.Y(value(t)));
            }
            count = Steps + 1;
        }
        dl.AddPolyline(ref Points[0], count, Theme.Accent, ImDrawFlags.None, 2f);

        double reached = step ? (at >= 1 ? 1 : 0) : value(at);
        var dot = new Vector2(area.X(at), area.Y(reached));
        dl.AddLine(new Vector2(dot.X, bottom), dot, Theme.WithAlpha(Theme.Accent, 0.35f));
        dl.PopClipRect();

        // The same movement without the time: what the property itself does.
        float rail = right + Track * 0.55f;
        dl.AddLine(dot, new Vector2(rail, dot.Y), Theme.WithAlpha(Theme.Accent, 0.35f));
        dl.AddLine(new Vector2(rail, bottom), new Vector2(rail, top), Theme.GridLineStrong, 2f);
        dl.AddLine(new Vector2(rail - 4, bottom), new Vector2(rail + 4, bottom), Theme.GridLineStrong);
        dl.AddLine(new Vector2(rail - 4, top), new Vector2(rail + 4, top), Theme.GridLineStrong);
        dl.AddCircleFilled(dot, Theme.Px(4.5f), Theme.Rgba(255, 255, 255));
        dl.AddCircle(dot, Theme.Px(4.5f), Theme.Accent, 0, 1.5f);
        dl.AddCircleFilled(new Vector2(rail, dot.Y), Theme.Px(5.5f), Theme.Accent);
    }

    /// <summary>What an easing does, in words: how its speed goes, and what is particular about its kind.</summary>
    private static IEnumerable<string> Describe(int easing)
    {
        if (easing < 0)
        {
            yield return L.T("easing.hold");
            yield break;
        }
        string name = SbInfo.EasingNames[Math.Clamp(easing, 0, SbInfo.EasingNames.Length - 1)];
        if (name == "Linear")
        {
            yield return L.T("easing.linear");
            yield break;
        }

        bool inOut = name.EndsWith("InOut", StringComparison.Ordinal), into = !inOut && name.EndsWith("In", StringComparison.Ordinal);
        string kind = name.Substring(0, name.Length - (inOut ? 5 : into ? 2 : 3));
        switch (kind)
        {
            case "Back":
                yield return inOut ? L.T("easing.back.inOut") : into ? L.T("easing.back.in") : L.T("easing.back.out");
                yield break;
            case "Elastic":
                yield return inOut ? L.T("easing.elastic.inOut") : into ? L.T("easing.elastic.in") : L.T("easing.elastic.out");
                yield break;
            case "ElasticHalf" or "ElasticQuarter":
                yield return L.T("easing.elastic.out");
                yield return L.T("easing.elastic.fewer");
                yield break;
            case "Bounce":
                yield return inOut ? L.T("easing.bounce.inOut") : into ? L.T("easing.bounce.in") : L.T("easing.bounce.out");
                yield break;
        }

        yield return inOut ? L.T("easing.inOut") : into ? L.T("easing.in") : L.T("easing.out");
        // The two plain ones of the format are the quadratic ones under another number.
        if (kind.Length == 0) yield return L.T("easing.sameAs", into ? "QuadIn" : "QuadOut");
        else if (Array.IndexOf(ByStrength, kind) >= 0)
            yield return L.T("easing.strength", string.Join(" < ", ByStrength.Select(k => k == kind ? "[" + k + "]" : k)));
    }
}

/// <summary>
/// A transition drawn by hand (<see cref="EasingCurve"/>), with its points there to be taken hold of: a
/// point is dragged to where the curve is to go through, Ctrl and a click put a new one where the mouse is,
/// and a right click takes one away. The two ends stay where they are. The dot runs along the curve as in
/// the pictures of the easings, so that what is drawn can be seen moving while it is drawn.
/// </summary>
internal static class CurveEditor
{
    /// <summary>The editor the mouse has hold of a point in, and the point.</summary>
    private static uint holding;
    private static int grabbed = -1;

    /// <param name="width">The room for the picture and the rail at its right.</param>
    /// <returns>True when the curve was changed.</returns>
    public static bool Draw(string id, EasingCurve curve, float width)
    {
        var io = ImGui.GetIO();
        var dl = ImGui.GetWindowDrawList();
        var area = new EasingPreview.Area(ImGui.GetCursorScreenPos(), MathF.Max(Theme.Px(120), width - EasingPreview.Track));
        ImGui.InvisibleButton(id, new Vector2(area.Width + EasingPreview.Track, area.Size.Y));
        uint item = ImGui.GetItemID();
        bool hovered = ImGui.IsItemHovered(), active = ImGui.IsItemActive();
        bool changed = false;

        // The point under the mouse: the nearest one within reach.
        int over = -1;
        float reach = Theme.Px(9), nearest = reach;
        if (hovered || active)
        {
            for (int i = 0; i < curve.Points.Count; i++)
            {
                float distance = Vector2.Distance(io.MousePos, new Vector2(area.X(curve.Points[i].X), area.Y(curve.Points[i].Y)));
                if (distance > nearest) continue;
                nearest = distance;
                over = i;
            }
        }

        if (ImGui.IsItemActivated())
        {
            holding = item;
            grabbed = over;
            // Ctrl and a click where no point is: a new one there, which the mouse then has hold of.
            if (over < 0 && io.KeyCtrl)
            {
                grabbed = curve.Add(area.Time(io.MousePos.X), Math.Clamp(area.Value(io.MousePos.Y), -area.Beyond, 1 + area.Beyond));
                changed = grabbed >= 0;
            }
        }
        bool mine = active && holding == item && grabbed >= 0 && grabbed < curve.Points.Count;
        if (mine && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 1f))
        {
            double x = area.Time(io.MousePos.X), y = Math.Clamp(area.Value(io.MousePos.Y), -area.Beyond, 1 + area.Beyond);
            // Near the lines of the start and the end value a point lands on them, unless Alt says otherwise.
            if (!io.KeyAlt)
            {
                if (MathF.Abs(io.MousePos.Y - area.Top) <= Theme.Px(4)) y = 1;
                else if (MathF.Abs(io.MousePos.Y - area.Bottom) <= Theme.Px(4)) y = 0;
            }
            var before = curve.Points[grabbed];
            curve.Move(grabbed, x, y);
            changed |= curve.Points[grabbed] != before;
        }
        if (!active && holding == item)
        {
            holding = 0;
            grabbed = -1;
        }
        // A right click on a point takes it away.
        if (hovered && !active && over >= 0 && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            curve.Points.RemoveAt(over);
            changed = true;
            over = -1;
        }

        var shape = curve.Compile();
        EasingPreview.Plot(dl, area, shape.At, false, EasingPreview.Editor(item));

        // The ends cannot be moved: small and square. The points in between: round, larger under the mouse.
        uint fixedColor = ImGui.GetColorU32(Theme.TextDim);
        foreach (var end in new[] { new Vector2(area.Left, area.Bottom), new Vector2(area.Right, area.Top) })
            dl.AddRectFilled(end - new Vector2(3, 3), end + new Vector2(3, 3), fixedColor);
        for (int i = 0; i < curve.Points.Count; i++)
        {
            var at = new Vector2(area.X(curve.Points[i].X), area.Y(curve.Points[i].Y));
            bool hot = mine ? i == grabbed : i == over;
            float radius = Theme.Px(hot ? 6.5f : 5f);
            dl.AddCircleFilled(at, radius, hot ? Theme.Accent : Theme.PanelDark);
            dl.AddCircle(at, radius, hot ? Theme.Rgba(255, 255, 255) : Theme.Accent, 0, 2f);
        }

        if (mine)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            Theme.SetTooltip(L.T("curve.point", curve.Points[grabbed].X * 100, curve.Points[grabbed].Y));
        }
        else if (hovered && over >= 0)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }
        return changed;
    }
}
