using System.Globalization;

namespace SbStudio.Core;

/// <summary>The parts of a .osu beatmap file that matter to a storyboard project.</summary>
public sealed class OsuFile
{
    public string AudioFilename = "";
    public bool Widescreen;
    public int Mode;
    public string Artist = "", Title = "", Creator = "", Version = "";
    public string? Background;
    public List<TimingPoint> Timing = new();

    public static OsuFile Read(string path) => Parse(File.ReadAllText(path));

    public static OsuFile Parse(string text)
    {
        var osu = new OsuFile();
        string section = "";
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                section = line.Substring(1, line.Length - 2);
                continue;
            }

            switch (section)
            {
                case "General":
                case "Metadata":
                {
                    int colon = line.IndexOf(':');
                    if (colon <= 0) break;
                    string key = line.Substring(0, colon).Trim();
                    string value = line.Substring(colon + 1).Trim();
                    switch (key)
                    {
                        case "AudioFilename": osu.AudioFilename = OsbParser.CleanPath(value); break;
                        case "WidescreenStoryboard": osu.Widescreen = value == "1"; break;
                        case "Mode": int.TryParse(value, out osu.Mode); break;
                        case "Artist": osu.Artist = value; break;
                        case "Title": osu.Title = value; break;
                        case "Creator": osu.Creator = value; break;
                        case "Version": osu.Version = value; break;
                    }
                    break;
                }

                case "Events":
                {
                    // Background: 0,0,"file",x,y. A line that names a video is one, whatever it calls itself:
                    // that is a storyboard object (see OsbParser), not the background.
                    if (osu.Background == null && BackgroundOrVideo(line) is { } file && Project.KindOf(file) == AssetKind.Image)
                        osu.Background = file;
                    break;
                }

                case "TimingPoints":
                {
                    var parts = line.Split(',');
                    if (parts.Length < 2) break;
                    if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double offset)) break;
                    if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double beatLength)) break;
                    bool uninherited = parts.Length > 6 ? parts[6].Trim() == "1" : beatLength > 0;
                    if (!uninherited || beatLength <= 0) break;
                    int meter = parts.Length > 2 && int.TryParse(parts[2], out int m) && m > 0 ? m : 4;
                    osu.Timing.Add(new TimingPoint { Offset = offset, BeatLength = beatLength, Meter = meter });
                    break;
                }
            }
        }
        osu.Timing.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        return osu;
    }

    /// <summary>The file an event line names when it is a background or a video line; null for any other line.</summary>
    private static string? BackgroundOrVideo(string line)
    {
        var parts = line.Split(',');
        if (parts.Length < 3) return null;
        string type = parts[0].Trim();
        bool either = type == "0" || type == "1" || type.Equals("Background", StringComparison.OrdinalIgnoreCase) ||
                      type.Equals("Video", StringComparison.OrdinalIgnoreCase);
        return either ? OsbParser.CleanPath(parts[2]) : null;
    }

    /// <summary>
    /// The text of a .osu file with the storyboard and the videos of its [Events] section replaced.
    /// Everything else stays as it is: the other sections, and the background, break and colour lines
    /// among the events.
    /// </summary>
    /// <param name="storyboard">The new storyboard lines, as <see cref="OsbWriter.WriteLayers(Storyboard, OsbWriteOptions?)"/> writes them.</param>
    /// <param name="videos">The new video lines, as <see cref="OsbWriter.WriteVideos(Storyboard, OsbWriteOptions?)"/> writes them.</param>
    public static string ReplaceStoryboard(string text, string storyboard, string videos = "")
    {
        string newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var output = new List<string>();
        bool inEvents = false, sawEvents = false, inObject = false;
        // Where the new lines go: where the old storyboard began, or else ahead of the colour lines that
        // osu! writes after it, or else at the end of the section.
        int insertAt = -1;
        // Where the videos go: where the old ones were, or else after the background, which is ahead of
        // the breaks, or else at the head of the section.
        int videosAt = -1, afterBackground = -1, beforeBreaks = -1, sectionStart = -1;

        void EndOfEvents()
        {
            if (!inEvents || insertAt >= 0) return;
            insertAt = output.Count;
            while (insertAt > 0 && output[insertAt - 1].Trim().Length == 0) insertAt--;
        }

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            string trimmed = line.Trim();
            if (trimmed.Length > 1 && trimmed[0] == '[' && trimmed[^1] == ']')
            {
                EndOfEvents();
                inEvents = trimmed.Equals("[Events]", StringComparison.OrdinalIgnoreCase);
                sawEvents |= inEvents;
                inObject = false;
                output.Add(line);
                if (inEvents && sectionStart < 0) sectionStart = output.Count;
                continue;
            }
            if (!inEvents || trimmed.Length == 0)
            {
                output.Add(line);
                continue;
            }

            // The commands of an object are the indented lines under it.
            if (line[0] == ' ' || line[0] == '_')
            {
                if (!inObject) output.Add(line);
                continue;
            }
            inObject = false;

            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                bool heading = trimmed.StartsWith("//Storyboard Layer", StringComparison.OrdinalIgnoreCase) ||
                               trimmed.StartsWith("//Storyboard Sound Samples", StringComparison.OrdinalIgnoreCase);
                if (heading || trimmed.StartsWith("//Background Colour Transformations", StringComparison.OrdinalIgnoreCase))
                {
                    if (insertAt < 0) insertAt = output.Count;
                }
                if (beforeBreaks < 0 && trimmed.StartsWith("//Break Periods", StringComparison.OrdinalIgnoreCase)) beforeBreaks = output.Count;
                if (!heading) output.Add(line);
                // The heading osu! writes over the background and the videos.
                if (trimmed.StartsWith("//Background and Video events", StringComparison.OrdinalIgnoreCase)) sectionStart = output.Count;
                continue;
            }

            string kind = trimmed.Split(',')[0].Trim().ToLowerInvariant();
            if (kind is "sprite" or "animation" or "sample" or "4" or "5" or "6")
            {
                if (insertAt < 0) insertAt = output.Count;
                inObject = true;
                continue;
            }
            if (BackgroundOrVideo(trimmed) is { } file)
            {
                if (Project.KindOf(file) == AssetKind.Video)
                {
                    // An old video line: the new ones take its place.
                    if (videosAt < 0) videosAt = output.Count;
                    inObject = true;
                    continue;
                }
                output.Add(line);
                afterBackground = output.Count;
                continue;
            }
            output.Add(line);
        }
        EndOfEvents();
        if (videosAt < 0) videosAt = afterBackground >= 0 ? afterBackground : beforeBreaks >= 0 ? beforeBreaks : sectionStart;

        static List<string> Lines(string script)
        {
            var lines = script.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return lines;
        }

        var added = Lines(storyboard);
        var addedVideos = Lines(videos);
        if (!sawEvents)
        {
            // A file without events gets the section at its end.
            while (output.Count > 0 && output[^1].Trim().Length == 0) output.RemoveAt(output.Count - 1);
            output.Add("");
            output.Add("[Events]");
            output.AddRange(addedVideos);
            output.AddRange(added);
            output.Add("");
        }
        else
        {
            // The later place first, so that the earlier one stays where it was counted to be. In the same
            // place the videos come first.
            if (videosAt > insertAt)
            {
                output.InsertRange(videosAt, addedVideos);
                output.InsertRange(insertAt, added);
            }
            else
            {
                output.InsertRange(insertAt, added);
                output.InsertRange(videosAt, addedVideos);
            }
        }
        return string.Join(newline, output);
    }

    /// <summary>
    /// The file name osu! looks for the shared storyboard of this beatmap under: artist, title and mapper,
    /// without the difficulty. A beatmap that names no artist goes by its audio file instead, and one that
    /// names no mapper has none in the name.
    /// </summary>
    public string OsbFileName()
    {
        string name = (Artist.Length > 0 ? Artist + " - " + Title : Path.GetFileNameWithoutExtension(AudioFilename)) +
                      (Creator.Length > 0 ? " (" + Creator + ")" : "") + ".osb";
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c.ToString(), "");
        return name;
    }
}
