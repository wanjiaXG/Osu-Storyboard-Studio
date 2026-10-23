namespace SbStudio.Core;

/// <summary>CatchAutoGenerator movement, using the same walking/dashing thresholds and catch margin.</summary>
public static class CatchPreviewMovement
{
    public readonly record struct Frame(double Time, float X);

    public static List<Frame> Build(OsuBeatmap map, IReadOnlyList<CatchPreviewObject> objects)
    {
        var frames = new List<Frame> { new(0, 256) };
        double lastTime = 0;
        float lastX = 256, halfWidth = 106.75f * map.Scale * 0.8f;
        foreach (var obj in objects.OrderBy(o => o.Index).ThenBy(o => o.Time))
        {
            float change = MathF.Abs(lastX - obj.X);
            double available = obj.Time - lastTime;
            if (available < 0) continue;
            double required = change == 0 ? 0 : change / available;
            if (lastX - halfWidth < obj.X && lastX + halfWidth > obj.X)
            {
                lastTime = obj.Time;
                Add(obj.Time, lastX);
                continue;
            }
            if (required > 1) Add(obj.Time, obj.X);
            else if (obj.HyperDash)
            {
                Add(lastTime, lastX);
                Add(obj.Time, obj.X);
            }
            else if (required > 0.5)
            {
                double dashTime = (change / 0.5 - available) / 2;
                float mid = lastX + (obj.X - lastX) * (float)(dashTime / available);
                Add(lastTime + 1, lastX);
                Add(lastTime + dashTime, mid);
                Add(obj.Time, obj.X);
            }
            else
            {
                Add(obj.Time - change / 0.5, lastX);
                Add(obj.Time, obj.X);
            }
            lastTime = obj.Time;
            lastX = obj.X;
        }
        return frames;
        void Add(double time, float x) => frames.Add(new(time, x));
    }

    public static float PositionAt(IReadOnlyList<Frame> frames, double time)
    {
        int low = 0, high = frames.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (frames[middle].Time <= time) low = middle + 1; else high = middle;
        }
        if (low == 0) return 256;
        var previous = frames[low - 1];
        if (low == frames.Count) return previous.X;
        var next = frames[low];
        double progress = next.Time > previous.Time ? (time - previous.Time) / (next.Time - previous.Time) : 1;
        return previous.X + (next.X - previous.X) * (float)Math.Clamp(progress, 0, 1);
    }
}
