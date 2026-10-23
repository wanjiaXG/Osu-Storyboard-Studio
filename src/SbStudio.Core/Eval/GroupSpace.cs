using System.Runtime.CompilerServices;

namespace SbStudio.Core;

/// <summary>What the far end of a space does to what is in it: nothing, fade it out, or darken it.</summary>
public enum FogMode { None, Fade, Darken }

/// <summary>
/// The camera of a space. It looks at a target point from some distance, and can orbit around the
/// target, tilt over it, roll and zoom. Its commands (CT, CD, CO, CI, CL and CF) are edited like those of
/// a group; osu! has no camera, so they are never written out but baked into the members.
///
/// Only the camera of an outermost group is used. One that a group inside another still has is kept for
/// when the group is taken out again (<see cref="SbObjectGroup.CameraActive"/>).
/// </summary>
public sealed class SbCamera
{
    /// <summary>CT, CD, CO, CI, CL and CF commands, timed absolutely.</summary>
    public List<SbCommand> Commands = new();

    /// <summary>Whether its property rows are shown. Kept only for this session, not saved.</summary>
    public bool Expanded = true;

    public SbCamera Clone() => new()
    {
        Commands = Commands.Select(c => c.CloneCommand()).ToList(),
        Expanded = Expanded,
    };

    /// <summary>
    /// The camera at a time. Without commands it is the camera that shows a space the way the storyboard
    /// shows it without one: 1000 pixels in front of the middle of the screen, with a zoom of as much.
    /// </summary>
    public CameraState Evaluate(double time) => CameraTimeline.Build(Commands).Evaluate(time);
}

/// <summary>A point or a direction in space: X to the right and Y down, as on the screen, and Z away from the viewer.</summary>
public readonly record struct V3(double X, double Y, double Z)
{
    public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static V3 operator *(V3 a, double b) => new(a.X * b, a.Y * b, a.Z * b);
    public double Dot(V3 b) => X * b.X + Y * b.Y + Z * b.Z;
    public double Length => Math.Sqrt(Dot(this));
}

/// <summary>A rotation in space, given by where it takes the three axes: the columns of its matrix.</summary>
public readonly record struct Rotation3(V3 X, V3 Y, V3 Z)
{
    public static Rotation3 Identity => new(new(1, 0, 0), new(0, 1, 0), new(0, 0, 1));

    public V3 Apply(V3 v) => X * v.X + Y * v.Y + Z * v.Z;

    /// <summary>Turns a vector back. The inverse of a rotation is its transpose.</summary>
    public V3 Inverse(V3 v) => new(X.Dot(v), Y.Dot(v), Z.Dot(v));

    /// <summary>This rotation followed by <paramref name="outer"/>, as a member turns with the group it is in.</summary>
    public Rotation3 Then(Rotation3 outer) => new(outer.Apply(X), outer.Apply(Y), outer.Apply(Z));

    /// <summary>Turning about X by <paramref name="x"/>, then about Y by <paramref name="y"/>, then about Z by <paramref name="z"/>: Rz·Ry·Rx.</summary>
    public static Rotation3 Euler(double x, double y, double z)
    {
        double cx = Math.Cos(x), sx = Math.Sin(x);
        double cy = Math.Cos(y), sy = Math.Sin(y);
        double cz = Math.Cos(z), sz = Math.Sin(z);
        return new(
            new(cz * cy, sz * cy, -sy),
            new(cz * sy * sx - sz * cx, sz * sy * sx + cz * cx, cy * sx),
            new(cz * sy * cx + sz * sx, sz * sy * cx - cz * sx, cy * cx));
    }

    /// <summary>The angles <see cref="Euler"/> makes this rotation from. RY comes out within a quarter turn either way.</summary>
    public (double RX, double RY, double R) Angles => (Math.Atan2(Y.Z, Z.Z), Math.Asin(Math.Clamp(-X.Z, -1, 1)), Math.Atan2(X.Y, X.X));
}

/// <summary>A camera at one moment: the values of its commands. Angles are in radians.</summary>
public readonly record struct CameraState(V3 Target, double Distance, double Orbit, double Tilt, double Roll, double Zoom)
{
    /// <summary>
    /// Which way the camera looks: its own axes in space. Orbiting turns it about the vertical and tilting
    /// about its own horizontal axis, both around the target; rolling turns it about the line of sight.
    /// The angles are negated because Y points down: a positive orbit takes the camera to the right, a
    /// positive tilt takes it up, and a positive roll turns the picture clockwise.
    /// </summary>
    public Rotation3 Rotation => Rotation3.Euler(0, 0, -Roll).Then(Rotation3.Euler(-Tilt, -Orbit, 0));

    /// <summary>Where the camera is: the distance away from the target, against the way it looks.</summary>
    public V3 Position => PositionFor(Rotation);

    /// <summary><see cref="Position"/> for a <see cref="Rotation"/> already worked out.</summary>
    internal V3 PositionFor(Rotation3 rotation) => Target + rotation.Apply(new(0, 0, -Distance));

    /// <summary>The values of the commands of one kind, in the order they are written.</summary>
    public double[] Values(CommandKind kind) => kind switch
    {
        CommandKind.CT => new[] { Target.X, Target.Y, Target.Z },
        CommandKind.CD => new[] { Distance },
        CommandKind.CO => new[] { Orbit },
        CommandKind.CI => new[] { Tilt },
        CommandKind.CL => new[] { Roll },
        CommandKind.CF => new[] { Zoom },
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

/// <summary>
/// A member placed in space: where its origin is and how it is turned; its size as the scale of its
/// groups times its own S, and its own V apart.
/// </summary>
public readonly record struct SpacePose(V3 Position, Rotation3 Rotation, double Scale, double ScaleX, double ScaleY);

/// <summary>
/// What the camera shows of a member: the state osu! draws it in, its depth (the distance in front of the
/// camera), and the shear the drawn rectangle leaves out, in radians.
/// </summary>
public readonly record struct SpaceProjection(SpriteState State, double Depth, double Shear);

/// <summary>
/// The space of a tree of groups that uses depth: positions along Z, turns about X and Y, and a camera
/// that looks at it all in perspective. osu! draws flat rectangles only, so what the camera sees of each
/// member is worked out here and baked into plain commands by <see cref="GroupBaker"/>.
///
/// A tree that uses none of it (no Z, RX or RY commands, no camera, no card that faces the camera) stays
/// on the flat path of <see cref="GroupTimeline.Apply(ref SpriteState, in GroupState, SbObjectGroup)"/>,
/// so that what it exports is exactly what it was before groups had depth.
/// </summary>
public static class GroupSpace
{
    /// <summary>
    /// What is known of a tree of groups: whether it uses depth, and a stamp of everything in it. In a tree
    /// that uses depth every member depends on all of the tree: the camera and the settings of space are
    /// shared, and an order by depth would place each member among all the others.
    /// </summary>
    private sealed class TreeCache
    {
        public long Epoch = -1;
        public long Stamp;
        public bool Depth;
    }

    private static readonly ConditionalWeakTable<SbObjectGroup, TreeCache> trees = new();

    /// <summary>Bumped on every edit of any object or group, and on every change of who is in which group.</summary>
    private static long epoch;

    /// <summary>
    /// Forgets what is known of every tree. Edits of single objects do not tell which tree they belong to,
    /// and walking a tree again is cheap next to baking it.
    /// </summary>
    public static void Invalidate() => Interlocked.Increment(ref epoch);

    private static TreeCache Tree(SbObjectGroup group)
    {
        var root = group.Root;
        var tree = trees.GetOrCreateValue(root);
        if (tree.Epoch == epoch) return tree;

        long stamp = 17;
        bool depth = false;
        void Walk(SbObjectGroup g, int level)
        {
            if (level >= SbObjectGroup.MaxDepth) return;
            stamp = (stamp * 31 + g.Id) * 31 + g.Revision;
            depth |= g.Camera != null || g.Commands.Any(c => SbInfo.DepthKinds.Contains(c.Kind));
            foreach (var s in g.Members)
            {
                stamp = (stamp * 31 + s.Id) * 31 + s.Revision;
                depth |= s.FaceCamera || s.DepthCommands.Count > 0;
            }
            foreach (var child in g.Children) Walk(child, level + 1);
        }
        Walk(root, 0);

        tree.Depth = depth;
        tree.Stamp = stamp;
        tree.Epoch = epoch;
        return tree;
    }

    /// <summary>
    /// Makes the members and children of every group those of the storyboard. The Group and Parent setters
    /// keep them up; this drops objects that were taken out of the storyboard. Only for a whole storyboard:
    /// the parts of one that are exported apart share its groups.
    /// </summary>
    public static void Rebind(Storyboard storyboard)
    {
        foreach (var g in storyboard.Groups)
        {
            g.Children.Clear();
            g.Members.Clear();
        }
        foreach (var g in storyboard.Groups) g.Parent?.Children.Add(g);
        foreach (var s in storyboard.AllObjects.OfType<SbSprite>()) s.Group?.Members.Add(s);
        Invalidate();
    }

    /// <summary>Whether the tree a group is in uses depth anywhere, and so is baked through the camera.</summary>
    public static bool UsesDepth(SbObjectGroup? group) => group != null && Tree(group).Depth;

    /// <summary>
    /// A number that is another one whenever something changed that what a member of the group shows
    /// depends on: in a tree that uses depth, anything in the tree; otherwise the chain of groups it is in.
    /// </summary>
    public static long Stamp(SbObjectGroup? group)
    {
        if (group == null) return 0;
        var tree = Tree(group);
        return tree.Depth ? tree.Stamp : group.ChainStamp;
    }

    /// <summary>One value of the commands of one kind, by the same rules as a sprite's own commands.</summary>
    internal static Channel Compile(IEnumerable<SbCommand> commands, CommandKind kind, int component = 0)
    {
        var channel = new Channel();
        foreach (var c in commands)
        {
            if (c.Kind == kind && c.From.Length > component && c.To.Length > component)
                channel.Add(new KeySeg(c.Start, c.End, c.Easing, c.From[component], c.To[component], c.Curve?.Compile()));
        }
        channel.Seal();
        return channel;
    }

    /// <summary>
    /// The value of the commands of one kind at a time, or <paramref name="fallback"/> without any. Worked
    /// out from the commands as they are, for a single look; what is evaluated many times is compiled
    /// once (<see cref="GroupTimeline"/>, <see cref="MemberSpace"/>).
    /// </summary>
    public static double Value(IEnumerable<SbCommand> commands, CommandKind kind, double time, double fallback, int component = 0) =>
        Compile(commands, kind, component).At(time, fallback);

    /// <summary>The camera of the space a group is in, which is that of its outermost group.</summary>
    public static CameraState Camera(SbObjectGroup group, double time) => CameraTimeline.Build(group.Root.Camera?.Commands).Evaluate(time);

    /// <summary>
    /// A sprite placed in the space of its groups: those inside of <paramref name="stopAt"/>, or with null
    /// all of them. A card that faces the camera is turned towards it only when all of them are followed.
    /// </summary>
    public static SpacePose Pose(SbSprite sprite, double time, SbObjectGroup? stopAt = null) => new MemberSpace(sprite, stopAt).Pose(time);

    /// <summary>A group placed in the space of the groups it is in, those inside of <paramref name="stopAt"/>.</summary>
    public static SpacePose Pose(SbObjectGroup group, double time, SbObjectGroup? stopAt = null)
    {
        var s = group.Timeline.Evaluate(time, group);
        var position = new V3(s.X, s.Y, s.Z);
        var rotation = Rotation3.Euler(s.RX, s.RY, s.Rotation);
        double scale = s.Scale;
        int steps = 0;
        for (var g = group.Parent; g != null && g != stopAt && steps++ < SbObjectGroup.MaxDepth; g = g.Parent)
        {
            var t = g.Timeline.Evaluate(time, g);
            var outer = Rotation3.Euler(t.RX, t.RY, t.Rotation);
            position = new V3(t.X, t.Y, t.Z) + outer.Apply(position - new V3(g.PivotX, g.PivotY, 0)) * t.Scale;
            rotation = rotation.Then(outer);
            scale *= t.Scale;
        }
        return new(position, rotation, scale, 1, 1);
    }

    /// <summary>What the camera shows of a member of a tree that uses depth.</summary>
    public static SpaceProjection Project(SbSprite sprite, double time) => new MemberSpace(sprite).Project(time);

    /// <summary>
    /// A member placed in space as the camera shows it, by the perspective of a pinhole camera. A card is
    /// flat, and osu! can only draw it as a rotated rectangle; the one drawn is the nearest to what the
    /// camera sees of the card at its origin.
    /// </summary>
    /// <param name="state">The member's own state; its position, scale and rotation are replaced.</param>
    /// <param name="reference">
    /// The turn the member was given, its own and its groups': of the rotations that draw the same
    /// rectangle the nearest one to it is taken, so that a card goes on turning through full turns.
    /// </param>
    internal static SpaceProjection Project(SpacePose pose, SpriteState state, CameraPose camera, SbObjectGroup root, double reference = 0)
    {
        var k = camera.Rotation;
        var q = k.Inverse(pose.Position - camera.Position);
        double depth = q.Z;
        // At and behind the near plane the member is not shown. Projecting from the plane keeps the numbers
        // finite meanwhile, and what was shown last is held (see GroupBaker).
        double safe = Math.Max(root.NearClip, depth), factor = camera.State.Zoom / safe;

        // The edges of the card as the camera sees them, carried to the screen by the derivative of the
        // projection at the origin of the card. Far edges are not made smaller than near ones: the picture
        // itself is not drawn in perspective.
        var u = k.Inverse(pose.Rotation.X * (pose.Scale * pose.ScaleX));
        var v = k.Inverse(pose.Rotation.Y * (pose.Scale * pose.ScaleY));
        double ux = factor * (u.X - q.X * u.Z / safe), uy = factor * (u.Y - q.Y * u.Z / safe);
        double vx = factor * (v.X - q.X * v.Z / safe), vy = factor * (v.Y - q.Y * v.Z / safe);

        // osu! draws R(θ)·diag(sx, sy). The θ that keeps the most of the edges on the diagonal once they are
        // turned back gives the rectangle with the least squared error; it fits exactly while the edges are
        // square to each other. sx and sy keep their signs, so that a card turning past its side flips over
        // instead of jumping by half a turn.
        double a = ux * ux + vy * vy, b = uy * uy + vx * vx, c = ux * uy - vx * vy;
        double theta = 0.5 * Math.Atan2(2 * c, a - b);
        theta += Math.Round((reference - theta) / Math.PI) * Math.PI;
        double cos = Math.Cos(theta), sin = Math.Sin(theta);
        state.X = 320 + factor * q.X;
        state.Y = 240 + factor * q.Y;
        state.Scale = 1;
        state.ScaleX = ux * cos + uy * sin;
        state.ScaleY = -vx * sin + vy * cos;
        state.Rotation = theta;

        // How far the edges are from square, which no rectangle can show.
        double length = Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
        double shear = length < 1e-12 ? 0 : Math.Asin(Math.Clamp(Math.Abs(ux * vx + uy * vy) / length, 0, 1));

        state.Opacity *= depth <= root.NearClip ? 0 : root.NearFade == 0 ? 1 : Math.Clamp((depth - root.NearClip) / root.NearFade, 0, 1);
        double fog = Math.Clamp((root.FogEnd - depth) / (root.FogEnd - root.FogStart), 0, 1);
        if (root.Fog == FogMode.Fade) state.Opacity *= fog;
        if (root.Fog == FogMode.Darken)
        {
            state.R *= fog;
            state.G *= fog;
            state.B *= fog;
        }
        return new(state, depth, shear);
    }

    /// <summary>A group as the camera shows its origin: for its handles, and for the values the properties show.</summary>
    public static GroupState ProjectGroup(SbObjectGroup group, double time)
    {
        var pose = Pose(group, time);
        var camera = CameraPose.Of(Camera(group, time));
        var shown = Project(pose, new SpriteState(), camera, group.Root).State;
        var angles = pose.Rotation.Angles;
        return new() { X = shown.X, Y = shown.Y, Z = pose.Position.Z, RX = angles.RX, RY = angles.RY, Scale = pose.Scale, Rotation = shown.Rotation };
    }

    /// <summary>
    /// How far something placed at <paramref name="pose"/> has to move in the space of the members of
    /// <paramref name="parent"/> (or of the storyboard, with null) to move by (dx, dy) on the screen: the
    /// ray from the camera through the moved point, met with the plane of the parent through the pose.
    /// False when that plane is turned nearly edge on to the camera, where the least move of the mouse
    /// would throw it far away.
    /// </summary>
    public static bool ScreenOffset(SbObjectGroup tree, SbObjectGroup? parent, SpacePose pose, double time, double dx, double dy, out double x, out double y)
    {
        var camera = CameraPose.Of(Camera(tree, time));
        var k = camera.Rotation;
        double zoom = camera.State.Zoom;
        var q = k.Inverse(pose.Position - camera.Position);
        var plane = parent == null ? new SpacePose(default, Rotation3.Identity, 1, 1, 1) : Pose(parent, time);
        var normal = plane.Rotation.Z;

        // The point on the screen, measured from the middle, and the ray through it moved.
        double sx = q.X * zoom / q.Z, sy = q.Y * zoom / q.Z;
        var ray = k.Apply(new((sx + dx) / zoom, (sy + dy) / zoom, 1));
        double denominator = normal.Dot(ray);
        if (Math.Abs(denominator) / ray.Length < Math.Sin(Math.PI / 36) || Math.Abs(plane.Scale) < 1e-9)
        {
            x = y = 0;
            return false;
        }
        double distance = normal.Dot(pose.Position - camera.Position) / denominator;
        var delta = plane.Rotation.Inverse(camera.Position + ray * distance - pose.Position) * (1 / plane.Scale);
        x = delta.X;
        y = delta.Y;
        return true;
    }

    /// <summary>Whether settings of a space can be: a near plane in front of the camera, and fog that ends behind where it starts.</summary>
    public static bool ValidSpace(double near, double fade, double from, double to) =>
        double.IsFinite(near + fade + from + to) && near > 0 && fade >= 0 && from > 0 && to > from;
}

/// <summary>A camera at one moment with its rotation and position worked out, which every projection at that moment needs.</summary>
internal readonly record struct CameraPose(CameraState State, Rotation3 Rotation, V3 Position)
{
    public static CameraPose Of(CameraState state)
    {
        var rotation = state.Rotation;
        return new(state, rotation, state.PositionFor(rotation));
    }
}

/// <summary>Compiled commands of a camera, evaluated by the same rules as a sprite's own commands.</summary>
internal sealed class CameraTimeline
{
    private readonly Channel targetX, targetY, targetZ, distance, orbit, tilt, roll, zoom;

    private CameraTimeline(IEnumerable<SbCommand> commands)
    {
        targetX = GroupSpace.Compile(commands, CommandKind.CT, 0);
        targetY = GroupSpace.Compile(commands, CommandKind.CT, 1);
        targetZ = GroupSpace.Compile(commands, CommandKind.CT, 2);
        distance = GroupSpace.Compile(commands, CommandKind.CD);
        orbit = GroupSpace.Compile(commands, CommandKind.CO);
        tilt = GroupSpace.Compile(commands, CommandKind.CI);
        roll = GroupSpace.Compile(commands, CommandKind.CL);
        zoom = GroupSpace.Compile(commands, CommandKind.CF);
    }

    /// <param name="commands">The commands of a camera; with null those of none, which is the default camera.</param>
    public static CameraTimeline Build(IEnumerable<SbCommand>? commands) => new(commands ?? Array.Empty<SbCommand>());

    public CameraState Evaluate(double time) => new(
        new V3(targetX.At(time, 320), targetY.At(time, 240), targetZ.At(time, 0)),
        distance.At(time, 1000), orbit.At(time, 0), tilt.At(time, 0), roll.At(time, 0), zoom.At(time, 1000));
}

/// <summary>
/// A sprite in the space of its groups, with everything its place depends on compiled once: its own
/// timeline and depth commands, the timelines of its groups, and the camera. A bake asks where a member
/// is at hundreds of moments, and looking all of that up anew each time cost more than the arithmetic.
/// Holds while none of it is edited; not to be shared between threads.
/// </summary>
internal sealed class MemberSpace
{
    private readonly SbSprite sprite;
    private readonly SpriteTimeline timeline;
    private readonly Channel z, rx, ry;
    /// <summary>The groups whose motion is followed, from the innermost, with their timelines.</summary>
    private readonly SbObjectGroup[] groups;
    private readonly GroupTimeline[] timelines;
    /// <summary>The states of the groups at the time <see cref="Pose(double, in SpriteState, in Rotation3)"/> last placed the sprite.</summary>
    private readonly GroupState[] states;
    /// <summary>The outermost group, whose camera and settings of space are those of the whole tree.</summary>
    private readonly SbObjectGroup? root;
    /// <summary>Null when not all groups are followed: then there is no camera to project through.</summary>
    private readonly CameraTimeline? camera;
    private readonly bool facesCamera;

    /// <param name="stopAt">The group whose motion and that of the groups it is in is left out; with null none is.</param>
    public MemberSpace(SbSprite sprite, SbObjectGroup? stopAt = null)
    {
        this.sprite = sprite;
        timeline = sprite.Timeline;
        z = GroupSpace.Compile(sprite.DepthCommands, CommandKind.Z);
        rx = GroupSpace.Compile(sprite.DepthCommands, CommandKind.RX);
        ry = GroupSpace.Compile(sprite.DepthCommands, CommandKind.RY);
        var chain = new List<SbObjectGroup>();
        int steps = 0;
        for (var g = sprite.Group; g != null && g != stopAt && steps++ < SbObjectGroup.MaxDepth; g = g.Parent) chain.Add(g);
        groups = chain.ToArray();
        timelines = groups.Select(g => g.Timeline).ToArray();
        states = new GroupState[groups.Length];
        root = sprite.Group?.Root;
        if (stopAt == null && root != null) camera = CameraTimeline.Build(root.Camera?.Commands);
        // A card faces the camera of the whole space, so only while it is followed all the way out.
        facesCamera = sprite.FaceCamera && camera != null;
    }

    /// <summary>The value of the sprite's own Z, RX or RY at a time.</summary>
    public double Own(CommandKind kind, double time) => kind switch
    {
        CommandKind.Z => z.At(time, 0),
        CommandKind.RX => rx.At(time, 0),
        CommandKind.RY => ry.At(time, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public SpacePose Pose(double time)
    {
        var rotation = facesCamera ? CameraPose.Of(camera!.Evaluate(time)).Rotation : Rotation3.Identity;
        return Pose(time, timeline.Evaluate(time, sprite), rotation);
    }

    /// <param name="own">The sprite's own state at the time.</param>
    /// <param name="cameraRotation">Which way the camera looks at the time; only a card that faces it asks.</param>
    private SpacePose Pose(double time, in SpriteState own, in Rotation3 cameraRotation)
    {
        var position = new V3(own.X, own.Y, z.At(time, 0));
        var turn = Rotation3.Euler(rx.At(time, 0), ry.At(time, 0), own.Rotation);
        var rotation = turn;
        double scale = own.Scale;
        for (int i = 0; i < groups.Length; i++)
        {
            var g = groups[i];
            var t = states[i] = timelines[i].Evaluate(time, g);
            var r = Rotation3.Euler(t.RX, t.RY, t.Rotation);
            position = new V3(t.X, t.Y, t.Z) + r.Apply(position - new V3(g.PivotX, g.PivotY, 0)) * t.Scale;
            rotation = rotation.Then(r);
            scale *= t.Scale;
        }
        // A card that faces the camera is not turned by its groups: only by itself, as seen from the camera.
        if (facesCamera) rotation = turn.Then(cameraRotation);
        return new(position, rotation, scale, own.ScaleX, own.ScaleY);
    }

    /// <summary>What the camera shows of the sprite. Only while all its groups are followed.</summary>
    public SpaceProjection Project(double time)
    {
        var lens = CameraPose.Of(camera!.Evaluate(time));
        var own = timeline.Evaluate(time, sprite);
        var pose = Pose(time, own, lens.Rotation);
        // The turn the sprite was given: its own, its groups' and that of the camera's roll, which turns
        // everything on the screen. A card facing the camera turns only by itself.
        double reference = own.Rotation + (sprite.FaceCamera ? 0 : lens.State.Roll);
        if (!sprite.FaceCamera)
        {
            foreach (var state in states) reference += state.Rotation;
        }
        return GroupSpace.Project(pose, own, lens, root!, reference);
    }
}
