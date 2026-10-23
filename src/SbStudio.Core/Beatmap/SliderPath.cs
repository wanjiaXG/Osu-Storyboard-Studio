using System.Numerics;

namespace SbStudio.Core;

/// <summary>The kinds of curve a slider is drawn with, by the letter a .osu file names them with.</summary>
public enum SplineKind
{
    Catmull,
    Bezier,
    Linear,
    PerfectCurve,
}

/// <summary>
/// A point a slider's curve is shaped by. One that has a kind begins a piece of the curve of that kind,
/// which runs up to the next such point.
/// </summary>
/// <param name="Degree">For a B-spline the degree it is of; without one a Bézier piece is a single Bézier curve.</param>
public readonly record struct PathPoint(Vector2 Position, SplineKind? Kind = null, int? Degree = null);

/// <summary>
/// Turns the control points of a curve into the corner points of a line that follows it closely. The same
/// methods, with the same tolerances, as osu!lazer's PathApproximator, so that a slider has the shape and
/// the length it has in the game.
/// </summary>
public static class PathApproximator
{
    private const float BezierTolerance = 0.25f;
    private const int CatmullDetail = 50;
    private const float CircularArcTolerance = 0.1f;

    /// <summary>What osu! takes for equal when it compares two single-precision numbers.</summary>
    public static bool AlmostEquals(float a, float b) => Math.Abs(a - b) <= 1e-3f;

    public static bool AlmostEquals(Vector2 a, Vector2 b) => AlmostEquals(a.X, b.X) && AlmostEquals(a.Y, b.Y);

    public static List<Vector2> Linear(ReadOnlySpan<Vector2> points)
    {
        var result = new List<Vector2>(points.Length);
        foreach (var point in points) result.Add(point);
        return result;
    }

    public static List<Vector2> Bezier(ReadOnlySpan<Vector2> points) => BSpline(points, Math.Max(1, points.Length - 1));

    /// <summary>A B-spline of a degree; with a degree of one less than the number of points it is one Bézier curve.</summary>
    public static List<Vector2> BSpline(ReadOnlySpan<Vector2> points, int degree)
    {
        var output = new List<Vector2>();
        if (points.Length < 2)
        {
            if (points.Length == 1) output.Add(points[0]);
            return output;
        }
        degree = Math.Clamp(degree, 1, points.Length - 1);
        int last = points.Length - 1;

        // The curves that are not followed closely enough yet. Each is halved until its halves are flat.
        var toFlatten = BSplineToBezier(points, degree);
        var free = new Stack<Vector2[]>();
        var buffer1 = new Vector2[degree + 1];
        var buffer2 = new Vector2[degree * 2 + 1];
        var left = buffer2;

        while (toFlatten.Count > 0)
        {
            var parent = toFlatten.Pop();
            if (IsFlatEnough(parent))
            {
                Approximate(parent, output, buffer1, buffer2, degree + 1);
                free.Push(parent);
                continue;
            }

            var right = free.Count > 0 ? free.Pop() : new Vector2[degree + 1];
            Subdivide(parent, left, right, buffer1, degree + 1);
            for (int i = 0; i < degree + 1; i++) parent[i] = left[i];
            toFlatten.Push(right);
            toFlatten.Push(parent);
        }

        output.Add(points[last]);
        return output;
    }

    /// <summary>The Bézier curves a B-spline is made of, the first one on top of the stack.</summary>
    private static Stack<Vector2[]> BSplineToBezier(ReadOnlySpan<Vector2> controlPoints, int degree)
    {
        var result = new Stack<Vector2[]>();
        int pointCount = controlPoints.Length - 1;
        var points = controlPoints.ToArray();

        if (degree == pointCount)
        {
            result.Push(points);
            return result;
        }

        // Each knot is put in again and again (Boehm's algorithm) until the spline falls apart at it.
        for (int i = 0; i < pointCount - degree; i++)
        {
            var piece = new Vector2[degree + 1];
            piece[0] = points[i];
            for (int j = 0; j < degree - 1; j++)
            {
                piece[j + 1] = points[i + 1];
                for (int k = 1; k < degree - j; k++)
                {
                    int l = Math.Min(k, pointCount - degree - i);
                    points[i + k] = (l * points[i + k] + points[i + k + 1]) / (l + 1);
                }
            }
            piece[degree] = points[i + 1];
            result.Push(piece);
        }
        result.Push(points[(pointCount - degree)..]);
        // The other way round, so that they come off in the order of the curve.
        return new Stack<Vector2[]>(result);
    }

    private static bool IsFlatEnough(Vector2[] points)
    {
        for (int i = 1; i < points.Length - 1; i++)
            if ((points[i - 1] - 2 * points[i] + points[i + 1]).LengthSquared() > BezierTolerance * BezierTolerance * 4) return false;
        return true;
    }

    private static void Subdivide(Vector2[] points, Vector2[] left, Vector2[] right, Vector2[] buffer, int count)
    {
        var midpoints = buffer;
        for (int i = 0; i < count; i++) midpoints[i] = points[i];
        for (int i = 0; i < count; i++)
        {
            left[i] = midpoints[0];
            right[count - i - 1] = midpoints[count - i - 1];
            for (int j = 0; j < count - i - 1; j++) midpoints[j] = (midpoints[j] + midpoints[j + 1]) / 2;
        }
    }

    private static void Approximate(Vector2[] points, List<Vector2> output, Vector2[] buffer1, Vector2[] buffer2, int count)
    {
        var left = buffer2;
        var right = buffer1;
        Subdivide(points, left, right, buffer1, count);
        for (int i = 0; i < count - 1; i++) left[count + i] = right[i + 1];

        output.Add(points[0]);
        for (int i = 1; i < count - 1; i++)
        {
            int index = 2 * i;
            output.Add(0.25f * (left[index - 1] + 2 * left[index] + left[index + 1]));
        }
    }

    public static List<Vector2> Catmull(ReadOnlySpan<Vector2> points)
    {
        var result = new List<Vector2>(Math.Max(0, (points.Length - 1) * CatmullDetail * 2));
        for (int i = 0; i < points.Length - 1; i++)
        {
            var v1 = i > 0 ? points[i - 1] : points[i];
            var v2 = points[i];
            var v3 = i < points.Length - 1 ? points[i + 1] : v2 + v2 - v1;
            var v4 = i < points.Length - 2 ? points[i + 2] : v3 + v3 - v2;
            for (int c = 0; c < CatmullDetail; c++)
            {
                result.Add(CatmullPoint(v1, v2, v3, v4, (float)c / CatmullDetail));
                result.Add(CatmullPoint(v1, v2, v3, v4, (float)(c + 1) / CatmullDetail));
            }
        }
        return result;
    }

    private static Vector2 CatmullPoint(Vector2 v1, Vector2 v2, Vector2 v3, Vector2 v4, float t)
    {
        float t2 = t * t, t3 = t * t2;
        return new Vector2(
            0.5f * (2f * v2.X + (-v1.X + v3.X) * t + (2f * v1.X - 5f * v2.X + 4f * v3.X - v4.X) * t2 + (-v1.X + 3f * v2.X - 3f * v3.X + v4.X) * t3),
            0.5f * (2f * v2.Y + (-v1.Y + v3.Y) * t + (2f * v1.Y - 5f * v2.Y + 4f * v3.Y - v4.Y) * t2 + (-v1.Y + 3f * v2.Y - 3f * v3.Y + v4.Y) * t3));
    }

    /// <summary>The circle through three points, and the part of it that runs from the first to the last by way of the middle one.</summary>
    public readonly struct CircularArc
    {
        public readonly bool IsValid;
        public readonly double ThetaStart, ThetaRange, Direction;
        public readonly float Radius;
        public readonly Vector2 Centre;

        public CircularArc(ReadOnlySpan<Vector2> points)
        {
            Vector2 a = points[0], b = points[1], c = points[2];
            ThetaStart = ThetaRange = Direction = 0;
            Radius = 0;
            Centre = default;
            IsValid = false;
            // Three points in a line have no circle through them.
            if (AlmostEquals(0, (b.Y - a.Y) * (c.X - a.X) - (b.X - a.X) * (c.Y - a.Y))) return;

            float d = 2 * (a.X * (b - c).Y + b.X * (c - a).Y + c.X * (a - b).Y);
            float aSq = a.LengthSquared(), bSq = b.LengthSquared(), cSq = c.LengthSquared();
            Centre = new Vector2(
                aSq * (b - c).Y + bSq * (c - a).Y + cSq * (a - b).Y,
                aSq * (c - b).X + bSq * (a - c).X + cSq * (b - a).X) / d;

            Vector2 dA = a - Centre, dC = c - Centre;
            Radius = dA.Length();
            ThetaStart = Math.Atan2(dA.Y, dA.X);
            double thetaEnd = Math.Atan2(dC.Y, dC.X);
            while (thetaEnd < ThetaStart) thetaEnd += 2 * Math.PI;
            Direction = 1;
            ThetaRange = thetaEnd - ThetaStart;

            // Which way round depends on the side of the line from first to last that the middle point lies on.
            Vector2 orthoAtoC = c - a;
            orthoAtoC = new Vector2(orthoAtoC.Y, -orthoAtoC.X);
            if (Vector2.Dot(orthoAtoC, b - a) < 0)
            {
                Direction = -Direction;
                ThetaRange = 2 * Math.PI - ThetaRange;
            }
            IsValid = true;
        }

        /// <summary>How many corner points the arc is followed with.</summary>
        public int PointCount => 2f * Radius <= CircularArcTolerance
            ? 2
            : Math.Max(2, (int)Math.Ceiling(ThetaRange / (2.0 * Math.Acos(1f - CircularArcTolerance / Radius))));
    }

    public static List<Vector2> CircularArcPoints(ReadOnlySpan<Vector2> points)
    {
        var arc = new CircularArc(points);
        if (!arc.IsValid) return Bezier(points);

        int count = arc.PointCount;
        var output = new List<Vector2>(count);
        for (int i = 0; i < count; i++)
        {
            double fraction = (double)i / (count - 1);
            double theta = arc.ThetaStart + arc.Direction * fraction * arc.ThetaRange;
            output.Add(arc.Centre + new Vector2((float)Math.Cos(theta), (float)Math.Sin(theta)) * arc.Radius);
        }
        return output;
    }
}

/// <summary>
/// The line a slider follows: its control points worked out into corner points, and cut or stretched to
/// the length the beatmap gives. After osu!lazer's SliderPath.
/// </summary>
public sealed class SliderPath
{
    private readonly List<Vector2> path = new();
    private readonly List<double> cumulativeLength = new();

    public IReadOnlyList<PathPoint> ControlPoints { get; }

    /// <summary>The length the beatmap gives the slider; null to take the curve as long as it is.</summary>
    public double? ExpectedDistance { get; }

    /// <summary>The corner points, from the slider's own position as (0, 0).</summary>
    public IReadOnlyList<Vector2> Points => path;

    /// <summary>The length of the line, after it has been cut or stretched.</summary>
    public double Distance => cumulativeLength.Count == 0 ? 0 : cumulativeLength[^1];

    /// <param name="optimiseCatmull">
    /// Leave out the small loops a Catmull curve makes around points that are given twice, as osu!stable
    /// does when it draws; the osu! ruleset has this on.
    /// </param>
    public SliderPath(IReadOnlyList<PathPoint> controlPoints, double? expectedDistance, bool optimiseCatmull = true)
    {
        ControlPoints = controlPoints;
        ExpectedDistance = expectedDistance;
        double optimisedLength = CalculatePath(optimiseCatmull);
        CalculateLength(optimisedLength);
    }

    /// <returns>The length that was taken out of Catmull pieces, which still counts as part of the curve.</returns>
    private double CalculatePath(bool optimiseCatmull)
    {
        double optimisedLength = 0;
        if (ControlPoints.Count == 0) return 0;

        var vertices = new Vector2[ControlPoints.Count];
        for (int i = 0; i < vertices.Length; i++) vertices[i] = ControlPoints[i].Position;

        int start = 0;
        for (int i = 0; i < ControlPoints.Count; i++)
        {
            if (ControlPoints[i].Kind == null && i < ControlPoints.Count - 1) continue;

            // This point ends the piece that began at the last point with a kind.
            var segment = vertices.AsSpan(start, i - start + 1);
            if (segment.Length == 1) path.Add(segment[0]);
            else if (segment.Length > 1)
            {
                var sub = CalculateSubPath(segment, ControlPoints[start].Kind ?? SplineKind.Linear, ControlPoints[start].Degree, optimiseCatmull, ref optimisedLength);
                // Where one piece ends is where the next begins: that point is not put in twice.
                bool skipFirst = path.Count > 0 && sub.Count > 0 && path[^1] == sub[0];
                for (int j = skipFirst ? 1 : 0; j < sub.Count; j++) path.Add(sub[j]);
            }
            start = i;
        }
        return optimisedLength;
    }

    private static List<Vector2> CalculateSubPath(ReadOnlySpan<Vector2> points, SplineKind kind, int? degree, bool optimiseCatmull, ref double optimisedLength)
    {
        switch (kind)
        {
            case SplineKind.Linear:
                return PathApproximator.Linear(points);

            case SplineKind.PerfectCurve:
            {
                if (points.Length != 3) break;
                var arc = new PathApproximator.CircularArc(points);
                // No circle through them, or one so large that a Bézier curve does as well.
                if (!arc.IsValid || arc.PointCount >= 1000) break;
                var sub = PathApproximator.CircularArcPoints(points);
                if (sub.Count == 0) break;
                return sub;
            }

            case SplineKind.Catmull:
            {
                var sub = PathApproximator.Catmull(points);
                if (!optimiseCatmull) return sub;

                // osu!stable only keeps corner points that are 6 pixels apart. That matters for the loops a
                // Catmull curve makes around a point given twice, so the same is done here, and the length
                // taken out is kept so that the curve is not stretched to make up for it.
                var optimised = new List<Vector2>(sub.Count);
                Vector2? lastStart = null;
                double removed = 0;
                const int segmentLength = 50 * 2;
                for (int i = 0; i < sub.Count; i++)
                {
                    if (lastStart == null)
                    {
                        optimised.Add(sub[i]);
                        lastStart = sub[i];
                        continue;
                    }
                    double fromStart = Vector2.Distance(lastStart.Value, sub[i]);
                    removed += Vector2.Distance(sub[i - 1], sub[i]);
                    if (fromStart > 6 || (i + 1) % segmentLength == 0 || i == sub.Count - 1)
                    {
                        optimised.Add(sub[i]);
                        optimisedLength += removed - fromStart;
                        lastStart = null;
                        removed = 0;
                    }
                }
                return optimised;
            }
        }
        return PathApproximator.BSpline(points, degree ?? points.Length);
    }

    private void CalculateLength(double optimisedLength)
    {
        double calculated = optimisedLength;
        cumulativeLength.Add(0);
        for (int i = 0; i < path.Count - 1; i++)
        {
            calculated += (path[i + 1] - path[i]).Length();
            cumulativeLength.Add(calculated);
        }

        if (ExpectedDistance is not { } expected || calculated == expected) return;

        // As in osu!stable: a curve whose last two points are the same is not stretched.
        if (path.Count >= 2 && path[^1] == path[^2] && expected > calculated)
        {
            cumulativeLength.Add(calculated);
            return;
        }

        cumulativeLength.RemoveAt(cumulativeLength.Count - 1);
        int end = path.Count - 1;
        if (calculated > expected)
        {
            // Too long: whatever lies wholly beyond the wanted length goes.
            while (cumulativeLength.Count > 0 && cumulativeLength[^1] >= expected)
            {
                cumulativeLength.RemoveAt(cumulativeLength.Count - 1);
                path.RemoveAt(end--);
            }
        }
        if (end <= 0)
        {
            cumulativeLength.Add(0);
            return;
        }

        // The last stretch is made as long as it takes to get to the wanted length.
        Vector2 stretch = path[end] - path[end - 1];
        // Two points in one place give no direction to go on in; the line then ends where it is.
        if (stretch.LengthSquared() > 0)
            path[end] = path[end - 1] + Vector2.Normalize(stretch) * (float)(expected - cumulativeLength[^1]);
        cumulativeLength.Add(expected);
    }

    /// <summary>The place on the line at a part of its length, from 0 at the start to 1 at the end.</summary>
    public Vector2 PositionAt(double progress)
    {
        double d = Math.Clamp(progress, 0, 1) * Distance;
        int i = cumulativeLength.BinarySearch(d);
        if (i < 0) i = ~i;
        return Interpolate(i, d);
    }

    /// <summary>The corner points of the part of the line between two parts of its length.</summary>
    public void GetPathToProgress(List<Vector2> result, double p0, double p1)
    {
        double d0 = Math.Clamp(p0, 0, 1) * Distance, d1 = Math.Clamp(p1, 0, 1) * Distance;
        result.Clear();
        int i = 0;
        for (; i < path.Count && cumulativeLength[i] < d0; i++)
        {
        }
        result.Add(Interpolate(i, d0));
        for (; i < path.Count && cumulativeLength[i] <= d1; i++) result.Add(path[i]);
        result.Add(Interpolate(i, d1));
    }

    private Vector2 Interpolate(int i, double d)
    {
        if (path.Count == 0) return Vector2.Zero;
        if (i <= 0) return path[0];
        if (i >= path.Count) return path[^1];

        Vector2 p0 = path[i - 1], p1 = path[i];
        double d0 = cumulativeLength[i - 1], d1 = cumulativeLength[i];
        // Two points that are all but in one place.
        if (Math.Abs(d0 - d1) <= 1e-7) return p0;
        return p0 + (p1 - p0) * (float)((d - d0) / (d1 - d0));
    }
}
