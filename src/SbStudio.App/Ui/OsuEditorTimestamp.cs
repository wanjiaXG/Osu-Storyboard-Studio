using SbStudio.Core;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SbStudio.App.Ui;

internal static class OsuEditorTimestamp
{
    private static readonly Regex Timestamp = new(@"\A(-?)([0-9]{2,}):([0-5][0-9]):([0-9]{3})(?: \([0-9]+(?:\|[0-9]+)?(?:,[0-9]+(?:\|[0-9]+)?)*\))? -\z",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    public static bool TryParse(string? text, out int time)
    {
        time = 0;
        if (text == null || text.Length > 4096) return false;
        var match = Timestamp.Match(text.Trim());
        if (!match.Success || !long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long minutes)
            || minutes > 35791) return false;
        long ms = minutes * 60000 + int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) * 1000
            + int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
        if (match.Groups[1].Length > 0) ms = -ms;
        if (ms < int.MinValue || ms > int.MaxValue) return false;
        time = (int)ms;
        return true;
    }

    // ComposeScreen.getTimestamp / OsuHitObjectComposer / ManiaHitObjectComposer.
    public static string Format(OsuBeatmap map, IEnumerable<TimelineHitObject> selection, double currentTime)
    {
        var objects = selection.OrderBy(e => e.Object.StartTime).ThenBy(e => e.Index).ToArray();
        double time = objects.Length > 0 ? objects[0].Object.StartTime : currentTime;
        long ms = (long)Math.Abs(time);
        string timestamp = FormattableString.Invariant($"{(time < 0 ? "-" : "")}{ms / 60000:00}:{ms / 1000 % 60:00}:{ms % 1000:000}");
        string description = map.Mode switch
        {
            0 or 2 => string.Join(',', objects.Select(e => e.Object.IndexInCurrentCombo + 1)),
            3 => string.Join(',', objects.Select(e => FormattableString.Invariant($"{Math.Round(e.Object.StartTime)}|{map.ColumnOf(e.Object)}"))),
            _ => "" // Taiko's composer has no object description; its timestamp still seeks correctly.
        };
        return timestamp + (description.Length > 0 ? $" ({description})" : "") + " - ";
    }

    // Stable ChatEngine.HandleLink only decodes spaces in edit links; retain the other delimiters.
    public static string Link(string timestamp) => "osu://edit/" + timestamp.Replace(" ", "%20");
}
