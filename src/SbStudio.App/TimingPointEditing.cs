using SbStudio.Core;
using System.Globalization;

namespace SbStudio.App;

internal static class TimingPointEditing
{
    public static string? Validate(IReadOnlyList<TimingPoint> points, double offset, double bpm, int meter)
    {
        if (!double.IsFinite(offset) || offset != Math.Truncate(offset) || offset < int.MinValue || offset > int.MaxValue)
            return "timing.invalidOffset";
        if (ValidateBpm(bpm) is { } bpmError) return bpmError;
        if (meter < 1 || meter > 16) return "timing.invalidMeter";
        if (points.Any(p => Math.Abs(p.Offset - offset) < 0.000001)) return "timing.duplicateOffset";
        return null;
    }

    public static string? ValidateInput(IReadOnlyList<TimingPoint> points, string offsetText, string bpmText, int meter,
        out int offset, out double bpm)
    {
        bpm = 0;
        if (!int.TryParse(offsetText.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out offset))
            return "timing.invalidOffset";
        if (ValidateBpmInput(bpmText, out bpm) is { } error) return error;
        return Validate(points, offset, bpm, meter);
    }

    public static string? ValidateBpm(double bpm) => !double.IsFinite(bpm) || bpm <= 0 || bpm > 1000
        ? "timing.invalidBpm" : bpm != Math.Round(bpm, 3) ? "timing.bpmPrecision" : null;

    public static string? ValidateBpmInput(string bpmText, out double bpm)
    {
        string text = bpmText.Trim();
        if (!double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out bpm)
            || !double.IsFinite(bpm) || bpm <= 0 || bpm > 1000) return "timing.invalidBpm";
        int dot = text.IndexOf('.');
        if (dot >= 0 && text.Length - dot - 1 > 3) return "timing.bpmPrecision";
        return ValidateBpm(bpm);
    }

    /// <summary>
    /// A typed Offset for one red line: whole milliseconds, and strictly between its neighbours so that red
    /// lines keep their order, as with the -/+ buttons. <paramref name="neighbours"/> describes the allowed
    /// range when that is what failed.
    /// </summary>
    public static string? ValidateOffsetInput(IReadOnlyList<TimingPoint> points, TimingPoint point, string offsetText,
        out double offset, out string neighbours)
    {
        offset = 0;
        neighbours = "";
        if (!int.TryParse(offsetText.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int typed))
            return "timing.invalidOffset";
        offset = typed;
        double? before = points.Where(p => p != point && p.Offset < point.Offset).Select(p => (double?)p.Offset).Max();
        double? after = points.Where(p => p != point && p.Offset > point.Offset).Select(p => (double?)p.Offset).Min();
        if ((before == null || offset > before) && (after == null || offset < after)) return null;
        var limits = new List<string>();
        if (before is { } low) limits.Add("> " + low.ToString("0.###", CultureInfo.InvariantCulture) + " ms");
        if (after is { } high) limits.Add("< " + high.ToString("0.###", CultureInfo.InvariantCulture) + " ms");
        neighbours = string.Join(", ", limits);
        return "timing.offsetNeighbours";
    }

    public static double? OffsetAfterAdjustment(IReadOnlyList<TimingPoint> points, TimingPoint? point, int direction, bool largeStep)
    {
        if (point == null || !points.Contains(point) || direction is not (-1 or 1) || !double.IsFinite(point.Offset)) return null;
        double offset = point.Offset + direction * (largeStep ? 5 : 1);
        if (!double.IsFinite(offset) || offset < int.MinValue || offset > int.MaxValue || offset == point.Offset) return null;
        // Do not merge with or cross another red line. Imported fractional offsets retain their fraction.
        foreach (var other in points)
        {
            if (other == point) continue;
            if (Math.Abs(other.Offset - offset) < 0.000001
                || (other.Offset < point.Offset && offset <= other.Offset)
                || (other.Offset > point.Offset && offset >= other.Offset)) return null;
        }
        return offset;
    }

    // Use the visible playback time, rounded to milliseconds, independently of beat snapping.
    public static int CurrentOffset(double time) => double.IsFinite(time)
        ? (int)Math.Clamp(Math.Round(time), int.MinValue, int.MaxValue) : 0;

    public static TimingPoint? AtOrBefore(IReadOnlyList<TimingPoint> points, double time) =>
        points.Where(p => double.IsFinite(p.Offset) && p.Offset <= time).MaxBy(p => p.Offset);

    public static TimingPoint? AtPlayhead(IReadOnlyList<TimingPoint> points, double time)
    {
        if (!double.IsFinite(time)) return null;
        // As with keyframes, compare at the displayed millisecond, without snapping to the beat grid.
        // Preserve exact selection of fractional offsets from existing imported timing.
        return points.FirstOrDefault(p => p.Offset == time)
            ?? points.FirstOrDefault(p => p.Offset == CurrentOffset(time));
    }
}
