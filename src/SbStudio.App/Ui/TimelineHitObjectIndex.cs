using SbStudio.Core;

namespace SbStudio.App.Ui;

internal sealed record TimelineHitObject(HitObject Object, int Index, int Stack);

/// <summary>Source objects only; no editable copies. Source-file refresh creates a new index.</summary>
internal sealed class TimelineHitObjectIndex
{
    public static readonly TimelineHitObjectIndex Empty = new(null);
    public TimelineHitObject[] Objects { get; }
    private readonly double[] prefixEnds;
    public TimelineHitObjectIndex(OsuBeatmap? map)
    {
        var objects = map?.Objects.Select((obj, i) => (Object: obj, Index: i)).OrderBy(e => e.Object.StartTime).ToArray();
        Objects = new TimelineHitObject[objects?.Length ?? 0];
        prefixEnds = new double[Objects.Length];
        var concurrent = new Stack<HitObject>();
        double end = double.NegativeInfinity;
        for (int i = 0; i < Objects.Length; i++)
        {
            var obj = objects![i];
            // TimelineBlueprintContainer: overlap stacks rise by 5 px, resetting after three
            // levels when time advances. Simultaneous notes stay in the same stack.
            while (concurrent.TryPeek(out var previous) && previous.EndTime < obj.Object.StartTime - 1) concurrent.Pop();
            if (concurrent.Count >= 3 && Math.Abs(concurrent.Peek().StartTime - obj.Object.StartTime) > 1) concurrent.Clear();
            Objects[i] = new(obj.Object, obj.Index, concurrent.Count);
            concurrent.Push(obj.Object);
            prefixEnds[i] = end = Math.Max(end, obj.Object.EndTime);
        }
    }
    public int FirstVisible(double time)
    {
        int low = 0, high = Objects.Length;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (prefixEnds[middle] < time) low = middle + 1; else high = middle;
        }
        return low;
    }
    public int FirstAfter(double time)
    {
        int low = 0, high = Objects.Length;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (Objects[middle].Object.StartTime <= time) low = middle + 1; else high = middle;
        }
        return low;
    }
}
