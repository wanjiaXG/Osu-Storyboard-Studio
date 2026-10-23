using System.Globalization;

namespace SbStudio.Core;

/// <summary>The uninherited TimingPoints lines to paste into a .osu file, without a section header.</summary>
public static class TimingCode
{
    public static string Format(IEnumerable<TimingPoint> points) => string.Join("\r\n", points.OrderBy(p => p.Offset).Select(p =>
        p.Offset.ToString("R", CultureInfo.InvariantCulture) + "," +
        p.BeatLength.ToString("R", CultureInfo.InvariantCulture) + "," +
        p.Meter.ToString(CultureInfo.InvariantCulture) + ",1,0,100,1,0"));
}
