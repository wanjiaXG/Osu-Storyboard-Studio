namespace SbStudio.Core;

/// <summary>
/// The part of baking that is about trees of groups that use depth (<see cref="GroupSpace"/>): what the
/// camera shows of a member at the moments a bake asks for, and taking a member or a group out of a group
/// that is inside another while it stays in the space.
/// </summary>
public static partial class GroupBaker
{
    /// <summary>
    /// What a member of a space shows at the moments its bake asks for. The fits of position, size,
    /// rotation, opacity and colour ask for largely the same moments, and each answer is a projection
    /// through the camera, so each moment is worked out once.
    ///
    /// At and behind the near plane the member is not shown, and its position, size and rotation are held
    /// at those of the last moment it was: what it does unseen needs no commands. That moment is looked for
    /// along a grid of the shortest step from the start of the bake, the same for every moment asked for,
    /// so that all of a hidden stretch holds one state, and each point of the grid is looked at once.
    /// </summary>
    private sealed class SpaceMotion
    {
        /// <summary>Tables of this size and more are not kept from one bake for the next.</summary>
        private const int MaxKept = 1 << 16;

        // One per thread, used again by every bake on it: dragging a camera bakes every member of the tree
        // every frame, and new tables for each would be tens of megabytes of garbage a frame. A bake bakes
        // nothing else while it runs.
        [ThreadStatic] private static SpaceMotion? reused;

        private readonly Dictionary<double, SpriteState> states = new();
        /// <summary>For each point of the grid, the state held behind the near plane from there to the next point.</summary>
        private readonly Dictionary<int, SpriteState> held = new();
        private MemberSpace space = null!;
        private int start, step;
        private double near;

        /// <param name="start">Where the bake starts, which is where the grid starts.</param>
        /// <param name="step">The shortest step of the bake, which is that of the grid.</param>
        public static SpaceMotion For(SbSprite sprite, int start, int step)
        {
            var motion = reused ??= new SpaceMotion();
            Clear(motion.states);
            Clear(motion.held);
            motion.space = new MemberSpace(sprite);
            motion.start = start;
            motion.step = step;
            motion.near = sprite.Group!.Root.NearClip;
            return motion;
        }

        private static void Clear<T>(Dictionary<T, SpriteState> table) where T : notnull
        {
            table.Clear();
            if (table.EnsureCapacity(0) > MaxKept) table.TrimExcess(MaxKept);
        }

        public SpriteState At(double time)
        {
            if (states.TryGetValue(time, out var known)) return known;
            var projection = space.Project(time);
            var state = projection.State;
            if (projection.Depth <= near)
            {
                var shown = Held(time);
                state.X = shown.X;
                state.Y = shown.Y;
                state.ScaleX = shown.ScaleX;
                state.ScaleY = shown.ScaleY;
                state.Rotation = shown.Rotation;
            }
            states[time] = state;
            return state;
        }

        /// <summary>
        /// What was shown at the last point of the grid up to a time that is in front of the near plane;
        /// with none, at the start.
        /// </summary>
        private SpriteState Held(double time)
        {
            int index = Math.Max(0, (int)Math.Floor((time - start) / step));
            if (held.TryGetValue(index, out var state)) return state;
            int point = index;
            while (!held.TryGetValue(point, out state))
            {
                var projection = space.Project(start + (double)point * step);
                if (point == 0 || projection.Depth > near)
                {
                    state = projection.State;
                    break;
                }
                point--;
            }
            for (int i = point; i <= index; i++) held[i] = state;
            return state;
        }
    }

    /// <summary>
    /// <see cref="BakeSprite"/> for a member of a space taken out of a group inside another: the motion of
    /// the groups inside of <paramref name="stopAt"/> is written into the member's own M, Z, V, R, RX and
    /// RY, so that it goes on moving through the space as before. The angles come from fitting the turn
    /// they make together, so near gimbal lock the result can be off; the copy then has
    /// <see cref="SbSprite.CompositionWarning"/> set.
    /// </summary>
    private static SbSprite BakeSpaceMember(SbSprite sprite, SbObjectGroup? stopAt, BakeOptions? options)
    {
        if (options == null)
        {
            var precision = BakeOptions.ForChain(sprite.Group, stopAt);
            options = BakeOptions.Of(precision.Step, precision.Tolerance);
        }
        var copy = (SbSprite)sprite.Clone();
        copy.PlaceCopyIn(stopAt);
        int start = sprite.Timeline.StartTime, end = sprite.Timeline.EndTime;

        // As for a flat bake, the ends of all commands involved are where the motion can change character.
        var points = new SortedSet<int> { start, end };
        var commands = sprite.AllCommands().Concat(sprite.DepthCommands).ToList();
        for (var g = sprite.Group; g != null && g != stopAt; g = g.Parent) commands.AddRange(g.Commands);
        foreach (var c in commands)
        {
            if (c.Start > start && c.Start < end) points.Add(c.Start);
            if (c.End > start && c.End < end) points.Add(c.End);
            if (c.Curve == null) continue;
            foreach (var (x, _) in c.Curve.Points)
            {
                int at = (int)Math.Round(c.Start + (c.End - c.Start) * x);
                if (at > start && at < end) points.Add(at);
            }
        }

        // Everything but the motion is kept; loops with motion in them are unrolled, as their length would
        // change with some of their commands taken out.
        var nodes = new List<SbNode>();
        bool Motion(CommandKind kind) => IsTransform(kind) || kind == CommandKind.V;
        foreach (var node in sprite.Nodes)
        {
            if (node is SbCommand c)
            {
                if (!Motion(c.Kind)) nodes.Add(c.Clone());
            }
            else if (node is SbLoop loop && loop.Commands.Any(lc => Motion(lc.Kind)))
            {
                if ((long)loop.Iterations * loop.Commands.Count > options.MaxLoopExpansion) return copy;
                for (int i = 0; i < loop.Iterations; i++)
                {
                    foreach (var lc in loop.Commands)
                    {
                        int from = loop.Start + i * loop.IterationLength + lc.Start, to = loop.Start + i * loop.IterationLength + lc.End;
                        if (from > start && from < end) points.Add(from);
                        if (to > start && to < end) points.Add(to);
                        if (Motion(lc.Kind)) continue;
                        var flat = lc.CloneCommand();
                        flat.Start = from;
                        flat.End = to;
                        nodes.Add(flat);
                    }
                }
            }
            else
            {
                nodes.Add(node.Clone());
            }
        }
        copy.DepthCommands = new();

        var space = new MemberSpace(sprite, stopAt);
        SpacePose At(double t) => space.Pose(t);

        // One of the angles of the combined turn: 0 for RX, 1 for RY, 2 for R.
        double Angle(double t, int component)
        {
            double own = component == 2 ? sprite.Timeline.Evaluate(t, sprite).Rotation : space.Own(component == 0 ? CommandKind.RX : CommandKind.RY, t);
            // A card that faces the camera is not turned by its groups, so its own angles stay as they are.
            if (sprite.FaceCamera) return own;
            var angles = At(t).Rotation.Angles;
            double value = component == 0 ? angles.RX : component == 1 ? angles.RY : angles.R;
            // The angles of a rotation are only known up to full turns. Those the turns that were written
            // add up to are taken, so that full turns are kept.
            double reference = own;
            for (var g = sprite.Group; g != null && g != stopAt; g = g.Parent)
            {
                var s = g.Timeline.Evaluate(t, g);
                reference += component == 0 ? s.RX : component == 1 ? s.RY : s.Rotation;
            }
            return value + Math.Round((reference - value) / (2 * Math.PI)) * 2 * Math.PI;
        }

        void Channel(CommandKind kind, Func<double, Vec> value, double tolerance, int dimensions)
        {
            var segments = new List<Segment>();
            int previous = start;
            foreach (int point in points)
            {
                if (point > previous) Fit(value, previous, point, tolerance, new() { 0 }, options.MinStep, segments);
                previous = point;
            }
            if (segments.Count == 0) segments.Add(new(start, end, value(start), value(end), 0));
            foreach (var s in segments)
            {
                double[] from = dimensions == 1 ? new[] { s.From.X } : new[] { s.From.X, s.From.Y };
                double[] to = dimensions == 1 ? new[] { s.To.X } : new[] { s.To.X, s.To.Y };
                var c = new SbCommand(kind, s.Easing, s.Start, s.End, from, to);
                if (SbInfo.DepthKinds.Contains(kind)) copy.DepthCommands.Add(c);
                else nodes.Add(c);
            }
        }

        Channel(CommandKind.M, t => { var p = At(t).Position; return new(p.X, p.Y); }, options.PositionTolerance, 2);
        Channel(CommandKind.Z, t => new(At(t).Position.Z, 0), options.PositionTolerance, 1);
        Channel(CommandKind.V, t => { var pose = At(t); return new(pose.Scale * pose.ScaleX, pose.Scale * pose.ScaleY); }, options.ScaleTolerance, 2);
        Channel(CommandKind.RX, t => new(Angle(t, 0), 0), options.RotationTolerance, 1);
        Channel(CommandKind.RY, t => new(Angle(t, 1), 0), options.RotationTolerance, 1);
        Channel(CommandKind.R, t => new(Angle(t, 2), 0), options.RotationTolerance, 1);
        copy.Nodes = nodes;
        copy.TouchCopy();

        // Each angle is fitted apart, but they act together: where the turn they make together is off by
        // more than the tolerance (in pixels at the BakeOptions.PerPixel radius), that is reported.
        var check = new MemberSpace(copy, stopAt);
        for (int t = start; t <= end;)
        {
            var a = At(t);
            var b = check.Pose(t);
            double position = (a.Position - b.Position).Length;
            double turn = sprite.FaceCamera ? 0 : Math.Max((a.Rotation.X - b.Rotation.X).Length, (a.Rotation.Y - b.Rotation.Y).Length) * Math.Abs(a.Scale) * 167;
            if (position > options.PositionTolerance + 0.02 || turn > options.PositionTolerance + 0.02)
            {
                copy.CompositionWarning = true;
                break;
            }
            if (t == end) break;
            t = (int)Math.Min((long)end, (long)t + Math.Max(1, options.MinStep));
        }
        return copy;
    }

    /// <summary>
    /// <see cref="BakeGroup"/> for a group of a space: its motion and that of the group it is in are written
    /// into its own commands, in three dimensions. Taken out of an outermost group it becomes one itself,
    /// and takes a copy of the camera and the settings of the space along.
    /// </summary>
    private static void BakeSpaceGroup(SbObjectGroup group, BakeOptions? options)
    {
        var outer = group.Parent!;
        var all = group.Commands.Concat(outer.Commands).ToArray();
        int start = all.Length == 0 ? 0 : all.Min(c => c.Start), end = all.Length == 0 ? 0 : all.Max(c => c.End);
        // The group stands for its members, so it is carried along for as long as any of them is shown.
        void Span(SbObjectGroup g)
        {
            foreach (var s in g.Members)
            {
                if (!s.Timeline.HasCommands) continue;
                start = Math.Min(start, s.Timeline.StartTime);
                end = Math.Max(end, s.Timeline.EndTime);
            }
            foreach (var c in g.Children) Span(c);
        }
        Span(group);

        // As in the flat case, a sprite at the group's position with the group's commands stands in for it.
        var proxy = new SbSprite { X = group.X, Y = group.Y, Group = outer };
        proxy.Nodes.Add(new SbCommand(CommandKind.F, 0, start, end, new[] { 1.0 }));
        foreach (var c in group.Commands)
        {
            if (SbInfo.DepthKinds.Contains(c.Kind)) proxy.DepthCommands.Add(c.CloneCommand());
            else proxy.Nodes.Add(c.Clone());
        }
        var baked = BakeSpaceMember(proxy, outer.Parent, options);

        group.CompositionWarning = baked.CompositionWarning;
        group.Commands = baked.Nodes.OfType<SbCommand>().Where(c => IsTransform(c.Kind)).Concat(baked.DepthCommands).ToList();
        // A group scales evenly: what the stand-in got as V is its S.
        foreach (var c in baked.Nodes.OfType<SbCommand>().Where(c => c.Kind == CommandKind.V))
            group.Commands.Add(new SbCommand(CommandKind.S, c.Easing, c.Start, c.End, new[] { c.From[0] }, new[] { c.To[0] }));
        if (outer.Parent == null) group.CopySpaceFrom(outer);
        group.Parent = outer.Parent;
        // Being put in a group makes a sprite one of its members; the stand-in and its copy are not.
        proxy.Group = null;
        baked.Group = null;
        group.Touch();
    }
}
