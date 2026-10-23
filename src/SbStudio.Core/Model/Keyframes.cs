using System.Collections;

namespace SbStudio.Core;

/// <summary>
/// The value a property has at one moment, the way a video editor shows it. The osb format has no such
/// thing: it has commands, each saying "from this time to that time, go from this value to that value". A
/// keyframe is where such commands meet: the end of one, the start of the next, or both.
/// </summary>
public sealed class Keyframe
{
    /// <summary>
    /// In place of an easing: no transition. The value stays as it is until the next keyframe and changes
    /// there at once. In the script it is the absence of a command between the two moments.
    /// </summary>
    public const int Hold = -1;

    public int Time;
    /// <summary>The value at <see cref="Time"/>. It is the array of a command: change it with <see cref="KeyTrack.SetValue"/>.</summary>
    public double[] Value = Array.Empty<double>();
    /// <summary>
    /// How the value gets from this keyframe to the next one: the number of an easing, or <see cref="Hold"/>.
    /// The last keyframe has nothing after it, so there it means nothing.
    /// </summary>
    public int Easing = Hold;
    /// <summary>
    /// A transition drawn by hand, which then takes the place of <see cref="Easing"/>: the curve of the
    /// command that leaves from here. Only the keyframes of a group of sprites have one.
    /// </summary>
    public EasingCurve? Curve;

    /// <summary>The command that ends at this keyframe and the one that starts at it.</summary>
    public SbCommand? In, Out;
    /// <summary>A command without duration that does nothing but give the value at this moment.</summary>
    public SbCommand? Point;
}

/// <summary>
/// Reads the commands of one kind as keyframes and edits them as such. The commands stay what is stored, so
/// a script written by hand or by another program is only touched where a keyframe is changed.
///
/// A list of nodes is the commands of a sprite, of a loop or trigger, or of a group of sprites.
/// </summary>
public static class KeyTrack
{
    /// <summary>The commands of one kind in order of their start. Those that start together keep their order.</summary>
    public static List<SbCommand> Commands(IList nodes, CommandKind kind)
    {
        var list = new List<SbCommand>();
        foreach (object? node in nodes)
            if (node is SbCommand c && c.Kind == kind) list.Add(c);
        for (int i = 1; i < list.Count; i++)
        {
            // Nearly always in order already; when not, sorted without changing the order of equal starts.
            if (list[i].Start >= list[i - 1].Start) continue;
            return list.OrderBy(c => c.Start).ToList();
        }
        return list;
    }

    public static List<Keyframe>? Read(IList nodes, CommandKind kind) => Read(Commands(nodes, kind));

    /// <summary>
    /// The keyframes that commands in order of start amount to, or null when some of them run at the same
    /// time: a property then has no single value to show at a moment, and the commands have to be edited
    /// as they are.
    /// </summary>
    public static List<Keyframe>? Read(IReadOnlyList<SbCommand> commands)
    {
        var keys = new List<Keyframe>(commands.Count + 1);
        int end = int.MinValue;
        foreach (var c in commands)
        {
            if (c.End < c.Start || c.Start < end) return null;
            end = c.End;

            var last = keys.Count > 0 ? keys[^1] : null;
            // The command starts where the last one left the value: one keyframe, not two.
            bool joins = last != null && last.Time == c.Start && last.Out == null && Same(last.Value, c.From);
            if (c.End == c.Start && Same(c.From, c.To))
            {
                if (joins && last!.Point == null) last.Point = c;
                else keys.Add(new Keyframe { Time = c.Start, Value = c.From, Point = c });
                continue;
            }
            if (joins)
            {
                last!.Out = c;
                last.Easing = c.Easing;
                last.Curve = c.Curve;
            }
            else
            {
                keys.Add(new Keyframe { Time = c.Start, Value = c.From, Out = c, Easing = c.Easing, Curve = c.Curve });
            }
            keys.Add(new Keyframe { Time = c.End, Value = c.To, In = c });
        }
        return keys;
    }

    private static bool Same(double[] a, double[] b) => a.AsSpan().SequenceEqual(b);

    /// <summary>The commands that say what a list of keyframes says. Only time, value and easing of the keyframes count.</summary>
    public static List<SbCommand> Build(CommandKind kind, IReadOnlyList<Keyframe> keys)
    {
        var commands = new List<SbCommand>();
        bool arrived = false;
        for (int i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            bool leaves = i + 1 < keys.Count && key.Easing != Keyframe.Hold;
            if (leaves)
                commands.Add(new SbCommand(kind, key.Easing, key.Time, keys[i + 1].Time, (double[])key.Value.Clone(), (double[])keys[i + 1].Value.Clone()) { Curve = key.Curve });
            // A keyframe that no transition ends at or starts from needs a command of its own to have its value.
            else if (!arrived)
                commands.Add(new SbCommand(kind, 0, key.Time, key.Time, (double[])key.Value.Clone()));
            arrived = leaves;
        }
        return commands;
    }

    /// <summary>Gives a keyframe another value: in every command that meets there.</summary>
    public static void SetValue(Keyframe key, double[] value)
    {
        if (key.In != null) key.In.To = (double[])value.Clone();
        if (key.Out != null) key.Out.From = (double[])value.Clone();
        if (key.Point != null)
        {
            key.Point.From = (double[])value.Clone();
            key.Point.To = (double[])value.Clone();
        }
        key.Value = key.Out?.From ?? key.In?.To ?? key.Point!.From;
    }

    /// <summary>The times a keyframe can be moved to: as far as the one before it and the one after it.</summary>
    public static (int Min, int Max) TimeRange(IReadOnlyList<Keyframe> keys, int index) =>
        (index > 0 ? keys[index - 1].Time : int.MinValue, index + 1 < keys.Count ? keys[index + 1].Time : int.MaxValue);

    /// <summary>
    /// Moves a keyframe in time: the commands that meet there get longer or shorter. The time has to be
    /// within <see cref="TimeRange"/>, or commands would come to run at the same time.
    /// </summary>
    public static void SetTime(Keyframe key, int time)
    {
        key.Time = time;
        if (key.In != null) key.In.End = time;
        if (key.Out != null) key.Out.Start = time;
        if (key.Point != null) key.Point.Start = key.Point.End = time;
    }

    /// <summary>
    /// Puts a keyframe at a time, with a value. Between two keyframes it splits the transition there, and
    /// both halves keep its easing. Before the first one or after the last one, the value travels in a
    /// straight line between the new keyframe and that one.
    /// </summary>
    /// <returns>The place of the keyframe among those of its kind; -1 when the commands cannot be read as keyframes.</returns>
    public static int Insert(IList nodes, CommandKind kind, int time, double[] value)
    {
        var commands = Commands(nodes, kind);
        var keys = Read(commands);
        if (keys == null) return -1;

        int at = keys.FindIndex(k => k.Time >= time);
        if (at >= 0 && keys[at].Time == time)
        {
            // There is one already. It gets the value, which is its own when the value was read off at this time.
            if (!Same(keys[at].Value, value)) SetValue(keys[at], value);
            return at;
        }

        var key = new Keyframe { Time = time, Value = value };
        if (at < 0)
        {
            if (keys.Count > 0) keys[^1].Easing = 0;
            keys.Add(key);
            at = keys.Count - 1;
        }
        else
        {
            key.Easing = at == 0 ? 0 : keys[at - 1].Easing;
            // Both halves go the way the whole did, each with a curve of its own to be changed by itself.
            key.Curve = at == 0 ? null : keys[at - 1].Curve?.Clone();
            keys.Insert(at, key);
        }
        Replace(nodes, commands, Build(kind, keys));
        return at;
    }

    /// <summary>
    /// Takes a keyframe away. The keyframe before it then leads straight to the one after it, with the
    /// transition it had.
    /// </summary>
    public static bool Remove(IList nodes, CommandKind kind, int index)
    {
        var commands = Commands(nodes, kind);
        var keys = Read(commands);
        if (keys == null || index < 0 || index >= keys.Count) return false;
        keys.RemoveAt(index);
        Replace(nodes, commands, Build(kind, keys));
        return true;
    }

    /// <summary>Sets how the value gets from a keyframe to the next one: an easing number or <see cref="Keyframe.Hold"/>.</summary>
    /// <param name="curve">A transition drawn by hand in place of the easing; for the keyframes of a group of sprites alone.</param>
    public static bool SetEasing(IList nodes, CommandKind kind, int index, int easing, EasingCurve? curve = null)
    {
        var commands = Commands(nodes, kind);
        var keys = Read(commands);
        if (keys == null || index < 0 || index >= keys.Count) return false;
        keys[index].Easing = easing;
        keys[index].Curve = easing == Keyframe.Hold ? null : curve;
        Replace(nodes, commands, Build(kind, keys));
        return true;
    }

    // ---------------------------------------------------------------- commands that run at the same time

    /// <summary>
    /// The command that has its way at a time among commands of one kind, some of which may run at the same
    /// time. osu! decides it like this, and so does the preview: of those that are running, the one that
    /// began first, or the one written first when they began together; when none is running, the one that
    /// began last, whose end value stays. Null before the first of them begins.
    /// </summary>
    /// <param name="commands">In order of their start, as <see cref="Commands"/> gives them.</param>
    public static SbCommand? Deciding(IReadOnlyList<SbCommand> commands, int time)
    {
        int last = -1;
        while (last + 1 < commands.Count && commands[last + 1].Start <= time) last++;
        if (last < 0) return null;
        for (int i = 0; i < last; i++)
            if (time < commands[i].End) return commands[i];
        return commands[last];
    }

    /// <summary>What became of a request to <see cref="Pin"/> a value at a time.</summary>
    public enum Pinned
    {
        /// <summary>A command gave the value at that time already; nothing was changed.</summary>
        Found,
        /// <summary>A command was added or cut in two; the picture is what it was.</summary>
        Added,
        /// <summary>It cannot be done without changing the picture at other times; nothing was changed.</summary>
        Blocked,
    }

    /// <summary>
    /// For commands that <see cref="Read"/> cannot make keyframes of, because some run at the same time:
    /// the keyframe at a time all the same, when the command that has its way there (<see cref="Deciding"/>)
    /// begins or ends at just that time. Its value is what the picture shows, and <see cref="SetValue"/>
    /// changes that. Null when no command gives a value of its own at that time.
    /// </summary>
    public static Keyframe? At(IList nodes, CommandKind kind, int time) => At(Commands(nodes, kind), time);

    private static Keyframe? At(List<SbCommand> commands, int time)
    {
        var decider = Deciding(commands, time);
        if (decider == null) return null;
        if (decider.End <= decider.Start)
        {
            // Without duration it gives its end value at once, and keeps it from then on.
            if (time != decider.Start) return null;
            return Same(decider.From, decider.To)
                ? new Keyframe { Time = time, Value = decider.From, Point = decider }
                : new Keyframe { Time = time, Value = decider.To, In = decider };
        }
        if (time == decider.End) return new Keyframe { Time = time, Value = decider.To, In = decider };
        if (time != decider.Start) return null;

        var key = new Keyframe { Time = time, Value = decider.From, Out = decider, Easing = decider.Easing, Curve = decider.Curve };
        // A command that ends here with the value this one starts with is the same keyframe from its other side.
        foreach (var c in commands)
            if (c != decider && c.End == time && c.End > c.Start && Same(c.To, decider.From)) key.In = c;
        return key;
    }

    /// <summary>
    /// The same as <see cref="At"/>, but where no command gives a value of its own at the time, one is made
    /// to: the way <see cref="Insert"/> does it for keyframes, and without changing the picture.
    /// Before the first command, a new one leads up to it in a straight line. After the command that has
    /// its way has ended and its value only stays, a new one gives that value at the time, and, when
    /// nothing comes after, the value travels there from where the commands end. Inside the command that
    /// has its way, that command is cut in two, both halves keeping its easing.
    ///
    /// The cut is what cannot always be made. The second half begins at the time, and a command of the kind
    /// that began before and is still running, unseen so far, would have its way over it from there on.
    /// Then nothing is changed and the outcome is <see cref="Pinned.Blocked"/>.
    /// </summary>
    /// <param name="value">What the property is at that time, as the picture has it.</param>
    public static Keyframe? Pin(IList nodes, CommandKind kind, int time, double[] value, out Pinned outcome)
    {
        var commands = Commands(nodes, kind);
        outcome = Pinned.Found;
        if (At(commands, time) is { } there) return there;
        outcome = Pinned.Added;
        if (commands.Count == 0)
        {
            var only = new SbCommand(kind, 0, time, time, (double[])value.Clone());
            nodes.Add(only);
            return new Keyframe { Time = time, Value = only.From, Point = only };
        }

        var decider = Deciding(commands, time);
        if (decider == null)
        {
            var first = commands[0];
            var lead = new SbCommand(kind, 0, time, first.Start, (double[])value.Clone(), (double[])first.From.Clone());
            nodes.Insert(nodes.IndexOf(first), lead);
            return new Keyframe { Time = time, Value = lead.From, Out = lead, Easing = 0 };
        }

        if (time > Math.Max(decider.Start, decider.End))
        {
            // Its end value stays. With commands still to come, it is given once more here, to be held
            // until they begin; after the last of them, the value travels here from where they all end.
            int ends = commands.Max(c => Math.Max(c.Start, c.End));
            bool travels = ends < time && !commands.Exists(c => c.Start > time);
            var added = travels
                ? new SbCommand(kind, 0, ends, time, (double[])decider.To.Clone(), (double[])value.Clone())
                : new SbCommand(kind, 0, time, time, (double[])value.Clone());
            nodes.Insert(nodes.IndexOf(decider) + 1, added);
            return travels
                ? new Keyframe { Time = time, Value = added.To, In = added }
                : new Keyframe { Time = time, Value = added.From, Point = added };
        }

        foreach (var c in commands)
        {
            if (c == decider || c.Start > time || Math.Max(c.Start, c.End) <= time) continue;
            outcome = Pinned.Blocked;
            return null;
        }
        var rest = new SbCommand(kind, decider.Easing, time, decider.End, (double[])value.Clone(), decider.To) { Curve = decider.Curve?.Clone() };
        decider.End = time;
        decider.To = (double[])value.Clone();
        nodes.Insert(nodes.IndexOf(decider) + 1, rest);
        return new Keyframe { Time = time, Value = rest.From, In = decider, Out = rest, Easing = rest.Easing, Curve = rest.Curve };
    }

    /// <summary>
    /// Puts new commands in the place of old ones. A new command that spans the same time as an old one
    /// becomes that old one, which so stays where it is in the script; the others are put in next to it.
    /// </summary>
    private static void Replace(IList nodes, List<SbCommand> old, List<SbCommand> fresh)
    {
        var free = new List<SbCommand>(old);
        // Where the next new command goes: after the last one dealt with, or before the first old one.
        int next = old.Count > 0 ? old.Min(c => nodes.IndexOf(c)) : nodes.Count;
        foreach (var command in fresh)
        {
            var same = free.Find(c => c.Start == command.Start && c.End == command.End);
            if (same != null)
            {
                free.Remove(same);
                same.Easing = command.Easing;
                same.Curve = command.Curve;
                same.From = command.From;
                same.To = command.To;
                next = nodes.IndexOf(same) + 1;
            }
            else
            {
                nodes.Insert(Math.Min(next, nodes.Count), command);
                next++;
            }
        }
        foreach (var gone in free) nodes.Remove(gone);
    }
}
