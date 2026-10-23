using SbStudio.Effects;

namespace SbStudio.Core;

/// <summary>Validates the entire result before creating document objects. No plug-in code is executed here.</summary>
public static class EffectOutputAdapter
{
    public static int Milliseconds(double value)
    {
        if (!double.IsFinite(value) || value < int.MinValue + 1d || value > int.MaxValue - 1d) throw new FormatException("Effect time is out of range.");
        return checked((int)Math.Round(value, MidpointRounding.AwayFromZero));
    }
    public static Storyboard Convert(StoryboardOutput output, Func<AssetReference, int, string> resolveAsset)
    {
        if (output.FormatVersion != 1) throw new NotSupportedException("Unsupported effect output version.");
        if (output.Groups.Count > 100000 || output.Sprites.Count > 1000000) throw new FormatException("Effect output exceeds object limits.");
        var doc = new Storyboard(); int count = 0;
        for (int i = 0; i < output.Groups.Count; i++)
        {
            var g = output.Groups[i];
            if (g.Parent is {} p && (p < 0 || p >= i)) throw new FormatException("Invalid group parent or cycle.");
            Point(g.Pivot); Point(g.Position);
            var group = new SbObjectGroup { Name = g.Name, Parent = g.Parent is {} parent ? doc.Groups[parent] : null,
                PivotX = g.Pivot.X, PivotY = g.Pivot.Y, X = g.Position.X, Y = g.Position.Y };
            int depth = 0; for (var ancestor = group.Parent; ancestor != null; ancestor = ancestor.Parent) depth++;
            if (depth >= SbObjectGroup.MaxDepth - 1) throw new FormatException("Effect group nesting is too deep.");
            foreach (var c in g.Commands) group.Commands.Add(Command(c, true, false));
            doc.Groups.Add(group);
        }
        foreach (var s in output.Sprites)
        {
            s.Asset.Validate(); Point(s.Position);
            if (!Enum.IsDefined(s.Layer) || !Enum.IsDefined(s.Origin) || !Enum.IsDefined(s.Loop) || s.Group is {} g && (g < 0 || g >= doc.Groups.Count))
                throw new FormatException("Invalid effect sprite.");
            if (s.Frames < 0 || s.Frames > 100000 || s.Frames > 0 && (!double.IsFinite(s.FrameDelay) || s.FrameDelay <= 0)) throw new FormatException("Invalid animation.");
            var sprite = new SbSprite { Path = resolveAsset(s.Asset, s.Frames), Layer = (SbLayer)s.Layer, Origin = (SbOrigin)s.Origin,
                X = s.Position.X, Y = s.Position.Y, Name = s.Name, Group = s.Group is {} parent ? doc.Groups[parent] : null,
                IsAnimation = s.Frames > 0, FrameCount = Math.Max(1, s.Frames), FrameDelay = s.FrameDelay, LoopType = (SbLoopType)s.Loop, FaceCamera = s.FaceCamera };
            foreach (var node in s.Nodes)
            {
                if ((node.Command == null) == (node.Block == null)) throw new FormatException("A node must contain exactly one command or block.");
                if (node.Command is {} command) sprite.Nodes.Add(Command(command, false, false));
                else if (node.Block is {} b)
                {
                    int start = Milliseconds(b.Start), end = Milliseconds(b.End);
                    if (end < start || b.Count < 1) throw new FormatException("Invalid block range/count.");
                    SbGroup block = b.Kind switch
                    {
                        "Loop" => new SbLoop { Start = start, Count = b.Count },
                        "Trigger" when !string.IsNullOrWhiteSpace(b.Trigger) && !b.Trigger.Any(c => char.IsControl(c) || c == ',') && b.TriggerGroup >= 0
                            => new SbTrigger { Start = start, End = end, Name = b.Trigger, Group = b.TriggerGroup },
                        _ => throw new FormatException("Invalid effect command block."),
                    };
                    foreach (var c in b.Commands) block.Commands.Add(Command(c, false, false));
                    if (block is SbLoop loop)
                    {
                        long begin = (long)loop.Start + loop.CommandsStart;
                        long finish = begin + ((long)loop.CommandsEnd - loop.CommandsStart) * loop.Count;
                        if (begin < int.MinValue + 1L || finish > int.MaxValue - 1L) throw new FormatException("Loop exceeds time range.");
                    }
                    sprite.Nodes.Add(block);
                }
            }
            foreach (var c in s.Depth) sprite.DepthCommands.Add(Command(c, false, true));
            doc.Objects.Add(sprite);
        }
        return doc;

        SbCommand Command(OutputCommand c, bool group, bool depth)
        {
            if (++count > 5000000) throw new FormatException("Effect output exceeds command limits.");
            if (!Enum.TryParse<CommandKind>(c.Kind, out var kind) || !Enum.IsDefined(kind) || kind >= CommandKind.CT ||
                !Enum.IsDefined(c.Ease) || c.End < c.Start || group && kind is CommandKind.F or CommandKind.C or CommandKind.P ||
                !group && depth != (kind is CommandKind.Z or CommandKind.RX or CommandKind.RY)) throw new FormatException("Invalid effect command.");
            int size = SbInfo.ValueCount(kind);
            if (c.From.Count != size || c.To.Count != size || c.From.Concat(c.To).Any(v => !double.IsFinite(v))) throw new FormatException("Invalid command values.");
            if (kind == CommandKind.C && c.From.Concat(c.To).Any(v => v < 0 || v > 255)) throw new FormatException("Invalid color.");
            if (kind == CommandKind.P && c.Parameter is not ('A' or 'H' or 'V')) throw new FormatException("Invalid parameter command.");
            var result = new SbCommand(kind, (int)c.Ease, Milliseconds(c.Start), Milliseconds(c.End), c.From.ToArray(), c.To.ToArray()) { Param = c.Parameter };
            if (c.Curve is {} curve)
            {
                if (!group || curve.Count < 2 || curve[0] != new CurvePoint(0, 0) || curve[^1] != new CurvePoint(1, 1)) throw new FormatException("Invalid group curve endpoints.");
                var target = new EasingCurve(); double previous = 0;
                foreach (var p in curve.Skip(1).SkipLast(1))
                {
                    if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X - previous < EasingCurve.MinGap || p.X > 1 - EasingCurve.MinGap || p.Y < -1 || p.Y > 2)
                        throw new FormatException("Invalid group curve point.");
                    target.Points.Add((p.X, p.Y)); previous = p.X;
                }
                result.Curve = target;
            }
            return result;
        }
        static void Point(Point2 p) { if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) throw new FormatException("Invalid effect point."); }
    }
}
