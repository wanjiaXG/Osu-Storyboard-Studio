using System.Globalization;
using System.Text.RegularExpressions;

namespace SbStudio.Core.Updates;

/// <summary>The supported release scheme: major.minor.patch, optionally followed by -beta.N.</summary>
public sealed record ReleaseVersion(Version Number, int? Beta) : IComparable<ReleaseVersion>
{
    public bool IsBeta => Beta.HasValue;

    public static ReleaseVersion Parse(string value)
    {
        var match = Regex.Match(value, @"^v?((?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*))(?:-beta\.([1-9][0-9]*))?$", RegexOptions.CultureInvariant);
        if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var number))
            throw new InvalidDataException("Expected a version such as v0.1.0-beta.1 or v1.0.0.");
        int? beta = null;
        if (match.Groups[2].Success)
        {
            if (!int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
                throw new InvalidDataException("The Beta sequence number is too large.");
            beta = index;
        }
        return new ReleaseVersion(number, beta);
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other == null) return 1;
        int core = Number.CompareTo(other.Number);
        if (core != 0) return core;
        if (!IsBeta) return other.IsBeta ? 1 : 0;
        return other.IsBeta ? Beta!.Value.CompareTo(other.Beta!.Value) : -1;
    }

    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;
    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;
    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;
    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;
}
