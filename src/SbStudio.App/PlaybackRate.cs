using System.Globalization;
using SbStudio.Core;

namespace SbStudio.App;

/// <summary>The playback speeds the player accepts: what the audio engine can stretch to (BASS_FX allows 0.05× and up).</summary>
public static class PlaybackRate
{
    public const double Min = 0.05, Max = 10;

    /// <summary>Why a typed speed cannot be used, or null with the speed in <paramref name="rate"/>.</summary>
    public static string? Problem(string text, out double rate)
    {
        rate = 0;
        string trimmed = text.Trim().TrimEnd('x', 'X', '×').Trim();
        if (trimmed.Length == 0) return L.T("input.empty");
        if (!double.TryParse(trimmed, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out rate)
            || !double.IsFinite(rate)) return L.T("input.notNumber", trimmed);
        if (rate < Min || rate > Max) return L.T("viewer.rate.invalid", Min, Max);
        if (rate != Math.Round(rate, 3)) return L.T("viewer.rate.precision");
        return null;
    }
}
