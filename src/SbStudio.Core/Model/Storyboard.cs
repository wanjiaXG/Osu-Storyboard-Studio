namespace SbStudio.Core;

public abstract class SbNode
{
    public VariableLine? VariableSource;
    public int VariablePart;
    public abstract SbNode Clone();
}

/// <summary>A single F/M/MX/MY/S/V/R/C/P command. Shorthand forms are expanded on parse.</summary>
public sealed class SbCommand : SbNode
{
    public CommandKind Kind;
    public int Easing;
    public int Start;
    public int End;
    public double[] From = Array.Empty<double>();
    public double[] To = Array.Empty<double>();
    /// <summary>Only for P commands: H, V or A.</summary>
    public char Param = 'A';
    /// <summary>
    /// A transition drawn by hand, which then takes the place of <see cref="Easing"/>. Only commands of a
    /// group of sprites have one: see <see cref="EasingCurve"/>.
    /// </summary>
    public EasingCurve? Curve;

    public SbCommand() { }

    public SbCommand(CommandKind kind, int easing, int start, int end, double[] from, double[]? to = null)
    {
        Kind = kind;
        Easing = easing;
        Start = start;
        End = end;
        From = from;
        To = to ?? (double[])from.Clone();
    }

    public static SbCommand Parameter(int start, int end, char param) =>
        new() { Kind = CommandKind.P, Start = start, End = end, Param = param };

    public override SbNode Clone() => CloneCommand();

    public SbCommand CloneCommand() => new()
    {
        Kind = Kind,
        Easing = Easing,
        Start = Start,
        End = End,
        From = (double[])From.Clone(),
        To = (double[])To.Clone(),
        Param = Param,
        Curve = Curve?.Clone(),
        VariableSource = VariableSource, VariablePart = VariablePart,
    };
}

/// <summary>Loop or trigger: a set of commands timed relative to the group.</summary>
public abstract class SbGroup : SbNode
{
    public List<SbCommand> Commands = new();

    public int CommandsStart => Commands.Count == 0 ? 0 : Commands.Min(c => c.Start);
    public int CommandsEnd => Commands.Count == 0 ? 0 : Commands.Max(c => c.End);
}

public sealed class SbLoop : SbGroup
{
    public int Start;
    public int Count = 1;

    /// <summary>osu! always plays a loop at least once.</summary>
    public int Iterations => Math.Max(1, Count);
    public int IterationLength => Math.Max(0, CommandsEnd - CommandsStart);
    public int AbsoluteStart => Start + CommandsStart;
    public int AbsoluteEnd => AbsoluteStart + IterationLength * Iterations;

    /// <summary>
    /// Which pass of the loop a time falls in, counted from 0. Before the loop it is the first, and from
    /// the start of the last one on it stays the last, also once the loop is over.
    /// </summary>
    public int IterationAt(double time)
    {
        int length = IterationLength;
        return length <= 0 ? 0 : (int)Math.Clamp(Math.Floor((time - AbsoluteStart) / length), 0, Iterations - 1);
    }

    /// <summary>
    /// The time the commands of the loop count from in the pass a time falls in. Taken off a time within
    /// the loop it gives the moment of the pass, which is the same moment in every pass: between where
    /// the first command begins and where the last one ends.
    /// </summary>
    public int StartAt(double time) => Start + IterationAt(time) * IterationLength;

    public override SbNode Clone() => new SbLoop
    {
        Start = Start,
        Count = Count,
        VariableSource = VariableSource, VariablePart = VariablePart,
        Commands = Commands.Select(c => c.CloneCommand()).ToList(),
    };
}

public sealed class SbTrigger : SbGroup
{
    public string Name = "HitSoundClap";
    public int Start;
    public int End;
    public int Group;

    /// <summary>A finite origin for editing relative commands when the trigger has no lower bound.</summary>
    public int EditOffset => Start == int.MinValue ? 0 : Start;

    public void ShiftWindow(int delta)
    {
        if (Start != int.MinValue) Start = (int)Math.Clamp((long)Start + delta, int.MinValue + 1L, int.MaxValue);
        if (End != int.MaxValue) End = (int)Math.Clamp((long)End + delta, int.MinValue, int.MaxValue - 1L);
    }

    public override SbNode Clone() => new SbTrigger
    {
        Name = Name,
        Start = Start,
        End = End,
        Group = Group,
        VariableSource = VariableSource, VariablePart = VariablePart,
        Commands = Commands.Select(c => c.CloneCommand()).ToList(),
    };
}

/// <summary>
/// Editor-only parent transform shared by several sprites. The osb format has nothing like it: on export
/// the group's motion is baked into each member's own commands.
///
/// A member is shown at  Position(t) + Rotate(Rotation(t)) * Scale(t) * (memberPosition - Pivot),
/// with its own scale multiplied by Scale(t) and its own rotation increased by Rotation(t). With no group
/// commands that is exactly the member's own position, so grouping by itself changes nothing.
///
/// A group is to the editor what a composition inside a composition is to a video editor: one thing in
/// the timeline, with its members out of sight until it is gone into. Groups can be members of groups
/// (<see cref="Parent"/>); a sprite then moves with every group it is in, the innermost first.
/// </summary>
public sealed class SbObjectGroup
{
    private static int nextId;

    /// <summary>Runtime-only identity, stable for the lifetime of the instance.</summary>
    public readonly int Id = Interlocked.Increment(ref nextId);

    /// <summary>How deep groups are followed into each other: more than anyone nests them, and an end to a circle in a damaged file.</summary>
    public const int MaxDepth = 32;

    /// <summary>The group this one is a member of; null for a group of the storyboard itself.</summary>
    private SbObjectGroup? parent;
    internal readonly HashSet<SbObjectGroup> Children = new();
    internal readonly HashSet<SbSprite> Members = new();
    public SbObjectGroup? Parent
    {
        get => parent;
        set { parent?.Children.Remove(this); parent = value; parent?.Children.Add(this); GroupSpace.Invalidate(); }
    }
    public SbObjectGroup Root { get { var g = this; for (int i = 0; g.Parent != null && i < MaxDepth; i++) g = g.Parent; return g; } }
    public PluginLink? Plugin;
    public SbObjectGroup? PluginRoot
    {
        get { int depth=0; for(var g=this; g!=null && depth++<MaxDepth; g=g.Parent) if(g.Plugin!=null) return g; return null; }
    }
    public SbCamera? Camera;
    public double NearClip = 100, NearFade = 200, FogStart = 2000, FogEnd = 6000;
    public FogMode Fog;
    public bool CompositionWarning;
    public bool CameraActive => Parent == null && Camera != null;
    public void CopySpaceFrom(SbObjectGroup other)
    {
        Camera = other.Camera?.Clone(); NearClip = other.NearClip; NearFade = other.NearFade;
        Fog = other.Fog; FogStart = other.FogStart; FogEnd = other.FogEnd;
    }

    /// <summary>Whether this group is <paramref name="other"/> or lies in it, however deep.</summary>
    public bool IsWithin(SbObjectGroup other)
    {
        int steps = 0;
        for (var g = this; g != null && steps < MaxDepth; g = g.Parent, steps++)
            if (g == other) return true;
        return false;
    }

    /// <summary>How many groups this one is in.</summary>
    public int Depth
    {
        get
        {
            int depth = 0;
            for (var g = Parent; g != null && depth < MaxDepth; g = g.Parent) depth++;
            return depth;
        }
    }

    /// <summary>
    /// A number that is another one whenever something changed that the place of a member depends on:
    /// this group, one of the groups it is in, or which those are.
    /// </summary>
    public long ChainStamp
    {
        get
        {
            long stamp = 17;
            int steps = 0;
            for (var g = this; g != null && steps < MaxDepth; g = g.Parent, steps++) stamp = (stamp * 31 + g.Id) * 31 + g.Revision;
            return stamp;
        }
    }

    /// <summary>The state of the group as it is shown: its own, carried through the groups it is in.</summary>
    public GroupState WorldState(double time)
    {
        if (GroupSpace.UsesDepth(this)) return GroupSpace.ProjectGroup(this, time);
        var state = Timeline.Evaluate(time, this);
        int steps = 0;
        for (var g = Parent; g != null && steps < MaxDepth; g = g.Parent, steps++) GroupTimeline.Apply(ref state, g.Timeline.Evaluate(time, g), g);
        return state;
    }

    public string Name = "";
    /// <summary>The point of the members' own coordinate space the group rotates and scales around.</summary>
    public double PivotX = 320, PivotY = 240;
    /// <summary>Where the pivot is shown while no move command says otherwise.</summary>
    public double X = 320, Y = 240;
    /// <summary>M, MX, MY, S and R commands, timed absolutely.</summary>
    public List<SbCommand> Commands = new();
    /// <summary>Timeline row is unfolded. Session-only, not saved.</summary>
    public bool Expanded = true;

    public int BakeStep = BakeOptions.DefaultMinStep;
    public double BakeTolerance = BakeOptions.DefaultTolerance;
    internal bool HasBakePrecision;
    private BakeOptions? bake;

    /// <summary>Precision for this group's complete motion, respecting stricter enclosing groups.</summary>
    public BakeOptions Bake
    {
        get
        {
            var precision = BakeOptions.ForChain(this);
            if (bake == null || bake.MinStep != precision.Step || bake.PositionTolerance != precision.Tolerance)
                bake = BakeOptions.Of(precision.Step, precision.Tolerance);
            return bake;
        }
    }

    private GroupTimeline? timeline;
    private int timelineRevision = -1;

    public int Revision { get; private set; }

    public void Touch() { Revision++; GroupSpace.Invalidate(); }

    /// <summary>Only moving, uniform scaling and rotating make sense for a whole group.</summary>
    public static bool Accepts(CommandKind kind) =>
        kind is CommandKind.M or CommandKind.MX or CommandKind.MY or CommandKind.S or CommandKind.R or CommandKind.Z or CommandKind.RX or CommandKind.RY;

    public GroupTimeline Timeline
    {
        get
        {
            if (timeline == null || timelineRevision != Revision)
            {
                timeline = GroupTimeline.Build(this);
                timelineRevision = Revision;
            }
            return timeline;
        }
    }

    public void ShiftTime(int delta)
    {
        foreach (var c in Commands.Concat(Camera?.Commands ?? Enumerable.Empty<SbCommand>()))
        {
            c.Start += delta;
            c.End += delta;
        }
        Touch();
    }

    /// <summary>Moves the group's position: the default one and every move command.</summary>
    public void Offset(double dx, double dy)
    {
        X += dx;
        Y += dy;
        foreach (var c in Commands)
        {
            switch (c.Kind)
            {
                case CommandKind.M:
                    c.From[0] += dx; c.From[1] += dy;
                    c.To[0] += dx; c.To[1] += dy;
                    break;
                case CommandKind.MX:
                    c.From[0] += dx; c.To[0] += dx;
                    break;
                case CommandKind.MY:
                    c.From[0] += dy; c.To[0] += dy;
                    break;
            }
        }
        Touch();
    }

    /// <summary>A copy that is in the same group as this one; copies of whole storyboards put it into the copy of that group.</summary>
    public SbObjectGroup Clone() => new()
    {
        parent = Parent,
        Plugin = Plugin?.Clone(),
        CompositionWarning = CompositionWarning,
        Camera = Camera?.Clone(), NearClip = NearClip, NearFade = NearFade, Fog = Fog, FogStart = FogStart, FogEnd = FogEnd,
        Name = Name,
        PivotX = PivotX,
        PivotY = PivotY,
        X = X,
        Y = Y,
        Commands = Commands.Select(c => c.CloneCommand()).ToList(),
        Expanded = Expanded,
        BakeStep = BakeStep,
        BakeTolerance = BakeTolerance,
        HasBakePrecision = HasBakePrecision,
    };
}

public abstract class SbObject
{
    public VariableLine? VariableSource;
    private static int nextId;

    /// <summary>Runtime-only identity, stable for the lifetime of the instance.</summary>
    public readonly int Id = Interlocked.Increment(ref nextId);

    public SbLayer Layer = SbLayer.Foreground;
    public string Path = "";

    // Editor-only data. Stored in the project's osb as "//@" lines, never exported.
    public string? Name;
    private bool hidden;
    internal bool OwnHidden=>hidden;
    public bool Hidden {get=>hidden || this is SbSprite s && s.Group?.PluginRoot?.Plugin?.Hidden==true;set=>hidden=value;}
    /// <summary>Explicit editor choice; null uses the automatically detected default.</summary>
    public bool? LockOverride;
    public bool DefaultLocked { get; internal set; }
    public bool Locked { get => this is SbSprite s && s.Group?.PluginRoot?.Plugin?.Locked==true || (LockOverride ?? DefaultLocked); set => LockOverride = value; }
    /// <summary>Plain "//" comment lines that preceded this object.</summary>
    public List<string> Comments = new();
    /// <summary>Timeline row is unfolded. Session-only, not saved.</summary>
    public bool Expanded;
    /// <summary>
    /// The difficulty this object belongs to, by the name of its .osu file: it is shown with that
    /// difficulty only, and its script goes into that file. Null for the storyboard all difficulties
    /// share, the .osb.
    /// </summary>
    public string? Difficulty;

    public string DisplayName => this is SbSprite { BeatmapBackgroundPlaceholder: true } ? L.T("props.beatmapBg")
        : !string.IsNullOrEmpty(Name) ? Name! : System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>
    /// What <see cref="Problems.FindMissing"/> last found out about the files of this object, and what it
    /// held for: the path as it was, the number of frames, and who was asked whether a file is there.
    /// Not copied with the object.
    /// </summary>
    internal (string? Path, int Frames, object? Asked, string? Missing, int Count) FilesChecked;

    public abstract SbObject Clone();

    /// <summary>Moves everything the object does by a number of milliseconds.</summary>
    public abstract void ShiftTime(int delta);

    protected void CopyBaseTo(SbObject other)
    {
        other.Layer = Layer;
        other.Path = Path;
        other.Name = Name;
        other.Hidden = hidden;
        other.LockOverride = LockOverride;
        other.DefaultLocked = DefaultLocked;
        other.Comments = new List<string>(Comments);
        other.Expanded = Expanded;
        other.Difficulty = Difficulty;
        other.VariableSource = VariableSource;
    }
}

public sealed class SbSprite : SbObject
{
    /// <summary>An editor background declaration: only its image path and difficulty ownership are editable.</summary>
    public bool BeatmapBackgroundPlaceholder;
    public SbOrigin Origin = SbOrigin.Centre;
    public double X = 320;
    public double Y = 240;

    public bool IsAnimation;
    public int FrameCount = 1;
    public double FrameDelay = 100;
    public SbLoopType LoopType = SbLoopType.LoopForever;

    public List<SbNode> Nodes = new();

    /// <summary>The group whose motion this sprite follows, if any. Must be one of the storyboard's groups.</summary>
    private SbObjectGroup? group;
    public SbObjectGroup? Group
    {
        get => group;
        set { group?.Members.Remove(this); group = value; group?.Members.Add(this); GroupSpace.Invalidate(); }
    }

    /// <summary>
    /// For a copy that is in no storyboard, such as what a member is exported as: names the group it would
    /// be in without making it a member there. Being one would have it counted with the group's tree, and
    /// setting <see cref="Group"/> makes every tree be walked again, once for each member exported.
    /// </summary>
    internal void PlaceCopyIn(SbObjectGroup? group) => this.group = group;
    public List<SbCommand> DepthCommands = new();
    public bool FaceCamera;
    public bool CompositionWarning;
    public bool BakeLoopTooLong;

    private SpriteTimeline? timeline;
    private int timelineRevision = -1;

    /// <summary>Bumped by <see cref="Touch"/> whenever the sprite is edited in place.</summary>
    public int Revision { get; private set; }

    public void Touch() { Revision++; GroupSpace.Invalidate(); }
    /// <summary>For a copy that is in no storyboard (see <see cref="PlaceCopyIn"/>): only its own caches are out of date.</summary>
    internal void TouchCopy() => Revision++;

    /// <summary>Compiled, cached view of the commands used for evaluation.</summary>
    public SpriteTimeline Timeline
    {
        get
        {
            if (timeline == null || timelineRevision != Revision)
            {
                timeline = SpriteTimeline.Build(this);
                timelineRevision = Revision;
            }
            return timeline;
        }
    }

    private Problem[]? problems;
    private int problemsRevision = -1;
    private long problemsChain;

    /// <summary>
    /// What is wrong with the sprite's commands, see <see cref="Problems.Find(SbSprite, List{Problem})"/>.
    /// Kept like the timeline until the sprite is edited: a storyboard is looked through after every edit,
    /// and of its tens of thousands of sprites an edit changes one.
    /// </summary>
    public Problem[] CommandProblems
    {
        get
        {
            if (problems == null || problemsRevision != Revision || problemsChain != GroupSpace.Stamp(Group))
            {
                problems = Problems.Of(this);
                problemsRevision = Revision;
                problemsChain = GroupSpace.Stamp(Group);
            }
            return problems;
        }
    }

    /// <summary>
    /// The state the sprite is meant to be in: its own commands with the motion of its group applied,
    /// exactly. What is shown is <see cref="ShownState"/>, which is as near to this as the export gets.
    /// </summary>
    public SpriteState WorldState(double time)
    {
        if (Group != null && GroupSpace.UsesDepth(Group)) return GroupSpace.Project(this, time).State;
        var state = Timeline.Evaluate(time, this);
        int steps = 0;
        for (var g = Group; g != null && steps < SbObjectGroup.MaxDepth; g = g.Parent, steps++) GroupTimeline.Apply(ref state, g.Timeline.Evaluate(time, g), g);
        return state;
    }

    private SbSprite? exported;
    private (int Revision, SbObjectGroup? Group, long Groups, (int, double, double, double, int) Options) exportedFor;
    private int exportedCommands;

    /// <summary>
    /// The sprite as it is written when the storyboard is exported: itself while it follows no group, and
    /// otherwise a copy with the motion of the group in commands of its own (<see cref="GroupBaker"/>).
    /// osu! knows nothing of groups and plays those commands, which follow the group as closely as the
    /// options say and no closer. Kept until the sprite, its group or the options change.
    /// </summary>
    public SbSprite Exported(BakeOptions? options = null)
    {
        if (Group == null) return this;
        options ??= Group.Bake;
        if (!IsExportedFresh(options))
        {
            exported = GroupBaker.BakeSprite(this, options);
            exportedFor = (Revision, Group, GroupSpace.Stamp(Group), options.Key);
            exportedCommands = 0;
            foreach (var node in exported.Nodes) exportedCommands += node is SbGroup block ? 1 + block.Commands.Count : 1;
        }
        return exported!;
    }

    /// <summary>Whether <see cref="Exported"/> has its answer at hand, or would have to work it out first.</summary>
    public bool IsExportedFresh(BakeOptions? options = null) =>
        Group == null || (exported != null && exportedFor == (Revision, Group, GroupSpace.Stamp(Group), (options ?? Group.Bake).Key));

    /// <summary>How many lines of commands the sprite has in the exported file; a loop or trigger counts with those in it.</summary>
    public int ExportedCommands(BakeOptions? options = null)
    {
        if (Group != null)
        {
            Exported(options);
            return exportedCommands;
        }
        int count = 0;
        foreach (var node in Nodes) count += node is SbGroup block ? 1 + block.Commands.Count : 1;
        return count;
    }

    /// <summary>
    /// The state the sprite is shown in: that of the commands it is exported with, which is what the game
    /// plays. For a sprite that follows a group it differs from <see cref="WorldState"/> by as much as
    /// the options allow.
    /// </summary>
    public SpriteState ShownState(double time, BakeOptions? options = null)
    {
        var shown = Exported(options);
        return shown.Timeline.Evaluate(time, shown);
    }

    public IEnumerable<SbCommand> AllCommands()
    {
        foreach (var node in Nodes)
        {
            if (node is SbCommand c) yield return c;
            else if (node is SbGroup g)
                foreach (var gc in g.Commands) yield return gc;
        }
    }

    /// <summary>File name of an animation frame: "a/b.png" + 3 => "a/b3.png".</summary>
    public static string FramePath(string path, int frame)
    {
        int dot = path.LastIndexOf('.');
        int slash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        if (dot <= slash) return path + frame;
        return path.Substring(0, dot) + frame + path.Substring(dot);
    }

    /// <summary>Shifts every absolute time of the sprite (commands, loops, triggers).</summary>
    public override void ShiftTime(int delta)
    {
        foreach (var c in DepthCommands) { c.Start += delta; c.End += delta; }
        foreach (var node in Nodes)
        {
            switch (node)
            {
                case SbCommand c: c.Start += delta; c.End += delta; break;
                case SbLoop l: l.Start += delta; break;
                case SbTrigger t: t.ShiftWindow(delta); break;
            }
        }
        Touch();
    }

    public override SbObject Clone()
    {
        var s = new SbSprite
        {
            BeatmapBackgroundPlaceholder = BeatmapBackgroundPlaceholder,
            Origin = Origin,
            X = X,
            Y = Y,
            IsAnimation = IsAnimation,
            FrameCount = FrameCount,
            FrameDelay = FrameDelay,
            LoopType = LoopType,
            Nodes = Nodes.Select(n => n.Clone()).ToList(),
            // The copy stays in the same group; whole-storyboard copies remap this afterwards.
            group = Group,
            DepthCommands = DepthCommands.Select(c => c.CloneCommand()).ToList(), FaceCamera = FaceCamera, CompositionWarning = CompositionWarning,
        };
        CopyBaseTo(s);
        return s;
    }
}

public sealed class SbSample : SbObject
{
    public int Time;
    public double Volume = 100;

    public override SbObject Clone()
    {
        var s = new SbSample { Time = Time, Volume = Volume };
        CopyBaseTo(s);
        return s;
    }

    public override void ShiftTime(int delta) => Time += delta;
}

/// <summary>
/// The beatmap's video: a file that plays behind every layer from a time on. Its line in the script reads
/// like that of a sprite, but it takes no commands: where it sits, how large it is and how it fades in
/// and out is osu!'s doing (see <see cref="VideoInfo"/>).
/// </summary>
public sealed class SbVideo : SbObject
{
    /// <summary>When the video begins, in milliseconds. May be negative.</summary>
    public int Time;

    public SbVideo() => Layer = SbLayer.Background;

    public override SbObject Clone()
    {
        var v = new SbVideo { Time = Time };
        CopyBaseTo(v);
        return v;
    }

    public override void ShiftTime(int delta) => Time += delta;
}

public sealed class Storyboard
{
    public List<StoryboardVariable> Variables = new();
    /// <summary>
    /// The objects of the storyboard as it is shown: those of the selected difficulty, then the ones all
    /// difficulties share. Order only matters between objects of the same layer: later ones are drawn in front.
    /// </summary>
    public List<SbObject> Objects = new();

    /// <summary>
    /// The objects of the difficulties that are not selected. They are saved with the project and go
    /// through undo with everything else, but nothing shows or edits them until their difficulty is selected.
    /// </summary>
    public List<SbObject> Parked = new();

    /// <summary>Parent transforms that sprites can follow. Editor-only; see <see cref="SbObjectGroup"/>.</summary>
    public List<SbObjectGroup> Groups = new();

    public IEnumerable<SbSprite> Sprites => Objects.OfType<SbSprite>();
    public IEnumerable<SbSample> Samples => Objects.OfType<SbSample>();
    public IEnumerable<SbVideo> Videos => Objects.OfType<SbVideo>();

    /// <summary>Everything the project holds: what is shown and what is parked.</summary>
    public IEnumerable<SbObject> AllObjects => Objects.Concat(Parked);

    /// <summary>The sprites that are members of a group themselves, among the objects that are shown.</summary>
    public IEnumerable<SbSprite> MembersOf(SbObjectGroup group) => Sprites.Where(s => s.Group == group);

    /// <summary>The groups that are members of a group themselves; with null those of the storyboard.</summary>
    public IEnumerable<SbObjectGroup> ChildrenOf(SbObjectGroup? group) => Groups.Where(g => g.Parent == group);

    /// <summary>The groups in a group, however deep, without the group itself.</summary>
    public IEnumerable<SbObjectGroup> GroupsIn(SbObjectGroup group) => Groups.Where(g => g != group && g.IsWithin(group));

    /// <summary>The sprites in a group, however deep: those shown, and with <paramref name="parked"/> those of the other difficulties too.</summary>
    public IEnumerable<SbSprite> SpritesIn(SbObjectGroup group, bool parked = false) =>
        (parked ? AllObjects : Objects).OfType<SbSprite>().Where(s => s.Group != null && s.Group.IsWithin(group));

    /// <summary>Whether any sprite is in the group, however deep, parked ones included. A group without one can go.</summary>
    public bool HasMembers(SbObjectGroup group) => AllObjects.Any(o => o is SbSprite s && s.Group != null && s.Group.IsWithin(group));

    /// <summary>
    /// Where each group stands when the storyboard is drawn: a number that is higher the further in
    /// front. A group is in front of the ones that come before it in the list: so the order of the groups
    /// is who covers whom, and two groups that change places change that. Within a group, the groups in it
    /// are in front of the sprites that are its members themselves, as the groups of the storyboard are
    /// in front of its sprites. All of that holds layer by layer: see <see cref="GroupedInDrawOrder"/>.
    /// </summary>
    public Dictionary<SbObjectGroup, int> GroupRanks()
    {
        var ranks = new Dictionary<SbObjectGroup, int>(Groups.Count);
        if (Groups.Count == 0) return ranks;
        var known = new HashSet<SbObjectGroup>(Groups);
        var inside = new Dictionary<SbObjectGroup, List<SbObjectGroup>>();
        var tops = new List<SbObjectGroup>();
        foreach (var group in Groups)
        {
            if (group.Parent == null || !known.Contains(group.Parent))
            {
                tops.Add(group);
                continue;
            }
            if (!inside.TryGetValue(group.Parent, out var list)) inside[group.Parent] = list = new List<SbObjectGroup>();
            list.Add(group);
        }

        int next = 0;
        void Visit(SbObjectGroup group, int depth)
        {
            // Its own number is that of its sprites; what is in it comes after, and so in front.
            if (!ranks.TryAdd(group, next)) return;
            next++;
            if (depth >= SbObjectGroup.MaxDepth || !inside.TryGetValue(group, out var list)) return;
            foreach (var child in list) Visit(child, depth + 1);
        }
        foreach (var top in tops) Visit(top, 0);
        // Groups that are in each other in a circle are reached from none of the tops.
        foreach (var group in Groups) Visit(group, 0);
        return ranks;
    }

    /// <summary>
    /// The sprites of a list that are in groups, in the order they are put on top of the others, the
    /// hindmost first: by their groups (<see cref="GroupRanks"/>), and within a group in the order of the
    /// list.
    ///
    /// A sprite stays in the layer it is of, grouped or not: osu! has its layers, and a group is in none.
    /// What a group has is precedence: a storyboard is put together from the bottom up, layer by layer,
    /// each layer first with the sprites that are in no group and then, on top of them, with those that
    /// are, in this order. So within a layer whatever is in a group covers what is in none, a later group
    /// covers an earlier one, and a sprite of the Background layer is behind every sprite of the
    /// Foreground layer whatever groups the two are in. This is the one order both the preview and the
    /// exported file go by.
    /// </summary>
    public static List<SbSprite> GroupedInDrawOrder(IEnumerable<SbObject> objects, Dictionary<SbObjectGroup, int> ranks)
    {
        var grouped = new List<(SbSprite Sprite, int Rank, int Place)>();
        int place = 0;
        foreach (var obj in objects)
        {
            place++;
            if (obj is not SbSprite { Group: not null } sprite) continue;
            grouped.Add((sprite, ranks.GetValueOrDefault(sprite.Group, -1), place));
        }
        grouped.Sort((a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : a.Place.CompareTo(b.Place));
        var result = new List<SbSprite>(grouped.Count);
        foreach (var entry in grouped) result.Add(entry.Sprite);
        return result;
    }

    /// <summary>
    /// Takes away the groups that no sprite is in any more, and sets right what cannot be: a group that is
    /// in one the storyboard does not have, or in itself by way of others, stands in the storyboard.
    /// </summary>
    /// <returns>True when a group went.</returns>
    public bool PruneGroups()
    {
        var known = new HashSet<SbObjectGroup>(Groups);
        foreach (var group in Groups)
        {
            if (group.Parent == null) continue;
            bool sound = known.Contains(group.Parent);
            int steps = 0;
            for (var g = group.Parent; sound && g != null; g = g.Parent)
                if (g == group || ++steps > SbObjectGroup.MaxDepth) sound = false;
            if (!sound) group.Parent = null;
        }

        var used = new HashSet<SbObjectGroup>();
        foreach (var obj in AllObjects)
        {
            if (obj is not SbSprite { Group: not null } sprite) continue;
            int steps = 0;
            for (var g = sprite.Group; g != null && steps < SbObjectGroup.MaxDepth && used.Add(g); g = g.Parent) steps++;
        }
        foreach(var plugin in Groups.Where(g=>g.Plugin!=null))
            for(var g=plugin;g!=null && used.Add(g);g=g.Parent) { }
        return Groups.RemoveAll(g => !used.Contains(g)) > 0;
    }

    /// <summary>
    /// Puts every object where the selected difficulty wants it. Its own objects come first: osu! reads a
    /// difficulty's .osu before the shared .osb, which draws them behind. Then the shared ones. Objects of
    /// the other difficulties are parked. An object that names a difficulty the project does not have
    /// becomes a shared one.
    /// </summary>
    /// <param name="selected">File name of the selected difficulty; null or empty for none.</param>
    /// <returns>True when anything moved.</returns>
    public bool Arrange(string? selected, IReadOnlyCollection<string> difficulties, bool keepMissing = false)
    {
        if (string.IsNullOrEmpty(selected)) selected = null;

        // Nearly always everything is in place already, and this is asked after every edit.
        bool tidy = true, sharedSeen = false;
        foreach (var obj in Objects)
        {
            if (obj.Difficulty == null) sharedSeen = true;
            else if (obj.Difficulty != selected || sharedSeen) tidy = false;
            if (!tidy) break;
        }
        if (tidy)
            foreach (var obj in Parked)
                if (obj.Difficulty == null || obj.Difficulty == selected || (!keepMissing && !difficulties.Contains(obj.Difficulty)))
                {
                    tidy = false;
                    break;
                }
        if (tidy) return false;

        var own = new List<SbObject>();
        var shared = new List<SbObject>();
        var parked = new List<SbObject>();
        foreach (var obj in AllObjects)
        {
            // Names are file names: their case does not tell two of them apart.
            if (obj.Difficulty != null)
                obj.Difficulty = difficulties.FirstOrDefault(d => string.Equals(d, obj.Difficulty, StringComparison.OrdinalIgnoreCase)) ?? (keepMissing ? obj.Difficulty : null);
            if (obj.Difficulty == null) shared.Add(obj);
            else if (obj.Difficulty == selected) own.Add(obj);
            else parked.Add(obj);
        }
        Objects.Clear();
        Objects.AddRange(own);
        Objects.AddRange(shared);
        Parked.Clear();
        Parked.AddRange(parked);
        return true;
    }

    /// <summary>Deep copy of objects and groups, with every member pointing at the copy of its group.</summary>
    public static (List<SbObject> Objects, List<SbObject> Parked, List<SbObjectGroup> Groups) CloneContent(
        IReadOnlyList<SbObject> objects, IReadOnlyList<SbObject> parked, IReadOnlyList<SbObjectGroup> groups, bool skipGenerated = false)
    {
        var map = new Dictionary<SbObjectGroup, SbObjectGroup>();
        var newGroups = new List<SbObjectGroup>(groups.Count);
        foreach (var group in groups)
        {
            if(skipGenerated && PluginLink.IsGenerated(group)) continue;
            var copy = group.Clone();
            map[group] = copy;
            newGroups.Add(copy);
        }
        foreach (var copy in newGroups)
            if (copy.Parent != null) copy.Parent = map.GetValueOrDefault(copy.Parent);

        List<SbObject> Copy(IReadOnlyList<SbObject> source)
        {
            var result = new List<SbObject>(source.Count);
            foreach (var obj in source)
            {
                if(skipGenerated && PluginLink.IsGenerated(obj)) continue;
                var copy = obj.Clone();
                if (copy is SbSprite { Group: not null } sprite) sprite.Group = map.GetValueOrDefault(sprite.Group);
                result.Add(copy);
            }
            return result;
        }

        return (Copy(objects), Copy(parked), newGroups);
    }

    public (List<SbObject> Objects, List<SbObject> Parked, List<SbObjectGroup> Groups) CloneContent(bool skipGenerated = false) => CloneContent(Objects, Parked, Groups, skipGenerated);

    /// <summary>Earliest and latest time anything happens, or null when empty.</summary>
    public (int Start, int End)? TimeRange()
    {
        int start = int.MaxValue, end = int.MinValue;
        foreach (var obj in Objects)
        {
            if (obj is SbSprite s)
            {
                var tl = s.Timeline;
                if (!tl.HasCommands) continue;
                start = Math.Min(start, tl.StartTime);
                end = Math.Max(end, tl.EndTime);
            }
            else if (obj is SbSample a)
            {
                start = Math.Min(start, a.Time);
                end = Math.Max(end, a.Time);
            }
            else if (obj is SbVideo v)
            {
                // Where it ends is for the file to say; when it begins counts like any other time.
                start = Math.Min(start, v.Time);
                end = Math.Max(end, v.Time);
            }
        }
        return start == int.MaxValue ? null : (start, end);
    }
}
