using System.Collections;
using SbStudio.Core;

namespace SbStudio.App;

internal static class KeyNavigation
{
    internal static IEnumerable<int> Times(IList nodes, CommandKind? kind, int origin, SbLoop? loop = null,
        bool boundsOnly = false)
    {
        int passes = loop?.Iterations ?? 1, length = loop?.IterationLength ?? 0;
        // Keep the same limit for navigation and for the availability shown in the timeline.
        if ((long)passes * nodes.Count > 50000) passes = 1;
        foreach (var node in nodes)
        {
            if (node is not SbCommand c || (kind != null && c.Kind != kind)) continue;
            for (int pass = 0; pass < passes; pass += boundsOnly ? Math.Max(1, passes - 1) : 1)
            {
                yield return origin + pass * length + c.Start;
                yield return origin + pass * length + c.End;
            }
        }
    }

    internal static (bool Previous, bool Next) Available(IList nodes, CommandKind kind, double time,
        int origin = 0, SbLoop? loop = null)
    {
        bool previous = false, next = false;
        // First and last passes suffice for availability; avoid expanding a loop on every UI frame.
        foreach (int key in Times(nodes, kind, origin, loop, boundsOnly: true))
        {
            previous |= key < time - 0.001;
            next |= key > time + 0.001;
            if (previous && next) break;
        }
        return (previous, next);
    }
}
