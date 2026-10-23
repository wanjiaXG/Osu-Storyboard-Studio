using System.Collections;

namespace SbStudio.Core;

public enum ProblemKind
{
    PluginFailed, PluginLinkMissing, PluginWarnings, PluginUnsupported,
    /// <summary>
    /// Commands of kinds that cannot run at the same time do: a move with MX or MY, the scale with the
    /// vector scale. See <see cref="CommandClash"/>. The game does not show what the script says.
    /// </summary>
    Clash,
    /// <summary>
    /// Commands of one kind run at the same time. osu! lets the one that began first have its way until it
    /// is over, and so does the preview; it is seldom what was meant.
    /// </summary>
    Overlap,
    /// <summary>A command ends before it starts.</summary>
    Reversed,
    /// <summary>An object names a file that is not among the project's assets.</summary>
    MissingFile,
    SpaceComposition, SpaceLoopTooLong,
}

/// <summary>One thing that is wrong with a storyboard, or likely to be, and where on the timeline it is.</summary>
/// <param name="Object">The object it is about; null when it is about a group of sprites.</param>
/// <param name="Command">
/// For a clash, M for a move against MX or MY and S for the scale against the vector scale. For an overlap
/// and a reversed command, their kind.
/// </param>
/// <param name="Start">Where it begins, in storyboard time. For a missing file, where the object begins.</param>
/// <param name="End">Where the stretch ends that it lasts for. For a reversed command, the end the command names.</param>
/// <param name="File">The file that is missing: the first of them when there are several.</param>
/// <param name="Files">How many files are missing.</param>
/// <param name="Loop">
/// The loop of the sprite it is in: its commands among themselves within one pass, with the times those of
/// the first pass, or the loop as a whole against another loop. Null when it is about the sprite's own
/// commands, among themselves or against a loop.
/// </param>
public readonly record struct Problem(ProblemKind Kind, SbObject? Object, SbObjectGroup? Group, CommandKind Command, int Start, int End, string File = "", int Files = 0,
    SbLoop? Loop = null)
{
    /// <summary>Conflicting command types and missing required assets are errors; other issues are warnings.</summary>
    public bool IsError => Kind is ProblemKind.Clash or ProblemKind.MissingFile or ProblemKind.PluginFailed;
}

/// <summary>Looks through a storyboard for what is listed in <see cref="ProblemKind"/>.</summary>
public static class Problems
{
    private const int KindCount = (int)CommandKind.CF + 1;
    private const int MoveBit = 1 << (int)CommandKind.M, AxesBits = 1 << (int)CommandKind.MX | 1 << (int)CommandKind.MY;
    private const int ScaleBit = 1 << (int)CommandKind.S, VectorBit = 1 << (int)CommandKind.V;

    /// <summary>
    /// What is wrong with the commands of a sprite: among its own, inside each of its loops, and between a
    /// loop and what is around it, where the loop counts as the one stretch it repeats in. Triggers start
    /// when the game says so and are left out.
    /// </summary>
    public static void Find(SbSprite sprite, List<Problem> found)
    {
        if (!IsTidy(sprite)) Look(sprite, found);
        SpaceProblems(sprite,found);
    }

    /// <summary>The same as an array, which is what <see cref="SbSprite.CommandProblems"/> keeps: the one empty array for nearly every sprite.</summary>
    internal static Problem[] Of(SbSprite sprite)
    {
        if (IsTidy(sprite) && !GroupSpace.UsesDepth(sprite.Group) && !sprite.CompositionWarning) return Array.Empty<Problem>();
        var found = new List<Problem>();
        if (!IsTidy(sprite)) Look(sprite, found);
        SpaceProblems(sprite,found);
        return found.ToArray();
    }

    /// <summary>
    /// Whether one pass over the commands of a sprite shows that nothing can be wrong with them, which is
    /// so for nearly every sprite: its commands of each kind follow one another, no loop has a kind that
    /// is around it too, and no two kinds are there that are at odds.
    /// </summary>
    private static void SpaceProblems(SbSprite sprite,List<Problem> found)
    {
        if(GroupSpace.UsesDepth(sprite.Group))
        {
            var baked=sprite.Exported();
            if(baked.BakeLoopTooLong) found.Add(new Problem(ProblemKind.SpaceLoopTooLong,sprite,null,CommandKind.M,sprite.Timeline.StartTime,sprite.Timeline.StartTime));
        }
        if(sprite.CompositionWarning) found.Add(new Problem(ProblemKind.SpaceComposition,sprite,null,CommandKind.R,0,0));
    }
    private static bool IsTidy(SbSprite sprite)
    {
        if (!Tidy(sprite.Nodes, out int kinds)) return false;
        foreach (var node in sprite.Nodes)
        {
            if (node is not SbLoop loop || loop.Commands.Count == 0) continue;
            if (!Tidy(loop.Commands, out int inLoop) || (inLoop & kinds) != 0) return false;
            kinds |= inLoop;
        }
        return ((kinds & MoveBit) == 0 || (kinds & AxesBits) == 0) && ((kinds & ScaleBit) == 0 || (kinds & VectorBit) == 0);
    }

    private static readonly CommandKind[] Move = { CommandKind.M }, Axes = { CommandKind.MX, CommandKind.MY };
    private static readonly CommandKind[] Scale = { CommandKind.S }, Vector = { CommandKind.V };

    private static void Look(SbSprite sprite, List<Problem> found)
    {
        var loops = new List<SbLoop>();
        foreach (var node in sprite.Nodes)
            if (node is SbLoop loop && loop.Commands.Count > 0) loops.Add(loop);

        foreach (var kind in SbInfo.AllKinds)
        {
            if (kind == CommandKind.P) continue;
            Reversed(sprite.Nodes, kind, 0, sprite, null, null, found);
            foreach (var loop in loops) Reversed(loop.Commands, kind, loop.Start, sprite, null, loop, found);
            Together(sprite, loops, ProblemKind.Overlap, kind, new[] { kind }, new[] { kind }, found);
        }
        Together(sprite, loops, ProblemKind.Clash, CommandKind.M, Move, Axes, found);
        Together(sprite, loops, ProblemKind.Clash, CommandKind.S, Scale, Vector, found);
    }

    /// <summary>
    /// The stretches in which commands of some kinds run together with commands of others, or, for an
    /// overlap, of the same kind, each kept with where it is so that it can be gone to: among the sprite's
    /// own commands and between those and a loop (no loop named), inside a pass of a loop, and between
    /// one loop and another (the later of the two named). A loop counts against what is around it as the
    /// one stretch it repeats in.
    /// </summary>
    private static void Together(SbSprite sprite, List<SbLoop> loops, ProblemKind problem, CommandKind command, CommandKind[] first, CommandKind[] second,
        List<Problem> found)
    {
        bool same = problem == ProblemKind.Overlap;
        var own = same ? SameKind(sprite.Nodes, command) : CommandClash.Overlaps(CommandClash.Coverage(sprite.Nodes, first), CommandClash.Coverage(sprite.Nodes, second));
        var ownFirst = CommandClash.Coverage(sprite.Nodes, first);
        var ownSecond = same ? ownFirst : CommandClash.Coverage(sprite.Nodes, second);

        var inLoops = new List<(int Start, int End)>[loops.Count];
        for (int i = 0; i < loops.Count; i++)
        {
            var loop = loops[i];
            var inside = same ? SameKind(loop.Commands, command) : CommandClash.Overlaps(CommandClash.Coverage(loop.Commands, first), CommandClash.Coverage(loop.Commands, second));
            inLoops[i] = new List<(int Start, int End)>();
            foreach (var (start, end) in inside) inLoops[i].Add((loop.Start + start, loop.Start + end));

            var wholeFirst = CommandClash.Whole(loop, first);
            var wholeSecond = same ? wholeFirst : CommandClash.Whole(loop, second);
            // Against the sprite's own commands, either way round.
            own.AddRange(CommandClash.Overlaps(ownFirst, wholeSecond, includeStarts: !same));
            if (!same) own.AddRange(CommandClash.Overlaps(wholeFirst, ownSecond));
            // Against the loops before it.
            for (int j = 0; j < i; j++)
            {
                inLoops[i].AddRange(CommandClash.Overlaps(CommandClash.Whole(loops[j], first), wholeSecond, includeStarts: !same));
                if (!same) inLoops[i].AddRange(CommandClash.Overlaps(wholeFirst, CommandClash.Whole(loops[j], second)));
            }
        }

        foreach (var (start, end) in CommandClash.Join(own))
            found.Add(new Problem(problem, sprite, null, command, start, end));
        for (int i = 0; i < loops.Count; i++)
            foreach (var (start, end) in CommandClash.Join(inLoops[i]))
                found.Add(new Problem(problem, sprite, null, command, start, end, Loop: loops[i]));
    }

    /// <summary>
    /// The commands a problem is about, each with the list it is in: those that run within its stretch,
    /// the one to look at first at the head. That is the one that begins last, which is the one that came
    /// in while another was running. None for a missing file, and none for a loop that as a whole runs
    /// together with another loop: there the loop is what the problem is about.
    /// </summary>
    public static List<(IList Nodes, SbCommand Command)> Commands(in Problem problem)
    {
        var found = new List<(IList Nodes, SbCommand Command)>();
        if (problem.Kind == ProblemKind.MissingFile) return found;
        IList? nodes = problem.Loop != null ? problem.Loop.Commands : problem.Group != null ? problem.Group.Commands : (problem.Object as SbSprite)?.Nodes;
        if (nodes == null) return found;
        int offset = problem.Loop?.Start ?? 0;
        var kinds = problem.Kind != ProblemKind.Clash ? new[] { problem.Command }
            : problem.Command == CommandKind.M ? new[] { CommandKind.M, CommandKind.MX, CommandKind.MY }
            : new[] { CommandKind.S, CommandKind.V };

        foreach (object? node in nodes)
        {
            if (node is not SbCommand c || Array.IndexOf(kinds, c.Kind) < 0) continue;
            int start = offset + c.Start, end = offset + Math.Max(c.Start, c.End);
            bool within = problem.Kind == ProblemKind.Reversed ? start == problem.Start && offset + c.End == problem.End && c.End < c.Start
                : problem.End > problem.Start ? start < problem.End && end > problem.Start
                : start <= problem.Start && end >= problem.Start;
            if (within) found.Add((nodes, c));
        }
        // The one that begins last first; of two that begin together, the one of the kind named second.
        return found.OrderByDescending(f => f.Command.Start).ThenByDescending(f => Array.IndexOf(kinds, f.Command.Kind)).ToList();
    }

    /// <summary>What is wrong with the commands of a group of sprites.</summary>
    public static void Find(SbObjectGroup group, List<Problem> found)
    {
        if(group.CompositionWarning) found.Add(new Problem(ProblemKind.SpaceComposition,null,group,CommandKind.R,0,0));
        if (Tidy(group.Commands, out int kinds) && ((kinds & MoveBit) == 0 || (kinds & AxesBits) == 0)) return;
        foreach (var kind in SbInfo.AllKinds.Concat(SbInfo.DepthKinds))
        {
            if (kind == CommandKind.P) continue;
            Reversed(group.Commands, kind, 0, null, group, null, found);
            foreach (var (start, end) in SameKind(group.Commands, kind))
                found.Add(new Problem(ProblemKind.Overlap, null, group, kind, start, end));
        }
        // A group has no vector scale: only its move can be at odds with MX and MY.
        foreach (var (start, end) in CommandClash.Overlaps(group.Commands, CommandKind.M))
            found.Add(new Problem(ProblemKind.Clash, null, group, CommandKind.M, start, end));
    }

    /// <summary>The files an object needs that are not there: one entry for the object, however many they are.</summary>
    /// <param name="exists">
    /// Whether a file, named the way a script names it, is among the project's assets. What it says about
    /// an object is kept with the object for as long as the same delegate asks and the object names the
    /// same files: a storyboard is looked through after every edit, and asking about each of tens of
    /// thousands of objects every time is what would take the time. So when the files change, it is
    /// another delegate that has to be passed.
    /// </param>
    public static void FindMissing(SbObject obj, Func<string, bool> exists, List<Problem> found)
    {
        int frames = obj is SbSprite { IsAnimation: true } animation ? Math.Max(1, animation.FrameCount) : 0;
        var known = obj.FilesChecked;
        if (!ReferenceEquals(known.Path, obj.Path) || known.Frames != frames || !ReferenceEquals(known.Asked, exists))
        {
            bool Exists(string path) => obj is SbSprite ? ImageAssets.Find(path, exists) != null : exists(path);
            string? first = null;
            int count = 0;
            if (obj.Path.Length > 0 && frames > 0)
            {
                for (int i = 0; i < frames; i++)
                {
                    string file = SbSprite.FramePath(obj.Path, i);
                    if (Exists(file)) continue;
                    first ??= file;
                    count++;
                }
            }
            else if (obj.Path.Length > 0 && !Exists(obj.Path))
            {
                first = obj.Path;
                count = 1;
            }
            obj.FilesChecked = known = (obj.Path, frames, exists, first, count);
        }
        if (known.Missing == null) return;

        int time = obj switch
        {
            SbSprite sprite => sprite.Timeline.HasCommands ? sprite.Timeline.StartTime : 0,
            SbSample sample => sample.Time,
            SbVideo video => video.Time,
            _ => 0,
        };
        found.Add(new Problem(ProblemKind.MissingFile, obj, null, default, time, time, known.Missing, known.Count));
    }

    /// <summary>
    /// Every problem of a storyboard in order of time: of its groups of sprites and of its objects, those
    /// of the difficulties that are not shown included, as saving and exporting write them all.
    /// </summary>
    /// <param name="exists">
    /// Whether a file is among the project's assets, see <see cref="FindMissing"/>: the same delegate for
    /// as long as the files are the same. Without it missing files are not looked for.
    /// </param>
    public static List<Problem> Find(Storyboard storyboard, Func<string, bool>? exists = null)
    {
        var all = new List<Problem>();
        foreach (var group in storyboard.Groups) Find(group, all);
        Add(storyboard.Objects, exists, all);
        Add(storyboard.Parked, exists, all);
        all.Sort(static (a, b) =>
        {
            int order = a.Start.CompareTo(b.Start);
            if (order == 0) order = (a.Object?.Id ?? -a.Group!.Id).CompareTo(b.Object?.Id ?? -b.Group!.Id);
            if (order == 0) order = a.Kind.CompareTo(b.Kind);
            return order != 0 ? order : a.Command.CompareTo(b.Command);
        });
        return all;
    }

    private static void Add(List<SbObject> objects, Func<string, bool>? exists, List<Problem> all)
    {
        foreach (var obj in objects)
        {
            if (exists != null) FindMissing(obj, exists, all);
            if (obj is SbSprite sprite && sprite.CommandProblems is { Length: > 0 } found) all.AddRange(found);
        }
    }

    /// <summary>
    /// The stretches in which a command of a kind runs while another of that kind is still running: from
    /// where the later one begins to where the first of the two ends. Commands that follow one another,
    /// the one beginning at the moment the other ends, do not: that is how keyframes are written.
    /// </summary>
    public static List<(int Start, int End)> SameKind(IList nodes, CommandKind kind)
    {
        var found = new List<(int Start, int End)>();
        int running = int.MinValue;
        foreach (var c in KeyTrack.Commands(nodes, kind))
        {
            int end = Math.Max(c.Start, c.End);
            if (c.Start < running) found.Add((c.Start, Math.Min(end, running)));
            running = Math.Max(running, end);
        }
        return CommandClash.Join(found);
    }

    private static void Reversed(IList nodes, CommandKind kind, int offset, SbSprite? sprite, SbObjectGroup? group, SbLoop? loop, List<Problem> found)
    {
        foreach (object? node in nodes)
            if (node is SbCommand c && c.Kind == kind && c.End < c.Start)
                found.Add(new Problem(ProblemKind.Reversed, sprite, group, kind, offset + c.Start, offset + c.End, Loop: loop));
    }

    /// <summary>
    /// One pass over a list of commands: whether those of each kind come in order, one after the other
    /// without sharing time, and none ends before it starts. P is left out: it switches something on for a
    /// while, and two of them at once do no harm.
    /// </summary>
    /// <param name="kinds">A bit for each kind the list has, as far as it was looked at.</param>
    private static bool Tidy(IList nodes, out int kinds)
    {
        Span<int> ends = stackalloc int[KindCount];
        ends.Fill(int.MinValue);
        kinds = 0;
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i] is not SbCommand c || c.Kind == CommandKind.P) continue;
            int kind = (int)c.Kind;
            kinds |= 1 << kind;
            if (c.End < c.Start || c.Start < ends[kind]) return false;
            ends[kind] = c.End;
        }
        return true;
    }
}
