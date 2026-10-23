using System.Globalization;

namespace SbStudio.Core;

/// <summary>
/// A transition drawn by hand: how far the value has got, from 0 where it starts to 1 where it ends, over
/// the time of the transition, which runs from 0 to 1 too. It is given by the points the curve goes
/// through on its way between its two ends, and may go below 0 and above 1 on the way.
///
/// Only the commands of a group of sprites can have one. The osb format knows its 35 easings and nothing
/// else, and a group is turned into plain commands when the storyboard is exported anyway: there the
/// curve becomes as many short commands as it takes to follow it.
///
/// Between its points the curve is as smooth as it can be without swinging out: where the points only go
/// up, so does the curve, and it only turns round at a point that lies higher or lower than both its
/// neighbours. So it goes beyond 0 and 1 exactly where a point was put there.
/// </summary>
public sealed class EasingCurve
{
    /// <summary>How near in time two points may come to each other, and to the two ends.</summary>
    public const double MinGap = 0.005;
    /// <summary>How far below the start and above the end a point may lie.</summary>
    public const double Lowest = -1, Highest = 2;

    /// <summary>
    /// The points between the two ends, in order of their time. The ends themselves, (0, 0) and (1, 1), are
    /// fixed and not in the list: without any point the curve is the straight line between them.
    /// </summary>
    public readonly List<(double X, double Y)> Points = new();

    public EasingCurve()
    {
    }

    public EasingCurve(IEnumerable<(double X, double Y)> points)
    {
        foreach (var (x, y) in points.OrderBy(p => p.X)) Add(x, y);
    }

    public EasingCurve Clone()
    {
        var copy = new EasingCurve();
        copy.Points.AddRange(Points);
        return copy;
    }

    public bool SameAs(EasingCurve other) => Points.SequenceEqual(other.Points);

    private static double Tidy(double value) => Math.Round(value, 4);

    /// <summary>
    /// Puts a point on the curve. Its time is kept clear of its neighbours' and of the two ends.
    /// </summary>
    /// <returns>The place of the point in <see cref="Points"/>, or -1 when there is no room for one more at that time.</returns>
    public int Add(double x, double y)
    {
        int at = 0;
        while (at < Points.Count && Points[at].X <= x) at++;
        double low = (at > 0 ? Points[at - 1].X : 0) + MinGap, high = (at < Points.Count ? Points[at].X : 1) - MinGap;
        if (high < low) return -1;
        Points.Insert(at, (Tidy(Math.Clamp(x, low, high)), Tidy(Math.Clamp(y, Lowest, Highest))));
        return at;
    }

    /// <summary>Moves a point. It stays between its neighbours in time: the curve has one value at a time.</summary>
    public void Move(int index, double x, double y)
    {
        double low = (index > 0 ? Points[index - 1].X : 0) + MinGap, high = (index + 1 < Points.Count ? Points[index + 1].X : 1) - MinGap;
        Points[index] = (Tidy(Math.Clamp(x, low, Math.Max(low, high))), Tidy(Math.Clamp(y, Lowest, Highest)));
    }

    /// <summary>The value at a point of the transition. For asking many times, <see cref="Compile"/> once and ask that.</summary>
    public double Apply(double t) => Compile().At(t);

    /// <summary>The curve as it is now, ready to be asked for its value at any time. Later changes of the points do not reach it.</summary>
    public Shape Compile() => new(Points);

    /// <summary>A curve as it is at one moment: the points with the slope the curve has at each of them.</summary>
    public sealed class Shape
    {
        private readonly double[] x, y, slope;

        internal Shape(List<(double X, double Y)> points)
        {
            int count = points.Count + 2;
            x = new double[count];
            y = new double[count];
            slope = new double[count];
            x[count - 1] = y[count - 1] = 1;
            for (int i = 0; i < points.Count; i++) (x[i + 1], y[i + 1]) = points[i];

            // The slope of the straight way from each point to the next.
            var rise = new double[count - 1];
            for (int i = 0; i < count - 1; i++) rise[i] = (y[i + 1] - y[i]) / Math.Max(1e-9, x[i + 1] - x[i]);

            // At a point the curve has the mean of the slopes on its two sides, and none where they point
            // opposite ways: there it turns round. At the ends it sets out and arrives the straight way.
            slope[0] = rise[0];
            slope[count - 1] = rise[count - 2];
            for (int i = 1; i < count - 1; i++) slope[i] = rise[i - 1] * rise[i] > 0 ? (rise[i - 1] + rise[i]) / 2 : 0;

            // Kept from swinging out between two points (Fritsch and Carlson): a slope more than three times
            // that of the straight way would carry the curve past the higher of the two.
            for (int i = 0; i < count - 1; i++)
            {
                if (rise[i] == 0)
                {
                    slope[i] = slope[i + 1] = 0;
                    continue;
                }
                double a = slope[i] / rise[i], b = slope[i + 1] / rise[i];
                double size = a * a + b * b;
                if (size <= 9) continue;
                double scale = 3 / Math.Sqrt(size);
                slope[i] = scale * a * rise[i];
                slope[i + 1] = scale * b * rise[i];
            }
        }

        public double At(double t)
        {
            if (t <= 0) return 0;
            if (t >= 1) return 1;
            int i = 0;
            while (i + 2 < x.Length && t >= x[i + 1]) i++;
            double width = x[i + 1] - x[i];
            if (width <= 0) return y[i + 1];
            double s = (t - x[i]) / width, s2 = s * s, s3 = s2 * s;
            return (2 * s3 - 3 * s2 + 1) * y[i] + (s3 - 2 * s2 + s) * width * slope[i] +
                   (3 * s2 - 2 * s3) * y[i + 1] + (s3 - s2) * width * slope[i + 1];
        }
    }

    /// <summary>
    /// A curve that goes the way one of the 35 easings goes, to start from when one of them is nearly
    /// right. Its points are put where they are needed most, one after the other at the place the curve
    /// is furthest off, until it follows the easing closely: a handful for the plain ones, more for the
    /// ones that swing or bounce. The straight line for the linear one, and for no transition at all.
    /// </summary>
    public static EasingCurve From(int easing)
    {
        const int samples = 400, most = 16;
        const double close = 0.01;
        var curve = new EasingCurve();
        if (easing <= 0 || easing >= Easing.Count) return curve;
        while (curve.Points.Count < most)
        {
            var shape = curve.Compile();
            double worst = 0, at = 0;
            for (int i = 1; i < samples; i++)
            {
                double x = i / (double)samples, off = Math.Abs(shape.At(x) - Easing.Apply(easing, x));
                if (off > worst) (worst, at) = (off, x);
            }
            if (worst <= close || curve.Add(at, Easing.Apply(easing, at)) < 0) break;
        }
        return curve;
    }

    // ---------------------------------------------------------------- in the project's script

    private static string Num(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>
    /// The curve as it stands where a command of a group has the number of its easing in the project's
    /// script: "c(" and ")" around its points, each as time;value, with "|" between them. No comma is in
    /// it, as commas are what part the fields of a command.
    /// </summary>
    public string ToToken() => "c(" + string.Join("|", Points.Select(p => Num(p.X) + ";" + Num(p.Y))) + ")";

    /// <summary>Reads what <see cref="ToToken"/> writes. Points that do not fit, out of order or too near to another, are left out.</summary>
    public static bool TryParse(string token, out EasingCurve? curve)
    {
        curve = null;
        token = token.Trim();
        if (token.Length < 3 || !token.StartsWith("c(", StringComparison.OrdinalIgnoreCase) || token[^1] != ')') return false;
        var read = new List<(double X, double Y)>();
        string inner = token.Substring(2, token.Length - 3);
        foreach (string part in inner.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split(';');
            if (pair.Length != 2 ||
                !double.TryParse(pair[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ||
                !double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) ||
                double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y)) return false;
            read.Add((x, y));
        }
        curve = new EasingCurve(read);
        return true;
    }
}
