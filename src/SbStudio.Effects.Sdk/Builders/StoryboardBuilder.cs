namespace SbStudio.Effects;

public sealed class StoryboardBuilder
{
    private bool sealedOutput;
    private readonly List<GroupBuilder> groups = [];
    private readonly List<Func<OutputSprite>> sprites = [];
    internal void Check() { if (sealedOutput) throw new InvalidOperationException("The storyboard has been sealed."); }
    public SpriteBuilder Sprite(string path, Layer layer = Layer.Background, Origin origin = Origin.Centre) => Sprite(AssetReference.Project(path), layer, origin);
    public SpriteBuilder Sprite(AssetReference asset, Layer layer = Layer.Background, Origin origin = Origin.Centre) => CreateSprite(asset, null, layer, origin);
    public AnimationBuilder Animation(AssetReference asset, int frames, double delay, AnimationLoop loop = AnimationLoop.LoopForever,
        Layer layer = Layer.Background, Origin origin = Origin.Centre) => CreateAnimation(asset, null, frames, delay, loop, layer, origin);
    public AnimationBuilder Animation(string path, int frames, double delay, AnimationLoop loop = AnimationLoop.LoopForever,
        Layer layer = Layer.Background, Origin origin = Origin.Centre) => Animation(AssetReference.Project(path), frames, delay, loop, layer, origin);
    public GroupBuilder Group(string name = "") => CreateGroup(name, null);
    internal SpriteBuilder CreateSprite(AssetReference asset, int? group, Layer layer, Origin origin)
    {
        Check(); var result = new SpriteBuilder(this, asset, group, layer, origin); sprites.Add(result.Snapshot); return result;
    }
    internal AnimationBuilder CreateAnimation(AssetReference asset, int? group, int frames, double delay, AnimationLoop loop, Layer layer, Origin origin)
    {
        Check(); var result = new AnimationBuilder(this, asset, group, layer, origin, frames, delay, loop); sprites.Add(result.Snapshot); return result;
    }
    internal GroupBuilder CreateGroup(string name, int? parent)
    {
        Check(); var result = new GroupBuilder(this, groups.Count, parent, name); groups.Add(result); return result;
    }
    public StoryboardOutput Build()
    {
        Check(); sealedOutput = true;
        return new(1, Array.AsReadOnly(groups.Select(g => g.Snapshot()).ToArray()), Array.AsReadOnly(sprites.Select(s => s()).ToArray()));
    }
}

internal sealed class SpriteState
{
    internal readonly StoryboardBuilder Owner;
    internal readonly List<OutputNode> Nodes = [];
    internal readonly List<OutputCommand> Depth = [];
    internal readonly AssetReference Asset;
    internal readonly int? Group;
    internal readonly Layer Layer;
    internal readonly Origin Origin;
    internal Point2 Position = new(320, 240);
    internal string? Name;
    internal bool FaceCamera;
    internal int Frames;
    internal double Delay;
    internal AnimationLoop Loop;
    internal SpriteState(StoryboardBuilder owner, AssetReference asset, int? group, Layer layer, Origin origin)
    {
        asset.Validate();
        if (!Enum.IsDefined(layer) || !Enum.IsDefined(origin)) throw new ArgumentException("Invalid layer or origin.");
        Owner = owner; Asset = asset; Group = group; Layer = layer; Origin = origin;
    }
    internal void Add(OutputCommand c) { Owner.Check(); if (c.Kind is "Z" or "RX" or "RY") Depth.Add(c); else Nodes.Add(new(c)); }
    internal OutputSprite Snapshot() => new(Asset, Group, Layer, Origin, Position, Name, Frames, Delay, Loop, FaceCamera,
        Array.AsReadOnly(Nodes.ToArray()), Array.AsReadOnly(Depth.ToArray()));
}

public abstract class SpriteBuilderBase<TSelf> : CommandBuilder<TSelf> where TSelf : SpriteBuilderBase<TSelf>
{
    private readonly SpriteState state;
    internal SpriteBuilderBase(SpriteState state) : base(state.Add) => this.state = state;
    internal OutputSprite Snapshot() => state.Snapshot();
    public TSelf Name(string name) { state.Owner.Check(); state.Name = name; return (TSelf)this; }
    public TSelf Position(double x, double y) { state.Owner.Check(); Guard.Point(new(x, y)); state.Position = new(x, y); return (TSelf)this; }
    public TSelf FaceCamera(bool enabled = true) { state.Owner.Check(); state.FaceCamera = enabled; return (TSelf)this; }
    public TSelf Loop(double start, int count, Action<BlockBuilder> commands)
    {
        Guard.Time(start, start); if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        return Block("Loop", start, start, count, null, 0, commands);
    }
    public TSelf Trigger(string name, double start, double end, Action<BlockBuilder> commands, int group = 0)
    {
        Guard.Time(start, end);
        if (string.IsNullOrWhiteSpace(name) || name.Any(c => char.IsControl(c) || c == ',')) throw new ArgumentException("Invalid trigger name.");
        if (group < 0) throw new ArgumentOutOfRangeException(nameof(group));
        return Block("Trigger", start, end, 1, name, group, commands);
    }
    private TSelf Block(string kind, double start, double end, int count, string? trigger, int group, Action<BlockBuilder> commands)
    {
        state.Owner.Check(); var items = new List<OutputCommand>(); bool open = true;
        var builder = new BlockBuilder(c => { state.Owner.Check(); if (!open) throw new InvalidOperationException("The command block has closed."); items.Add(c); });
        try { commands(builder); } finally { open = false; }
        state.Owner.Check(); state.Nodes.Add(new(Block: new(kind, start, end, count, trigger, group, Array.AsReadOnly(items.ToArray()))));
        return (TSelf)this;
    }
}
public sealed class SpriteBuilder : SpriteBuilderBase<SpriteBuilder>
{
    internal SpriteBuilder(StoryboardBuilder owner, AssetReference asset, int? group, Layer layer, Origin origin)
        : base(new(owner, asset, group, layer, origin)) { }
}
public sealed class AnimationBuilder : SpriteBuilderBase<AnimationBuilder>
{
    internal AnimationBuilder(StoryboardBuilder owner, AssetReference asset, int? group, Layer layer, Origin origin, int frames, double delay, AnimationLoop loop)
        : base(Create(owner, asset, group, layer, origin, frames, delay, loop)) { }
    private static SpriteState Create(StoryboardBuilder owner, AssetReference asset, int? group, Layer layer, Origin origin, int frames, double delay, AnimationLoop loop)
    {
        Guard.Finite(delay);
        if (frames < 1 || frames > 100000 || delay <= 0 || !Enum.IsDefined(loop)) throw new ArgumentOutOfRangeException(nameof(frames));
        return new(owner, asset, group, layer, origin) { Frames = frames, Delay = delay, Loop = loop };
    }
}

public sealed class GroupBuilder : CommandBuilder<GroupBuilder>
{
    private readonly StoryboardBuilder owner;
    private readonly int index;
    private readonly int? parent;
    private readonly List<OutputCommand> commands;
    private string name;
    private Point2 pivot = new(320, 240), position = new(320, 240);
    internal GroupBuilder(StoryboardBuilder owner, int index, int? parent, string name) : this(owner, index, parent, name, []) { }
    private GroupBuilder(StoryboardBuilder owner, int index, int? parent, string name, List<OutputCommand> commands) : base(c =>
    {
        owner.Check();
        if (c.Kind is "F" or "C" or "P") throw new InvalidOperationException("Groups do not support opacity, color or parameter commands.");
        commands.Add(c);
    }) { this.owner = owner; this.index = index; this.parent = parent; this.name = name; this.commands = commands; }
    public GroupBuilder Name(string value) { owner.Check(); name = value; return this; }
    public GroupBuilder Pivot(double x, double y) { owner.Check(); Guard.Point(new(x, y)); pivot = new(x, y); return this; }
    public GroupBuilder Position(double x, double y) { owner.Check(); Guard.Point(new(x, y)); position = new(x, y); return this; }
    public GroupBuilder Curve(params CurvePoint[] points)
    {
        owner.Check();
        if (commands.Count == 0 || points.Length < 2 || points[0].X != 0 || points[^1].X != 1) throw new ArgumentException("A curve needs endpoints at 0 and 1 and a preceding command.");
        double last = -1;
        foreach (var p in points) { Guard.Finite(p.X); Guard.Finite(p.Y); if (p.X <= last || p.X > 1) throw new ArgumentException("Curve times must increase."); last = p.X; }
        commands[^1] = commands[^1] with { Curve = Array.AsReadOnly(points.ToArray()) }; return this;
    }
    public SpriteBuilder Sprite(AssetReference asset, Layer layer = Layer.Background, Origin origin = Origin.Centre) => owner.CreateSprite(asset, index, layer, origin);
    public SpriteBuilder Sprite(string path, Layer layer = Layer.Background, Origin origin = Origin.Centre) => Sprite(AssetReference.Project(path), layer, origin);
    public AnimationBuilder Animation(AssetReference asset, int frames, double delay, AnimationLoop loop = AnimationLoop.LoopForever,
        Layer layer = Layer.Background, Origin origin = Origin.Centre) => owner.CreateAnimation(asset, index, frames, delay, loop, layer, origin);
    public GroupBuilder Group(string name = "") => owner.CreateGroup(name, index);
    internal OutputGroup Snapshot() => new(parent, name, pivot, position, Array.AsReadOnly(commands.ToArray()));
}
