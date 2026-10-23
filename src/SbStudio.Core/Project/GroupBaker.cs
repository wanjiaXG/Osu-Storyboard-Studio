namespace SbStudio.Core;

/// <summary>
/// How closely the plain commands follow the motion of a group. Each group has its own precision.
/// </summary>
public sealed class BakeOptions
{
    /// <summary>What a new group starts out with.</summary>
    public const int DefaultMinStep = 17;
    public const double DefaultTolerance = 0.5;
    /// <summary>
    /// The least the two can be. Times are whole milliseconds, so no step is shorter than one; and positions
    /// are written with two decimals, so nothing is gained below a hundredth of a pixel.
    /// </summary>
    public const int LeastStep = 1;
    public const double LeastTolerance = 0.01;
    /// <summary>
    /// How much of a scale, and how many radians, count for as much as a pixel of position: what moves a
    /// point 167 pixels from the origin of a picture by one pixel.
    /// </summary>
    public const double PerPixel = 0.006;

    /// <summary>Largest allowed deviation of a baked position, in storyboard pixels.</summary>
    public double PositionTolerance = DefaultTolerance;
    public double ScaleTolerance = DefaultTolerance * PerPixel;
    /// <summary>In radians.</summary>
    public double RotationTolerance = DefaultTolerance * PerPixel;
    /// <summary>Segments are not split below this many milliseconds, however far off they are.</summary>
    public int MinStep = DefaultMinStep;
    /// <summary>Loops that would unroll into more commands than this are left alone.</summary>
    public int MaxLoopExpansion = 100000;
    public Func<string,(int Width,int Height)?>? ImageSize;
    public static Func<string,(int Width,int Height)?>? DefaultImageSize;

    /// <summary>
    /// The options for a shortest step and a tolerance in pixels; scale and rotation are allowed to be
    /// off by as much as comes to the same on the screen. What is less than can be is taken for the least.
    /// </summary>
    public static BakeOptions Of(int minStep, double tolerance)
    {
        if (double.IsNaN(tolerance)) tolerance = DefaultTolerance;
        tolerance = Math.Max(LeastTolerance, tolerance);
        return new BakeOptions
        {
            MinStep = Math.Max(LeastStep, minStep),
            ImageSize = DefaultImageSize,
            PositionTolerance = tolerance,
            ScaleTolerance = tolerance * PerPixel,
            RotationTolerance = tolerance * PerPixel,
        };
    }

    /// <summary>All that the outcome depends on, to tell whether something made with other options still holds.</summary>
    internal (int, double, double, double, int) Key => (MinStep, PositionTolerance, ScaleTolerance, RotationTolerance, MaxLoopExpansion);

    // Combined motion must respect every participating group's requested precision.
    internal static (int Step, double Tolerance) ForChain(SbObjectGroup? group, SbObjectGroup? stopAt = null)
    {
        int step = int.MaxValue, depth = 0;
        double tolerance = double.PositiveInfinity;
        for (var current = group; current != null && current != stopAt && depth < SbObjectGroup.MaxDepth; current = current.Parent, depth++)
        {
            step = Math.Min(step, Math.Max(LeastStep, current.BakeStep));
            double value = double.IsFinite(current.BakeTolerance) ? current.BakeTolerance : DefaultTolerance;
            tolerance = Math.Min(tolerance, Math.Max(LeastTolerance, value));
        }
        return depth == 0 ? (DefaultMinStep, DefaultTolerance) : (step, tolerance);
    }
}

/// <summary>
/// Turns group motion into plain osb. Each member gets M, S and R commands that reproduce what it shows
/// while following its group; everything else (F, C, V, P) is kept as written.
///
/// A former member stays in the layer it is of. What its group gave it is precedence within that layer:
/// a storyboard without groups has the former members after the sprites that were in no group, in the
/// order of their groups (<see cref="Storyboard.GroupedInDrawOrder"/>), and in osb what is written later
/// is drawn on top.
///
/// Where the combined motion is itself a single eased move, one command reproduces it exactly. Anything
/// else, such as the arc a member travels when its group rotates, becomes a chain of short linear commands
/// that stays within the tolerances of <see cref="BakeOptions"/>.
///
/// A member of a tree that uses depth is baked from what the camera shows of it (<see cref="GroupSpace"/>):
/// its size as V, and its F (and with darkening fog its C) as well, since nearness and fog change them.
/// </summary>
public static partial class GroupBaker
{
    private readonly record struct Vec(double X, double Y)
    {
        public static Vec operator +(Vec a, Vec b) => new(a.X + b.X, a.Y + b.Y);
        public static Vec operator -(Vec a, Vec b) => new(a.X - b.X, a.Y - b.Y);
        public static Vec operator *(Vec a, double k) => new(a.X * k, a.Y * k);
        public double Length => Math.Sqrt(X * X + Y * Y);
    }

    private readonly record struct Segment(int Start, int End, Vec From, Vec To, int Easing);

    /// <summary>
    /// A copy of the storyboard without groups, in which every former member moves on its own. The
    /// former members come last, in the order of their groups, each still of its own layer: written
    /// layer by layer, each layer has them after the sprites that were in no group, and so on top of them.
    /// </summary>
    public static Storyboard Bake(Storyboard source, BakeOptions? options = null, List<string>? warnings = null)
    {
        // The source may be one part of a storyboard sharing its groups with the others, as when exporting
        // the shared objects and each difficulty's apart: the members of the groups are those of the whole.
        var result = new Storyboard();
        foreach (var obj in source.Objects)
            if (obj is not SbSprite { Group: not null }) result.Objects.Add(obj.Clone());
        foreach (var sprite in Storyboard.GroupedInDrawOrder(source.Objects, source.GroupRanks()))
            result.Objects.Add(BakeSprite(sprite, options, warnings));
        return result;
    }

    public static bool IsTransform(CommandKind kind) => SbObjectGroup.Accepts(kind);

    /// <summary>
    /// Whether a group shows its members just where they are: no commands, so a scale of 1 and no turn,
    /// and its pivot shown where it is. Depth is asked separately.
    /// </summary>
    public static bool IsStill(SbObjectGroup group) =>
        group.Commands.Count == 0 && group.X == group.PivotX && group.Y == group.PivotY;

    /// <summary>
    /// Whether a sprite is scaled by V commands of its own anywhere: at the top, in a loop or in a trigger.
    /// osu! lets S and V not both have their way at once, so such a sprite gets the scale of its groups
    /// written as V commands, its own S and V folded in, rather than as S commands next to its V.
    /// </summary>
    public static bool UsesVectorScale(SbSprite sprite)
    {
        foreach (var node in sprite.Nodes)
        {
            if (node is SbCommand { Kind: CommandKind.V }) return true;
            if (node is SbGroup block && block.Commands.Any(c => c.Kind == CommandKind.V)) return true;
        }
        return false;
    }

    /// <summary>
    /// A copy of a grouped sprite that shows the same thing without its groups: the one it is in, and
    /// every one that one is in.
    /// </summary>
    /// <param name="stopAt">
    /// The group the copy stays in: only the motion of the groups inside of it is written into the copy.
    /// With null that is all of them, and the copy is in no group.
    /// </param>
    public static SbSprite BakeSprite(SbSprite sprite, BakeOptions? options = null, List<string>? warnings = null, SbObjectGroup? stopAt = null)
    {
        bool depth = GroupSpace.UsesDepth(sprite.Group);
        // Taken out of a group that is inside another, the member stays in the space: it keeps moving in
        // three dimensions, not as the camera shows it.
        if (depth && stopAt != null) return BakeSpaceMember(sprite, stopAt, options);
        if (options == null)
        {
            var precision = BakeOptions.ForChain(sprite.Group, stopAt);
            options = BakeOptions.Of(precision.Step, precision.Tolerance);
        }
        // A step of nothing would never be reached by halving.
        int minStep = Math.Max(BakeOptions.LeastStep, options.MinStep);
        double scaleTolerance = options.ScaleTolerance, rotationTolerance = options.RotationTolerance;
        // Through a camera a picture can come close and large, and then a scale or a turn that is off by
        // a fixed amount moves its corners by many pixels. With the size of the picture known, both are
        // allowed only as much as moves an edge (for scale) or a corner (for rotation) by the tolerance.
        string picture = sprite.IsAnimation ? SbSprite.FramePath(sprite.Path, 0) : sprite.Path;
        if (depth && (options.ImageSize ?? BakeOptions.DefaultImageSize)?.Invoke(picture) is { } size && size.Width > 0 && size.Height > 0)
        {
            scaleTolerance = options.PositionTolerance * 2 / Math.Max(size.Width, size.Height);
            rotationTolerance = options.PositionTolerance * 2 / Math.Sqrt((double)size.Width * size.Width + (double)size.Height * size.Height);
        }
        var baked = (SbSprite)sprite.Clone();
        // The groups whose motion is written out, from the innermost.
        var groups = new List<SbObjectGroup>();
        for (var g = sprite.Group; g != null && g != stopAt && groups.Count < SbObjectGroup.MaxDepth; g = g.Parent) groups.Add(g);
        baked.PlaceCopyIn(groups.Count > 0 && groups.Count < SbObjectGroup.MaxDepth ? groups[^1].Parent : sprite.Group);
        var timeline = sprite.Timeline;
        // A sprite without commands is never shown, and triggers only run in the game: nothing to bake.
        if (groups.Count == 0 || !timeline.HasCommands) return baked;
        // Groups that neither move nor turn nor scale leave the sprite where its own commands put it, so
        // those commands are already exact: written again by sampling they could only come out less so.
        if (!depth && groups.All(IsStill)) return baked;

        int start = timeline.StartTime, end = timeline.EndTime;
        var groupTimelines = groups.Select(g => g.Timeline).ToArray();
        string name = sprite.DisplayName;

        // The sprite's own move, scale and rotate commands are replaced by baked ones. Their start and end
        // times, and the group's, are where the combined motion can change character. A sprite with V
        // commands of its own has its whole size written as V, so its V commands are replaced too and no S
        // is written for it: S and V would be at odds in osu!.
        //
        // Through a camera the size is always written as V, since a card seen at an angle is narrower one
        // way than the other. The near plane and fog change the opacity, and darkening fog the colour, so
        // those commands are baked as well.
        bool vector = depth || UsesVectorScale(sprite);
        bool fade = depth;
        bool darken = depth && sprite.Group!.Root.Fog == FogMode.Darken;
        bool Replaced(CommandKind kind) =>
            IsTransform(kind) || (vector && kind == CommandKind.V) || (fade && kind == CommandKind.F) || (darken && kind == CommandKind.C);
        var kept = new List<SbNode>();
        var points = new SortedSet<int> { start, end };
        var easings = new List<int> { 0 };

        void Mark(int from, int to, int easing)
        {
            if (from > start && from < end) points.Add(from);
            if (to > start && to < end) points.Add(to);
            if (!easings.Contains(easing)) easings.Add(easing);
        }

        // A transition drawn by hand can turn at each of its points, however near they are to each other.
        // The stretch between two of them is described by itself, so that none is passed over.
        void MarkCurve(SbCommand c)
        {
            if (c.Curve == null) return;
            foreach (var (x, _) in c.Curve.Points)
            {
                int at = (int)Math.Round(c.Start + (c.End - c.Start) * x);
                if (at > start && at < end) points.Add(at);
            }
        }

        foreach (var node in sprite.Nodes)
        {
            switch (node)
            {
                case SbCommand c when Replaced(c.Kind):
                    Mark(c.Start, c.End, c.Easing);
                    MarkCurve(c);
                    break;

                case SbCommand c:
                    kept.Add(c.Clone());
                    break;

                case SbLoop loop when !loop.Commands.Any(c => Replaced(c.Kind)):
                    kept.Add(loop.Clone());
                    break;

                case SbLoop loop:
                {
                    // Taking some commands out of a loop would change its length, so the whole loop is unrolled.
                    int length = loop.IterationLength, iterations = loop.Iterations;
                    if ((long)iterations * loop.Commands.Count > options.MaxLoopExpansion)
                    {
                        baked.BakeLoopTooLong = true;
                        warnings?.Add(L.T("bake.loopTooLong", name));
                        return baked;
                    }
                    for (int k = 0; k < iterations; k++)
                    {
                        foreach (var c in loop.Commands)
                        {
                            int from = loop.Start + k * length + c.Start, to = loop.Start + k * length + c.End;
                            if (Replaced(c.Kind))
                            {
                                Mark(from, to, c.Easing);
                            }
                            else
                            {
                                var flat = c.CloneCommand();
                                flat.Start = from;
                                flat.End = to;
                                kept.Add(flat);
                            }
                        }
                    }
                    break;
                }

                case SbTrigger trigger:
                    if (trigger.Commands.Any(c => IsTransform(c.Kind)))
                        warnings?.Add(L.T("bake.trigger", name));
                    kept.Add(trigger.Clone());
                    break;
            }
        }

        foreach (var group in groups)
        {
            foreach (var c in group.Commands)
            {
                if (c.End < start || c.Start > end) continue;
                Mark(c.Start, c.End, c.Easing);
                MarkCurve(c);
            }
        }

        // In a space the member's own depth commands and the camera move it too.
        if (depth)
        {
            foreach (var c in sprite.DepthCommands.Concat(sprite.Group!.Root.Camera?.Commands ?? Enumerable.Empty<SbCommand>()))
            {
                Mark(c.Start, c.End, c.Easing);
                MarkCurve(c);
            }
        }

        var space = depth ? SpaceMotion.For(sprite, start, minStep) : null;
        SpriteState World(double time)
        {
            if (space != null) return space.At(time);
            var state = timeline.Evaluate(time, sprite);
            for (int i = 0; i < groups.Count; i++) GroupTimeline.Apply(ref state, groupTimelines[i].Evaluate(time, groups[i]), groups[i]);
            return state;
        }

        var position = new List<Segment>();
        var scale = new List<Segment>();
        var rotation = new List<Segment>();
        var opacity = new List<Segment>();
        var colorRG = new List<Segment>();
        var colorB = new List<Segment>();
        var linearOnly = new List<int> { 0 };
        int previous = start;
        foreach (int point in points)
        {
            if (point == previous) continue;
            Fit(t => { var w = World(t); return new Vec(w.X, w.Y); }, previous, point, options.PositionTolerance, easings, minStep, position);
            if (vector) Fit(t => { var w = World(t); return new Vec(w.Scale * w.ScaleX, w.Scale * w.ScaleY); }, previous, point, scaleTolerance, easings, minStep, scale);
            else Fit(t => new Vec(World(t).Scale, 0), previous, point, scaleTolerance, easings, minStep, scale);
            Fit(t => new Vec(World(t).Rotation, 0), previous, point, rotationTolerance, easings, minStep, rotation);
            if (fade) Fit(t => new Vec(World(t).Opacity, 0), previous, point, 0.001, easings, minStep, opacity);
            if (darken)
            {
                // Linear only: the colour is written as straight steps between the ends of both fits together.
                Fit(t => { var w = World(t); return new Vec(w.R, w.G); }, previous, point, 0.5, linearOnly, minStep, colorRG);
                Fit(t => new Vec(World(t).B, 0), previous, point, 0.5, linearOnly, minStep, colorB);
            }
            previous = point;
        }

        var first = World(start);
        var last = World(end);
        baked.X = Math.Round(first.X, 2);
        baked.Y = Math.Round(first.Y, 2);

        // Stretches where a value does not change need no command: it holds between commands anyway.
        foreach (var s in position)
        {
            double[] from = { Math.Round(s.From.X, 2), Math.Round(s.From.Y, 2) }, to = { Math.Round(s.To.X, 2), Math.Round(s.To.Y, 2) };
            if (from[0] != to[0] || from[1] != to[1]) kept.Add(new SbCommand(CommandKind.M, s.Easing, s.Start, s.End, from, to));
        }

        bool scaleVaries = false;
        if (vector)
        {
            foreach (var s in scale)
            {
                double[] from = { Math.Round(s.From.X, 4), Math.Round(s.From.Y, 4) }, to = { Math.Round(s.To.X, 4), Math.Round(s.To.Y, 4) };
                if (from[0] == to[0] && from[1] == to[1]) continue;
                scaleVaries = true;
                kept.Add(new SbCommand(CommandKind.V, s.Easing, s.Start, s.End, from, to));
            }
            double width = Math.Round(first.Scale * first.ScaleX, 4), height = Math.Round(first.Scale * first.ScaleY, 4);
            if (!scaleVaries && (width != 1 || height != 1))
                kept.Add(new SbCommand(CommandKind.V, 0, start, start, new[] { width, height }));
        }
        else
        {
            foreach (var s in scale)
            {
                double from = Math.Round(s.From.X, 4), to = Math.Round(s.To.X, 4);
                if (from == to) continue;
                scaleVaries = true;
                kept.Add(new SbCommand(CommandKind.S, s.Easing, s.Start, s.End, new[] { from }, new[] { to }));
            }
            if (!scaleVaries && Math.Round(first.Scale, 4) != 1)
                kept.Add(new SbCommand(CommandKind.S, 0, start, start, new[] { Math.Round(first.Scale, 4) }));
        }

        bool rotationVaries = false;
        foreach (var s in rotation)
        {
            double from = Math.Round(s.From.X, 5), to = Math.Round(s.To.X, 5);
            if (from == to) continue;
            rotationVaries = true;
            kept.Add(new SbCommand(CommandKind.R, s.Easing, s.Start, s.End, new[] { from }, new[] { to }));
        }
        if (!rotationVaries && Math.Round(first.Rotation, 5) != 0)
            kept.Add(new SbCommand(CommandKind.R, 0, start, start, new[] { Math.Round(first.Rotation, 5) }));

        if (depth)
        {
            // The copy is flat: what depth and the camera did is in its plain commands now.
            baked.DepthCommands.Clear();
            baked.FaceCamera = false;
            if (fade)
            {
                if (opacity.Count == 0) kept.Add(new SbCommand(CommandKind.F, 0, start, end, new[] { first.Opacity }));
                foreach (var s in opacity) kept.Add(new SbCommand(CommandKind.F, s.Easing, s.Start, s.End, new[] { s.From.X }, new[] { s.To.X }));
            }
            if (darken)
            {
                // One C command per stretch between the ends of either fit, so that all three stay linear.
                var times = colorRG.Concat(colorB).SelectMany(s => new[] { s.Start, s.End }).Append(start).Append(end).Distinct().OrderBy(t => t).ToArray();
                for (int i = 0; i < Math.Max(1, times.Length - 1); i++)
                {
                    int a = times[i], b = times[Math.Min(i + 1, times.Length - 1)];
                    var x = World(a);
                    var y = World(b);
                    kept.Add(new SbCommand(CommandKind.C, 0, a, b, new[] { x.R, x.G, x.B }, new[] { y.R, y.G, y.B }));
                }
            }
            // Where a card would be sheared it is written as the nearest rectangle. That is not reported: the
            // preview shows the same rectangle the game does, so there is nothing that differs from it.
        }
        baked.Nodes = kept;
        baked.TouchCopy();

        // The sprite must stay visible for exactly as long as before. If the commands that used to mark the
        // beginning or the end were dropped as "no change", a move that goes nowhere takes their place.
        var bakedTimeline = baked.Timeline;
        if (!bakedTimeline.HasCommands)
        {
            kept.Add(Hold(start, end, first));
        }
        else
        {
            if (bakedTimeline.StartTime > start) kept.Add(Hold(start, bakedTimeline.StartTime, first));
            if (bakedTimeline.EndTime < end) kept.Add(Hold(bakedTimeline.EndTime, end, last));
        }
        baked.TouchCopy();
        return baked;
    }

    /// <summary>
    /// Writes the motion of the group a group is in into that group's own commands, so that its members go
    /// on as before once it is taken out of the other: where it is, how large and how far turned. The group
    /// is changed in place and stands where the other one stood.
    /// </summary>
    public static void BakeGroup(SbObjectGroup group, BakeOptions? options = null)
    {
        var outer = group.Parent;
        if (outer == null) return;
        if (GroupSpace.UsesDepth(group))
        {
            BakeSpaceGroup(group, options);
            return;
        }
        if (options == null)
        {
            var precision = BakeOptions.ForChain(group, outer.Parent);
            options = BakeOptions.Of(precision.Step, precision.Tolerance);
        }
        // A group is carried along by the one it is in the way a sprite at its position is: a sprite with
        // the commands of the group stands in for it, shown for as long as either of the two does anything.
        int start = int.MaxValue, end = int.MinValue;
        foreach (var c in group.Commands.Concat(outer.Commands))
        {
            start = Math.Min(start, Math.Min(c.Start, c.End));
            end = Math.Max(end, Math.Max(c.Start, c.End));
        }
        if (start == int.MaxValue) start = end = 0;
        var stand = new SbSprite { X = group.X, Y = group.Y, Group = outer };
        stand.Nodes.Add(new SbCommand(CommandKind.F, 0, start, end, new[] { 1.0 }));
        foreach (var c in group.Commands) stand.Nodes.Add(c.CloneCommand());

        var baked = BakeSprite(stand, options, null, outer.Parent);
        group.X = baked.X;
        group.Y = baked.Y;
        group.Commands = baked.Nodes.OfType<SbCommand>().Where(c => IsTransform(c.Kind)).ToList();
        group.Parent = outer.Parent;
        // Being put in a group makes a sprite one of its members; the stand-in and its copy are not.
        stand.Group = null;
        baked.Group = null;
        group.Touch();
    }

    private static SbCommand Hold(int from, int to, SpriteState state) =>
        new(CommandKind.M, 0, from, to, new[] { Math.Round(state.X, 2), Math.Round(state.Y, 2) });

    /// <summary>Describes a function on [from, to] with as few commands as the tolerance allows.</summary>
    private static void Fit(Func<double, Vec> f, int from, int to, double tolerance, List<int> easings, int minStep, List<Segment> output)
    {
        Vec a = f(from), b = f(to);
        foreach (int easing in easings)
        {
            if (!Matches(f, from, to, a, b, easing, tolerance)) continue;
            output.Add(new Segment(from, to, a, b, easing));
            return;
        }
        Split(f, from, to, a, b, tolerance, minStep, output);
    }

    /// <summary>Whether one command with this easing follows the function closely enough.</summary>
    private static bool Matches(Func<double, Vec> f, int from, int to, Vec a, Vec b, int easing, double tolerance)
    {
        const int samples = 16;
        for (int i = 1; i < samples; i++)
        {
            double u = i / (double)samples;
            Vec expected = a + (b - a) * Easing.Apply(easing, u);
            if ((f(from + (to - from) * u) - expected).Length > tolerance) return false;
        }
        return true;
    }

    private static void Split(Func<double, Vec> f, int from, int to, Vec a, Vec b, double tolerance, int minStep, List<Segment> output)
    {
        if (to - from <= minStep || Matches(f, from, to, a, b, 0, tolerance))
        {
            output.Add(new Segment(from, to, a, b, 0));
            return;
        }
        int middle = (from + to) / 2;
        Vec m = f(middle);
        Split(f, from, middle, a, m, tolerance, minStep, output);
        Split(f, middle, to, m, b, tolerance, minStep, output);
    }
}
