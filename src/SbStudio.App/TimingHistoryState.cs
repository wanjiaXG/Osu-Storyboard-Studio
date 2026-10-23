using SbStudio.Core;

namespace SbStudio.App;

/// <summary>Immutable timing values for the active view and every retained difficulty.</summary>
internal sealed class TimingHistoryState
{
    private readonly record struct Point(double Offset, double BeatLength, int Meter)
    {
        public TimingPoint Create() => new() { Offset = Offset, BeatLength = BeatLength, Meter = Meter };
    }

    private readonly Dictionary<string, Point[]> values = new(StringComparer.OrdinalIgnoreCase);
    private static Point[] Read(IEnumerable<TimingPoint> points) => points.Select(p => new Point(p.Offset, p.BeatLength, p.Meter)).ToArray();

    public static TimingHistoryState Capture(Project project)
    {
        var state = new TimingHistoryState();
        foreach (var difficulty in project.Settings.Difficulties) state.values[difficulty.File] = Read(difficulty.Timing);
        if (project.Settings.Shared != null) state.values[""] = Read(project.Settings.Shared.Timing);
        // The active settings can be newer than the parked difficulty's copy, until a switch or save.
        state.values[project.Settings.Difficulty] = Read(project.Settings.Timing);
        return state;
    }

    public bool SameValues(TimingHistoryState other) => values.Count == other.values.Count &&
        values.All(pair => other.values.TryGetValue(pair.Key, out var points) && pair.Value.AsSpan().SequenceEqual(points));

    /// <returns>Whether the active timing changed and preview audio needs to be rescheduled.</returns>
    public bool Restore(Project project)
    {
        foreach (var difficulty in project.Settings.Difficulties)
            if (values.TryGetValue(difficulty.File, out var points)) difficulty.Timing = points.Select(p => p.Create()).ToList();
        if (project.Settings.Shared != null && values.TryGetValue("", out var shared))
            project.Settings.Shared.Timing = shared.Select(p => p.Create()).ToList();
        if (!values.TryGetValue(project.Settings.Difficulty, out var active) || active.AsSpan().SequenceEqual(Read(project.Settings.Timing))) return false;
        project.Settings.Timing = active.Select(p => p.Create()).ToList();
        return true;
    }
}
