namespace SbStudio.Core;

/// <summary>
/// A group of sprites handled in time the way a video editor handles a clip that has clips of its own in
/// it: moved, everything in it moves along; made shorter or longer at one of its ends, what is in it is
/// cut or drawn out to match. The storyboard has no window to show a group through, so the members
/// themselves are what gets changed.
/// </summary>
public static class GroupClip
{
    /// <summary>From the first moment a sprite of the list is shown to the last one, or null when none ever is.</summary>
    public static (int Start, int End)? Span(IEnumerable<SbSprite> sprites)
    {
        int start = int.MaxValue, end = int.MinValue;
        foreach (var sprite in sprites)
        {
            var timeline = sprite.Timeline;
            if (!timeline.HasCommands) continue;
            start = Math.Min(start, timeline.StartTime);
            end = Math.Max(end, timeline.EndTime);
        }
        return start == int.MaxValue ? null : (start, end);
    }

    /// <summary>The time a group takes up: that of the sprites in it, however deep, among those shown.</summary>
    public static (int Start, int End)? Span(Storyboard storyboard, SbObjectGroup group) => Span(storyboard.SpritesIn(group));

    /// <summary>
    /// Moves a group in time with everything in it: its own commands, the groups in it and theirs, and
    /// every sprite, those of the difficulties that are not shown too.
    /// </summary>
    public static void Shift(Storyboard storyboard, SbObjectGroup group, int delta)
    {
        if (delta == 0) return;
        foreach (var inner in storyboard.Groups)
            if (inner.IsWithin(group)) { inner.ShiftTime(delta); if(inner.Plugin!=null) inner.Plugin.Offset+=delta; }
        foreach (var sprite in storyboard.SpritesIn(group, true)) sprite.ShiftTime(delta);
    }

    /// <summary>
    /// A group being made shorter or longer at its ends. It remembers every sprite as it was when the
    /// change began, and each step works from that: an end that is pushed in and pulled out again leaves
    /// things as they were.
    ///
    /// Pushed in, an end cuts what reaches past it (<see cref="SpriteTrim"/>): the commands beyond are
    /// dropped and the one that crosses is cut, with the value it has there. A loop that crosses the end is
    /// first written out pass by pass, so that it can be cut like the rest; one that lies beyond goes. A
    /// sprite that lies beyond altogether is left with nothing, and is to be taken out of the storyboard
    /// when the change is done (<see cref="Emptied"/>). Pulled out, an end takes along the sprites that
    /// reached it: they are shown for longer, as a sprite is when its own end is pulled out. The commands
    /// of the group itself stay as they are: they say how the group moves, not when it is there.
    /// </summary>
    public sealed class Trim
    {
        private readonly List<(SbSprite Sprite, SbSprite Original, int Start, int End)> members = new();
        private readonly int maxLoopExpansion;

        /// <summary>The time the group took up when the change began.</summary>
        public int Start { get; }
        public int End { get; }
        /// <summary>False for a group in which nothing is ever shown: there is nothing to cut.</summary>
        public bool HasContent => members.Count > 0;

        public Trim(Storyboard storyboard, SbObjectGroup group, int maxLoopExpansion = 100000)
        {
            this.maxLoopExpansion = maxLoopExpansion;
            if(group.PluginRoot!=null || storyboard.GroupsIn(group).Any(g=>g.Plugin!=null)) return;
            int start = int.MaxValue, end = int.MinValue;
            foreach (var sprite in storyboard.SpritesIn(group, true))
            {
                var timeline = sprite.Timeline;
                if (!timeline.HasCommands) continue;
                members.Add((sprite, (SbSprite)sprite.Clone(), timeline.StartTime, timeline.EndTime));
                // The time the group takes up is that of what is shown; the sprites of other difficulties are cut along.
                if (!storyboard.Objects.Contains(sprite)) continue;
                start = Math.Min(start, timeline.StartTime);
                end = Math.Max(end, timeline.EndTime);
            }
            if (start == int.MaxValue)
            {
                foreach (var member in members)
                {
                    start = Math.Min(start, member.Start);
                    end = Math.Max(end, member.End);
                }
            }
            Start = start == int.MaxValue ? 0 : start;
            End = end == int.MinValue ? 0 : end;
        }

        /// <summary>The sprites the last step left with nothing to show.</summary>
        public IEnumerable<SbSprite> Emptied => members.Where(m => m.Sprite.Nodes.Count == 0).Select(m => m.Sprite);

        /// <summary>Makes the group take up the time from <paramref name="start"/> to <paramref name="end"/>.</summary>
        public void Apply(int start, int end)
        {
            end = Math.Max(end, start);
            foreach (var (sprite, original, a, b) in members)
            {
                bool cutStart = start > Start, cutEnd = end < End;
                // Beyond an end that was pushed in: nothing of it is left.
                bool gone = (cutStart && (b < start || (b == start && a < b))) || (cutEnd && (a > end || (a == end && a < b)));
                if (gone)
                {
                    sprite.DepthCommands.Clear();
                    Set(sprite, new List<SbNode>());
                    continue;
                }
                int from = cutStart ? Math.Max(a, start) : start < Start && a == Start ? start : a;
                int to = cutEnd ? Math.Min(b, end) : end > End && b == End ? end : b;
                sprite.DepthCommands = SpriteTrim.Depth(original,from,to);
                if (from == a && to == b)
                {
                    Set(sprite, original.Nodes.Select(n => n.Clone()).ToList());
                    continue;
                }
                Set(sprite, Cut(original, from, to, from > a, to < b));
            }
        }

        private static void Set(SbSprite sprite, List<SbNode> nodes)
        {
            sprite.Nodes = nodes;
            sprite.Touch();
        }

        /// <summary>The nodes of a sprite shown from <paramref name="from"/> to <paramref name="to"/> in place of when it was.</summary>
        private List<SbNode> Cut(SbSprite original, int from, int to, bool cutStart, bool cutEnd)
        {
            var work = (SbSprite)original.Clone();
            if (cutStart || cutEnd)
            {
                var nodes = new List<SbNode>();
                foreach (var node in work.Nodes)
                {
                    switch (node)
                    {
                        case SbLoop { Commands.Count: > 0 } loop:
                        {
                            int first = loop.AbsoluteStart, last = loop.AbsoluteEnd;
                            // Beyond the end that is pushed in: gone with the rest out there.
                            if ((cutStart && last <= from) || (cutEnd && first >= to)) break;
                            bool crosses = (cutStart && first < from) || (cutEnd && last > to);
                            int length = loop.IterationLength, passes = loop.Iterations;
                            // Too long to write out: it stays whole, and the sprite reaches past the end by it.
                            if (!crosses || (long)passes * loop.Commands.Count > maxLoopExpansion)
                            {
                                nodes.Add(loop);
                                break;
                            }
                            for (int pass = 0; pass < passes; pass++)
                            {
                                foreach (var c in loop.Commands)
                                {
                                    var flat = c.CloneCommand();
                                    flat.Start = loop.Start + pass * length + c.Start;
                                    flat.End = loop.Start + pass * length + c.End;
                                    nodes.Add(flat);
                                }
                            }
                            break;
                        }

                        case SbTrigger trigger:
                            // It waits for its event between two times: those are kept inside what is left.
                            if ((cutStart && trigger.End <= from) || (cutEnd && trigger.Start >= to)) break;
                            if (cutStart) trigger.Start = Math.Max(trigger.Start, from);
                            if (cutEnd) trigger.End = Math.Min(trigger.End, to);
                            nodes.Add(trigger);
                            break;

                        default:
                            nodes.Add(node);
                            break;
                    }
                }
                work.Nodes = nodes;
                work.Touch();
            }

            var timeline = work.Timeline;
            if (!timeline.HasCommands) return new List<SbNode>();
            // With the loops beyond the end gone it may end before the end already: it is not drawn out to it.
            if (cutStart) from = Math.Max(from, timeline.StartTime);
            if (cutEnd) to = Math.Min(to, timeline.EndTime);
            if (to < from) return new List<SbNode>();
            return SpriteTrim.Apply(work, from, to);
        }
    }
}
