using System.Collections;

namespace SbStudio.Core;

/// <summary>
/// Commands of different kinds that cannot run at the same time. A move (M) sets both coordinates, of which
/// MX and MY set one each; S and V are both the scale. osu! lets only one of them have its way at any
/// moment (its sprite update keeps a single flag for "the scale has been set", and leaves a move alone once
/// MX or MY has spoken), so a storyboard in which they run together does not show what it says.
///
/// Running together includes a point command at another command's start. One that begins at the moment
/// the other ends is fine: that is how they follow one another.
/// </summary>
public static class CommandClash
{
    /// <summary>One place where such commands run together.</summary>
    /// <param name="Kind">M for a move against MX or MY, S for the scale against the vector scale.</param>
    /// <param name="Time">The first moment at which they do, in storyboard time.</param>
    public readonly record struct Clash(SbSprite? Sprite, SbObjectGroup? Group, CommandKind Kind, int Time);

    private static readonly CommandKind[] None = Array.Empty<CommandKind>();
    private static readonly CommandKind[] Move = { CommandKind.M }, Axes = { CommandKind.MX, CommandKind.MY };
    private static readonly CommandKind[] Scale = { CommandKind.S }, Vector = { CommandKind.V };

    /// <summary>The kinds whose commands may not run while those of a kind do.</summary>
    public static CommandKind[] Rivals(CommandKind kind) => kind switch
    {
        CommandKind.M => Axes,
        CommandKind.MX or CommandKind.MY => Move,
        CommandKind.S => Vector,
        CommandKind.V => Scale,
        _ => None,
    };

    /// <summary>Another kind sets the same property at this exact command start, including instant commands.</summary>
    public static bool HasRivalStart(IList nodes, CommandKind kind, int time)
    {
        var rivals = Rivals(kind);
        if (rivals.Length == 0) return false;
        foreach (object? node in nodes)
            if (node is SbCommand c && c.Start == time && Array.IndexOf(rivals, c.Kind) >= 0) return true;
        return false;
    }

    /// <summary>
    /// The stretches of time in which the commands of a list of one kind run together with those of a kind
    /// they may not run with. A single moment has the same start and end.
    /// </summary>
    public static List<(int Start, int End)> Overlaps(IList nodes, CommandKind kind)
    {
        var rivals = Rivals(kind);
        if (rivals.Length == 0) return new List<(int, int)>();
        return Overlaps(Coverage(nodes, kind), Coverage(nodes, rivals));
    }

    /// <summary>Every such place of a sprite: among its own commands, inside its loops, and between a loop and what is around it.</summary>
    public static List<Clash> Find(SbSprite sprite)
    {
        var found = new List<Clash>();
        foreach (var pair in new[] { CommandKind.M, CommandKind.S })
        {
            var stretches = Stretches(sprite, pair);
            if (stretches.Count > 0) found.Add(new Clash(sprite, null, pair, stretches[0].Start));
        }
        return found;
    }

    /// <summary>
    /// Every stretch of time in which commands of a sprite run together that may not, in order: among its
    /// own commands, inside its loops, and between a loop and what is around it.
    /// </summary>
    /// <param name="pair">M for a move against MX or MY, S for the scale against the vector scale.</param>
    public static List<(int Start, int End)> Stretches(SbSprite sprite, CommandKind pair)
    {
        var (first, second) = pair == CommandKind.M ? (Move, Axes) : (Scale, Vector);
        // The commands of the sprite itself as they are; each loop as the one stretch it repeats in.
        // What a loop does within one pass is looked at on its own, in the loop's own time.
        var sources = new List<(List<(int, int)> First, List<(int, int)> Second)> { (Coverage(sprite.Nodes, first), Coverage(sprite.Nodes, second)) };
        var found = new List<(int Start, int End)>();
        foreach (var node in sprite.Nodes)
        {
            if (node is not SbLoop loop || loop.Commands.Count == 0) continue;
            foreach (var (start, end) in Overlaps(Coverage(loop.Commands, first), Coverage(loop.Commands, second)))
                found.Add((loop.Start + start, loop.Start + end));
            sources.Add((Whole(loop, first), Whole(loop, second)));
        }
        for (int i = 0; i < sources.Count; i++)
        {
            for (int j = 0; j < sources.Count; j++)
            {
                // A loop against itself has been looked at pass by pass.
                if (i == j && i > 0) continue;
                found.AddRange(Overlaps(sources[i].First, sources[j].Second));
            }
        }
        return Join(found);
    }

    /// <summary>
    /// Every such place in a storyboard: in its groups of sprites and in its sprites, those of the
    /// difficulties that are not shown included, as saving and exporting write them all.
    /// </summary>
    public static List<Clash> Find(Storyboard storyboard)
    {
        var found = new List<Clash>();
        foreach (var group in storyboard.Groups)
        {
            // A group has no vector scale: only its move can be at odds with MX and MY.
            var overlaps = Overlaps(group.Commands, CommandKind.M);
            if (overlaps.Count > 0) found.Add(new Clash(null, group, CommandKind.M, overlaps[0].Start));
        }
        foreach (var obj in storyboard.AllObjects)
            if (obj is SbSprite sprite) found.AddRange(Find(sprite));
        return found;
    }

    /// <summary>The stretches of time in which commands of some kinds run, in order, those that touch joined into one.</summary>
    internal static List<(int Start, int End)> Coverage(IList nodes, params CommandKind[] kinds)
    {
        var spans = new List<(int Start, int End)>();
        foreach (object? node in nodes)
            if (node is SbCommand c && Array.IndexOf(kinds, c.Kind) >= 0) spans.Add((c.Start, Math.Max(c.Start, c.End)));
        // A point at the end of a span is a separate command start. Folding it into the
        // span would hide a clash with a rival that starts at that very moment.
        var joined = Join(spans.Where(s => s.Start < s.End).ToList());
        var result = new List<(int Start, int End)>(joined);
        int interval = 0;
        foreach (int point in spans.Where(s => s.Start == s.End).Select(s => s.Start).Distinct().Order())
        {
            while (interval < joined.Count && joined[interval].End <= point) interval++;
            if (interval == joined.Count || point < joined[interval].Start) result.Add((point, point));
        }
        result.Sort();
        return result;
    }

    /// <summary>Stretches put in order, those that touch or share time joined into one.</summary>
    internal static List<(int Start, int End)> Join(List<(int Start, int End)> spans)
    {
        if (spans.Count < 2) return spans;

        spans.Sort();
        var joined = new List<(int Start, int End)> { spans[0] };
        for (int i = 1; i < spans.Count; i++)
        {
            var last = joined[^1];
            if (spans[i].Start <= last.End) joined[^1] = (last.Start, Math.Max(last.End, spans[i].End));
            else joined.Add(spans[i]);
        }
        return joined;
    }

    /// <summary>The one stretch from the first pass of a loop to its last in which commands of some kinds run; nothing when it has none of them.</summary>
    internal static List<(int Start, int End)> Whole(SbLoop loop, params CommandKind[] kinds)
    {
        var one = Coverage(loop.Commands, kinds);
        var whole = new List<(int, int)>();
        if (one.Count > 0)
        {
            int start = loop.Start + one[0].Start, end = loop.Start + (loop.Iterations - 1) * loop.IterationLength + one[^1].End;
            whole.Add((start, end));
            if (start < end && one[^1].Start == one[^1].End) whole.Add((end, end));
        }
        return whole;
    }

    /// <summary>What two lists of stretches, each in order and without overlaps of its own, have in common.</summary>
    internal static List<(int Start, int End)> Overlaps(List<(int Start, int End)> a, List<(int Start, int End)> b, bool includeStarts = true)
    {
        var shared = new List<(int, int)>();
        int i = 0, j = 0;
        while (i < a.Count && j < b.Count)
        {
            int from = Math.Max(a[i].Start, b[j].Start), to = Math.Min(a[i].End, b[j].End);
            if (from < to)
            {
                shared.Add((from, to));
            }
            else if (from == to)
            {
                // They share a single moment. Two stretches that meet end to start follow one another; a
                // point command at the start or inside a rival's span, or two points at one moment, clash.
                bool pointA = a[i].Start == a[i].End, pointB = b[j].Start == b[j].End;
                bool startsA = includeStarts ? a[i].Start <= from : a[i].Start < from;
                bool startsB = includeStarts ? b[j].Start <= from : b[j].Start < from;
                if ((pointA && pointB) || (pointA && startsB && from < b[j].End) || (pointB && startsA && from < a[i].End))
                    shared.Add((from, to));
            }
            // The one that ends first has nothing more to meet; a point goes before a stretch that ends with it.
            if (a[i].End < b[j].End || (a[i].End == b[j].End && a[i].Start >= b[j].Start)) i++;
            else j++;
        }
        return shared;
    }
}
