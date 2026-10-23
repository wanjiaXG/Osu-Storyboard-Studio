namespace SbStudio.Core;

public struct GroupState
{
    public double X, Y;
    public double Scale;
    public double Rotation;
    public double Z, RX, RY;
}

/// <summary>Compiled commands of a group, evaluated by the same rules as a sprite's own commands.</summary>
public sealed class GroupTimeline
{
    private readonly ChannelSet channels = new();
    /// <summary>Z, RX and RY, which only a space reads (see <see cref="GroupSpace"/>).</summary>
    private Channel z = null!, rx = null!, ry = null!;

    public bool HasCommands { get; private set; }
    public int StartTime { get; private set; }
    public int EndTime { get; private set; }

    private GroupTimeline() { }

    public static GroupTimeline Build(SbObjectGroup group)
    {
        var tl = new GroupTimeline();
        int start = int.MaxValue, end = int.MinValue;
        foreach (var c in group.Commands)
        {
            if (!SbObjectGroup.Accepts(c.Kind)) continue;
            tl.channels.Add(c);
            start = Math.Min(start, c.Start);
            end = Math.Max(end, Math.Max(c.Start, c.End));
        }
        tl.channels.Seal();
        tl.z = GroupSpace.Compile(group.Commands, CommandKind.Z);
        tl.rx = GroupSpace.Compile(group.Commands, CommandKind.RX);
        tl.ry = GroupSpace.Compile(group.Commands, CommandKind.RY);
        tl.HasCommands = start != int.MaxValue;
        tl.StartTime = tl.HasCommands ? start : 0;
        tl.EndTime = tl.HasCommands ? end : 0;
        return tl;
    }

    public GroupState Evaluate(double time, SbObjectGroup group)
    {
        GroupState state;
        state.X = channels[ChannelSet.X].At(time, group.X);
        state.Y = channels[ChannelSet.Y].At(time, group.Y);
        state.Scale = channels[ChannelSet.Scale].At(time, 1);
        state.Rotation = channels[ChannelSet.Rotation].At(time, 0);
        state.Z = z.At(time, 0);
        state.RX = rx.At(time, 0);
        state.RY = ry.At(time, 0);
        return state;
    }

    /// <summary>Carries a member's own state into what is shown on screen.</summary>
    public static void Apply(ref SpriteState member, in GroupState state, SbObjectGroup group)
    {
        double dx = member.X - group.PivotX, dy = member.Y - group.PivotY;
        double cos = Math.Cos(state.Rotation), sin = Math.Sin(state.Rotation);
        member.X = state.X + (dx * cos - dy * sin) * state.Scale;
        member.Y = state.Y + (dx * sin + dy * cos) * state.Scale;
        member.Scale *= state.Scale;
        member.Rotation += state.Rotation;
    }

    /// <summary>The same for a group that is a member of another group: it is carried along like a sprite at its position.</summary>
    public static void Apply(ref GroupState member, in GroupState state, SbObjectGroup group)
    {
        double dx = member.X - group.PivotX, dy = member.Y - group.PivotY;
        double cos = Math.Cos(state.Rotation), sin = Math.Sin(state.Rotation);
        member.X = state.X + (dx * cos - dy * sin) * state.Scale;
        member.Y = state.Y + (dx * sin + dy * cos) * state.Scale;
        member.Scale *= state.Scale;
        member.Rotation += state.Rotation;
    }

    /// <summary>
    /// How far something has to move in the space of the members of a group to move by (dx, dy) on screen,
    /// through that group and every group it is in. With null it is in the storyboard itself, and the
    /// offset is what it is.
    /// </summary>
    public static (double X, double Y) ToMemberOffset(SbObjectGroup? container, double time, double dx, double dy)
    {
        int steps = 0;
        for (var g = container; g != null && steps < SbObjectGroup.MaxDepth; g = g.Parent, steps++)
            (dx, dy) = ToMemberOffset(g.Timeline.Evaluate(time, g), dx, dy);
        return (dx, dy);
    }

    /// <summary>The opposite direction for an offset: how far a member has to move in its own space to move by (dx, dy) on screen.</summary>
    public static (double X, double Y) ToMemberOffset(in GroupState state, double dx, double dy)
    {
        double scale = Math.Abs(state.Scale) < 1e-6 ? 1 : state.Scale;
        double cos = Math.Cos(-state.Rotation), sin = Math.Sin(-state.Rotation);
        return ((dx * cos - dy * sin) / scale, (dx * sin + dy * cos) / scale);
    }
}
