using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SbStudio.App.Audio;
using SbStudio.App.Gfx;
using SbStudio.Core;

namespace SbStudio.App.Ui;

/// <summary>
/// The window that sets up a video export, and the one that shows how far it has got. What is chosen is
/// kept in the user's settings and is there again next time; the time range starts out each time as what
/// the preview's progress bar runs over.
///
/// The numbers of the video are typed in freely: size, frame rate, bit rates and sample rate each have a
/// list of the usual values next to them, but take any that a video can have. One that it cannot have
/// (<see cref="VideoLimits"/>) is not taken over: the value stays what it was, and the window says why.
/// </summary>
public sealed class VideoExportDialog
{
    public const string Id = "###videoexport";
    private const string ProgressId = "###videoprogress";

    private static readonly string[] Formats = { "mp4", "mkv", "flv", "avi" };
    private static readonly int[] FrameRates = { 24, 25, 30, 50, 60, 120, 144, 240 };
    private static readonly int[] VideoKbps = { 2000, 4000, 6000, 8000, 12000, 16000, 20000, 30000, 50000 };
    private static readonly (string Id, string Name)[] Codecs = { ("h264", "H.264 (AVC)"), ("hevc", "H.265 (HEVC)") };

    /// <summary>Something that can encode: the processor, or a graphics card.</summary>
    /// <param name="Id">"cpu", or the maker of the card.</param>
    /// <param name="Encoder">Its name in FFmpeg.</param>
    /// <param name="Available">It can be started on this machine.</param>
    private sealed record EncoderChoice(string Id, string Label, string Encoder, bool Available);

    /// <summary>The audio that takes the place of the main audio, once it has been read; or why it could not be.</summary>
    /// <param name="Samples">The whole of it as stereo at <paramref name="Rate"/>.</param>
    /// <param name="Rate">The sample rate it was brought to: that of the video's sound at the time it was read.</param>
    /// <param name="Against">The main audio it was lined up with; null when there was none to go by.</param>
    /// <param name="DelayMs">How much later than time 0 it has to begin to sit where the main audio sits.</param>
    /// <param name="Match">How alike the two are once lined up, from 0 to 1.</param>
    private sealed record MusicLoad(string File, float[]? Samples, int Rate, AudioFileInfo? Info, string? Error, string? Against, double DelayMs, double Match)
    {
        /// <summary>The two are the same music, and <see cref="DelayMs"/> is where the replacement belongs.</summary>
        public bool Aligned => Against != null && Match >= GoodMatch;

        /// <summary>The delay to start out with: the one that was found when it can be trusted, none otherwise.</summary>
        public double AutoDelayMs => Aligned ? Math.Round(DelayMs, 1) : 0;
    }

    /// <summary>Below this the replacement is taken to be another recording than the main audio, and its place a guess.</summary>
    private const double GoodMatch = 0.5;

    private readonly EditorApp app;
    private readonly Func<VideoExportRequest, string?> startExport;
    private readonly Dictionary<string, List<EncoderChoice>> encoders = new();
    private string folder = "", fileName = "";
    private int start, end;
    private string? error;

    private bool replaceMusic;
    private string musicFile = "", musicProject = "";
    private Task<MusicLoad>? musicReading;
    private MusicLoad? music;
    private double musicDelayMs;
    /// <summary>The delay was set by hand and stays when the same file is read again for another sample rate.</summary>
    private bool musicKeepDelay;

    /// <summary>The text of the number fields that are being typed in, by field.</summary>
    private readonly Dictionary<string, string> typing = new();
    /// <summary>The field whose last input was not taken over, and what to tell about it.</summary>
    private (string Field, string Why)? refused;

    public VideoExportDialog(EditorApp app, Func<VideoExportRequest, string?> startExport)
    {
        this.app = app;
        this.startExport = startExport;
    }

    /// <summary>Fills the window in for the open project. The caller then opens the popup <see cref="Id"/>.</summary>
    public void Prepare()
    {
        if (app.Project is not { } project) return;
        var set = app.Settings.VideoExport;
        folder = Directory.Exists(set.Folder) ? set.Folder : Path.Combine(project.Folder, "export");
        fileName = string.IsNullOrEmpty(project.Settings.Name) ? Path.GetFileName(project.Folder) : project.Settings.Name;
        foreach (char c in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(c.ToString(), "");
        if (Array.IndexOf(Formats, set.Format) < 0) set.Format = Formats[0];
        // What the settings file has and a video cannot have, from an edit by hand: back to the usual.
        if (VideoLimits.HeightProblem(set.Height) != null) set.Height = 1080;
        if (VideoLimits.FpsProblem(set.Fps) != null) set.Fps = 60;
        if (VideoLimits.VideoBitrateProblem(set.BitrateKbps) != null) set.CustomBitrate = false;
        if (VideoLimits.SampleRateProblem(set.AudioSampleRate, false) != null) set.AudioSampleRate = AudioEngine.SampleRate;
        if (set.AudioBitrateKbps < VideoLimits.MinAudioKbps) set.AudioBitrateKbps = 320;
        if (!Array.Exists(Codecs, c => c.Id == set.Codec)) set.Codec = Codecs[0].Id;
        WholeRange();
        error = null;
        typing.Clear();
        refused = null;

        // The audio that replaces the main one belongs to the project it was chosen for.
        replaceMusic = set.ReplacementAudio.TryGetValue(project.Folder, out string? saved) && saved.Length > 0;
        if (musicProject != project.Folder)
        {
            musicProject = project.Folder;
            musicFile = saved ?? "";
            music = null;
            musicReading = null;
        }
        // Read when it has not been yet, and again when the main audio it was lined up with is another now.
        if (replaceMusic && musicReading == null && (music == null || (music.Samples != null && music.Against != app.Audio.MusicFile))) ReadMusic(musicFile);
    }

    /// <summary>
    /// Reads the audio that is to replace the main one and finds where it has to sit, away from the
    /// interface: a few minutes of lossless audio take a moment. <see cref="music"/> has it once it is done.
    /// It is read at the sample rate the video's sound has, so that it gets there in one step from what
    /// its own file has, and without any when the two are the same.
    /// </summary>
    /// <param name="keepDelay">The delay that is set stays; otherwise it becomes the one that is found.</param>
    private void ReadMusic(string file, bool keepDelay = false)
    {
        musicFile = file.Trim().Trim('"');
        music = null;
        musicReading = null;
        musicKeepDelay = keepDelay;
        if (musicFile.Length == 0) return;
        int rate = app.Settings.VideoExport.AudioSampleRate;
        if (!File.Exists(musicFile))
        {
            music = new MusicLoad(musicFile, null, rate, null, L.T("videoexport.music.missing"), null, 0, 0);
            return;
        }

        string chosen = musicFile;
        var audio = app.Audio;
        musicReading = Task.Run(() =>
        {
            float[]? samples = AudioFileDecoder.Read(chosen, rate, out var info, out string? problem);
            if (samples == null) return new MusicLoad(chosen, null, rate, info, problem ?? "", null, 0, 0);
            // Lined up with the main audio as the video would have it, so that it sits exactly where that does.
            string? against = audio.MusicFile;
            float[]? main = audio.DecodeMusic(rate);
            if (against == null || main == null) return new MusicLoad(chosen, samples, rate, info, null, null, 0, 0);
            var (lag, match) = AudioAlign.Find(OneChannel(main), OneChannel(samples), rate);
            return new MusicLoad(chosen, samples, rate, info, null, against, -lag * 1000.0 / rate, match);
        });
    }

    private static float[] OneChannel(float[] stereo)
    {
        var mono = new float[stereo.Length / 2];
        for (int i = 0; i < mono.Length; i++) mono[i] = (stereo[i * 2] + stereo[i * 2 + 1]) * 0.5f;
        return mono;
    }

    /// <summary>Takes over what <see cref="ReadMusic"/> has found, once it has.</summary>
    private void FinishReadingMusic()
    {
        if (musicReading is not { IsCompleted: true } done) return;
        musicReading = null;
        try
        {
            music = done.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Most likely a file too large to hold in memory.
            music = new MusicLoad(musicFile, null, app.Settings.VideoExport.AudioSampleRate, null, ex.Message, null, 0, 0);
        }
        // A read of a file that has been swapped for another in the meantime is of no interest.
        if (music.File != musicFile) music = null;
        else if (!musicKeepDelay) musicDelayMs = music.AutoDelayMs;
    }

    /// <summary>Remembers for the project which audio replaces its main audio, or that none does.</summary>
    private void KeepMusicChoice(Project project)
    {
        var saved = app.Settings.VideoExport.ReplacementAudio;
        if (replaceMusic && musicFile.Length > 0) saved[project.Folder] = musicFile;
        else saved.Remove(project.Folder);
    }

    private void WholeRange()
    {
        var (from, to) = app.PlayRange;
        start = (int)Math.Floor(from);
        end = (int)Math.Ceiling(to);
    }

    /// <summary>
    /// What can encode a codec on this machine: the processor, and each graphics card whose maker has an
    /// encoder for it. Starting the encoder of a card is the only way to know that it works, which takes a
    /// moment, so each codec is looked into once.
    /// </summary>
    private List<EncoderChoice> Encoders(string codec)
    {
        if (encoders.TryGetValue(codec, out var list)) return list;
        list = new List<EncoderChoice>();
        bool hevc = codec == "hevc";
        string software = hevc ? "libx265" : "libx264";
        list.Add(new EncoderChoice("cpu", L.T("videoexport.encoder.cpu", hevc ? "x265" : "x264"), software, VideoWriter.CanEncode(software)));
        foreach (var adapter in GraphicsAdapters.List())
        {
            // Of two cards of one maker only the first can be chosen: the encoder takes whichever it finds first.
            if (list.Exists(e => e.Id == adapter.Maker)) continue;
            var (suffix, technology) = adapter.Maker switch
            {
                "nvidia" => ("nvenc", "NVENC"),
                "intel" => ("qsv", "Quick Sync"),
                _ => ("amf", "AMF"),
            };
            string name = (hevc ? "hevc_" : "h264_") + suffix;
            string label = L.T(adapter.Integrated ? "videoexport.encoder.integrated" : "videoexport.encoder.discrete", adapter.Name, technology);
            list.Add(new EncoderChoice(adapter.Maker, label, name, VideoWriter.CanEncode(name)));
        }
        encoders[codec] = list;
        return list;
    }

    /// <summary>
    /// The bit rate that suits a picture: a tenth of a bit for every pixel of every frame, which is about
    /// what video sites ask for, and two thirds of that for H.265, which needs less for the same picture.
    /// </summary>
    public static int RecommendedKbps(int width, int height, int fps, bool hevc)
    {
        double kbps = width * (double)height * fps * 0.1 / 1000 * (hevc ? 0.65 : 1);
        return (int)Math.Max(1000, Math.Round(kbps / 500) * 500);
    }

    private static string Clock(double seconds)
    {
        int whole = (int)Math.Max(0, Math.Round(seconds));
        return whole >= 3600 ? $"{whole / 3600}:{whole / 60 % 60:00}:{whole % 60:00}" : $"{whole / 60:00}:{whole % 60:00}";
    }

    public void Draw(Vector2 centre)
    {
        DrawOptions(centre);
        DrawProgress(centre);
    }

    // ---------------------------------------------------------------- number fields

    /// <summary>
    /// A whole number that is typed in, with a list of the usual values to pick from at its right. What
    /// is typed is taken over when Enter is pressed or the field is left, and only when nothing is wrong
    /// with it: otherwise the value stays what it was, and <see cref="ShowRefused"/> tells why.
    /// </summary>
    /// <param name="name">What the number is, for telling that it was not changed.</param>
    /// <param name="problem">What is wrong with a value, in the user's language; null when it will do.</param>
    /// <param name="label">How a usual value reads in the list; the bare number when left out.</param>
    private bool NumberField(string id, string name, ref int value, float width, int[] usual, Func<int, string?> problem, string? tip = null, Func<int, string>? label = null)
    {
        bool changed = false;
        string text = typing.TryGetValue(id, out string? typed) ? typed : value.ToString(CultureInfo.InvariantCulture);
        ImGui.SetNextItemWidth(width - ImGui.GetFrameHeight());
        ImGui.InputText("##" + id, ref text, 12, ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.AutoSelectAll);
        if (ImGui.IsItemActive()) typing[id] = text;
        else if (typing.Remove(id)) changed = Take(id, name, text, ref value, problem);
        // Not over the line that says why the last input was not taken, which is right below.
        if (tip != null && refused?.Field != id) Theme.Tooltip(tip);

        ImGui.SameLine(0, 0);
        if (ImGui.BeginCombo("##usual" + id, "", ImGuiComboFlags.NoPreview | ImGuiComboFlags.PopupAlignLeft))
        {
            foreach (int one in usual)
            {
                // The usual values that cannot be had as things are, such as a bit rate too high for the sample rate, cannot be picked.
                bool possible = problem(one) == null;
                if (!ImGui.Selectable(label?.Invoke(one) ?? one.ToString(CultureInfo.InvariantCulture), one == value, possible ? ImGuiSelectableFlags.None : ImGuiSelectableFlags.Disabled)) continue;
                changed |= one != value;
                value = one;
                if (refused?.Field == id) refused = null;
            }
            ImGui.EndCombo();
        }
        return changed;
    }

    /// <summary>Takes over what was typed into a number field when it is a number the field can have.</summary>
    private bool Take(string id, string name, string text, ref int value, Func<int, string?> problem)
    {
        string trimmed = text.Trim();
        string? why;
        if (trimmed.Length == 0) why = L.T("input.empty");
        else if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || double.IsNaN(number) || double.IsInfinity(number))
            why = L.T("input.notNumber", trimmed);
        else if (number != Math.Round(number)) why = L.T("input.notInteger", trimmed);
        else if (Math.Abs(number) > 1e9) why = L.T("input.tooLarge");
        else
        {
            int entered = (int)number;
            why = entered == value ? null : problem(entered);
            if (why == null)
            {
                if (refused?.Field == id) refused = null;
                bool changed = entered != value;
                value = entered;
                return changed;
            }
        }
        refused = (id, L.T("videoexport.refused", name, value, why));
        return false;
    }

    /// <summary>Tells, under the fields it is called for, that the last input into one of them was not taken over, and why.</summary>
    private void ShowRefused(params ReadOnlySpan<string> fields)
    {
        if (refused is not { } last) return;
        foreach (string field in fields)
        {
            if (field != last.Field) continue;
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextError);
            Theme.TextWrapped(last.Why);
            ImGui.PopStyleColor();
            return;
        }
    }

    // ---------------------------------------------------------------- options

    private void DrawOptions(Vector2 centre)
    {
        if (!MainUi.BeginModal(L.T("dialog.videoExport.title") + Id, centre, 640)) return;
        var project = app.Project;
        if (project == null)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }
        var set = app.Settings.VideoExport;
        var style = ImGui.GetStyle();

        // ---- where to
        ImGui.SeparatorText(L.T("videoexport.output"));
        ImGui.TextUnformatted(L.T("export.folder"));
        MainUi.FolderField("videofolder", ref folder);

        ImGui.TextUnformatted(L.T("export.fileName"));
        string extension = "." + set.Format;
        ImGui.SetNextItemWidth(-ImGui.CalcTextSize(extension).X - style.ItemInnerSpacing.X);
        ImGui.InputText("##videoname", ref fileName, 200);
        ImGui.SameLine(0, style.ItemInnerSpacing.X);
        ImGui.TextUnformatted(extension);

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(L.T("videoexport.format"));
        foreach (string format in Formats)
        {
            ImGui.SameLine();
            if (ImGui.RadioButton(format.ToUpperInvariant(), set.Format == format)) set.Format = format;
        }
        // The two older kinds of file only take H.264.
        bool onlyH264 = set.Format is "flv" or "avi";
        if (onlyH264) set.Codec = "h264";

        // ---- the picture
        ImGui.SeparatorText(L.T("videoexport.picture"));
        bool wide = set.Widescreen;
        if (ImGui.Checkbox(L.T("videoexport.widescreen"), ref wide)) set.Widescreen = wide;
        Theme.Tooltip(L.T("videoexport.widescreen.tip"));

        ImGui.SameLine(0, Theme.Px(18));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(L.T("videoexport.resolution"));
        ImGui.SameLine();
        // The height is what is set; the width follows from it and from the shape of the picture.
        int height = set.Height;
        if (NumberField("height", L.T("videoexport.resolution"), ref height, Theme.Px(96), EditorApp.FrameHeights, VideoLimits.HeightProblem,
                L.T("videoexport.resolution.tip", VideoLimits.MinHeight, VideoLimits.MaxHeight), h => SizeLabel(h, set.Widescreen))) set.Height = height;
        int width = EditorApp.FrameWidth(set.Height, set.Widescreen);
        ImGui.SameLine(0, style.ItemInnerSpacing.X);
        ImGui.AlignTextToFramePadding();
        Theme.TextDimmed($"p   {width}×{set.Height}");

        ImGui.SameLine(0, Theme.Px(18));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(L.T("videoexport.fps"));
        ImGui.SameLine();
        int fps = set.Fps;
        if (NumberField("fps", L.T("videoexport.fps"), ref fps, Theme.Px(84), FrameRates, VideoLimits.FpsProblem,
                L.T("videoexport.fps.tip", VideoLimits.MinFps, VideoLimits.MaxFps))) set.Fps = fps;
        ShowRefused("height", "fps");

        // ---- the encoding
        ImGui.SeparatorText(L.T("videoexport.encoding"));
        float labelWidth = Theme.LabelWidth(80, L.T("videoexport.codec"), L.T("videoexport.encoder"));
        ImGui.PushItemWidth(-labelWidth);
        string codecName = Array.Find(Codecs, c => c.Id == set.Codec).Name ?? Codecs[0].Name;
        if (ImGui.BeginCombo(L.T("videoexport.codec"), codecName))
        {
            foreach (var (id, label) in Codecs)
            {
                bool usable = id == "h264" || !onlyH264;
                if (ImGui.Selectable(label, id == set.Codec, usable ? ImGuiSelectableFlags.None : ImGuiSelectableFlags.Disabled)) set.Codec = id;
                if (!usable && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) Theme.SetTooltip(L.T("videoexport.codec.onlyH264"));
            }
            ImGui.EndCombo();
        }

        var choices = Encoders(set.Codec);
        var chosen = choices.Find(e => e.Id == set.Encoder && e.Available) ?? choices.Find(e => e.Available);
        if (ImGui.BeginCombo(L.T("videoexport.encoder"), chosen?.Label ?? L.T("common.none")))
        {
            foreach (var choice in choices)
            {
                if (ImGui.Selectable(choice.Label, choice == chosen, choice.Available ? ImGuiSelectableFlags.None : ImGuiSelectableFlags.Disabled)) set.Encoder = choice.Id;
                if (!choice.Available && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) Theme.SetTooltip(L.T("videoexport.encoder.unavailable"));
            }
            ImGui.EndCombo();
        }
        Theme.Tooltip(L.T("videoexport.encoder.tip"));
        ImGui.PopItemWidth();

        int recommended = Math.Min(VideoLimits.MaxVideoKbps, RecommendedKbps(width, set.Height, set.Fps, set.Codec == "hevc"));
        bool custom = set.CustomBitrate;
        if (ImGui.Checkbox(L.T("videoexport.bitrate.custom"), ref custom))
        {
            set.CustomBitrate = custom;
            // It starts from what would have been used anyway.
            if (custom && VideoLimits.VideoBitrateProblem(set.BitrateKbps) != null) set.BitrateKbps = recommended;
        }
        Theme.Tooltip(L.T("videoexport.bitrate.tip"));
        ImGui.SameLine();
        ImGui.BeginDisabled(!set.CustomBitrate);
        int kbps = set.CustomBitrate ? set.BitrateKbps : recommended;
        if (NumberField("bitrate", L.T("videoexport.bitrate.name"), ref kbps, Theme.Px(130), VideoKbps, VideoLimits.VideoBitrateProblem) && set.CustomBitrate)
            set.BitrateKbps = kbps;
        ImGui.EndDisabled();
        ImGui.SameLine(0, style.ItemInnerSpacing.X);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("kbps");
        ImGui.SameLine(0, Theme.Px(12));
        Theme.TextDimmed(L.T("videoexport.bitrate.recommended", recommended));
        ShowRefused("bitrate");

        string? soundProblem = DrawSound(set);

        // ---- the stretch of time
        ImGui.SeparatorText(L.T("videoexport.range"));
        float timeWidth = Theme.Px(150);
        TimeInput.Field(app, "##exportstart", ref start, timeWidth, 0, "%d ms", int.MinValue, int.MaxValue, L.T("videoexport.range.startName"), L.T("props.caption.start"));
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("→");
        ImGui.SameLine();
        TimeInput.Field(app, "##exportend", ref end, timeWidth, 0, "%d ms", int.MinValue, int.MaxValue, L.T("videoexport.range.endName"), L.T("props.caption.end"));
        ImGui.SameLine(0, Theme.Px(12));
        if (ImGui.Button(L.T("videoexport.range.whole"))) WholeRange();
        var (wholeStart, wholeEnd) = app.PlayRange;
        Theme.Tooltip(L.T("videoexport.range.whole.tip", Theme.FormatTime(wholeStart), Theme.FormatTime(wholeEnd)));

        int now = (int)Math.Round(app.Time);
        if (ImGui.Button(L.T("videoexport.range.startNow"))) start = now;
        ImGui.SameLine();
        if (ImGui.Button(L.T("videoexport.range.endNow"))) end = now;
        ImGui.SameLine(0, Theme.Px(12));
        ImGui.AlignTextToFramePadding();
        Theme.TextDimmed(L.T("videoexport.range.now", Theme.FormatTime(now)));

        // ---- what comes of it
        ImGui.Spacing();
        ImGui.Separator();
        bool rangeValid = end > start;
        double seconds = Math.Max(0, end - start) / 1000.0;
        int frames = Math.Max(1, (int)Math.Ceiling(seconds * set.Fps - 1e-6));
        long bytes = (long)((kbps * 1000L + (set.Audio ? set.AudioBitrateKbps * 1000L : 0)) * seconds / 8);
        if (rangeValid)
            ImGui.TextUnformatted(L.T("videoexport.summary", Theme.FormatTime(end - start), frames, width, set.Height, Theme.FormatSize(bytes)));
        else
            ImGui.TextColored(ImGui.ColorConvertU32ToFloat4(Theme.TextError), L.T("videoexport.badRange"));
        Theme.TextDimmed(L.T("videoexport.note"));
        DrawRenderPreviewSwitch();

        string name = fileName.Trim();
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c.ToString(), "");
        string path = folder.Trim().Length > 0 && name.Length > 0 ? Path.Combine(folder.Trim(), name + extension) : "";
        if (path.Length > 0 && File.Exists(path))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Rgba(235, 200, 90));
            Theme.TextWrapped(L.T("videoexport.exists", path));
            ImGui.PopStyleColor();
        }
        if (error != null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextError);
            Theme.TextWrapped(L.T("videoexport.failed", error));
            ImGui.PopStyleColor();
        }

        ImGui.Spacing();
        // Other music has to be read and in its place before it can go into a video.
        bool otherMusic = set.Audio && replaceMusic;
        bool musicReady = !otherMusic || (musicReading == null && music is { Samples: not null } && music.Rate == set.AudioSampleRate);
        ImGui.BeginDisabled(!rangeValid || path.Length == 0 || chosen == null || !musicReady || soundProblem != null);
        if (Theme.Button(L.T("videoexport.start"), 120) && chosen != null)
        {
            set.Folder = folder.Trim();
            set.Encoder = chosen.Id;
            KeepMusicChoice(project);
            app.Settings.Save();
            error = startExport(new VideoExportRequest(path, chosen.Encoder, set.Widescreen, set.Height, set.Fps, kbps * 1000L, set.Audio, start, end,
                set.AudioBitrateKbps * 1000L, set.AudioSampleRate, otherMusic ? music!.Samples : null, musicDelayMs));
            // The window that shows the progress takes over.
            if (error == null) ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (Theme.Button(L.T("common.close"), 110))
        {
            KeepMusicChoice(project);
            app.Settings.Save();
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    /// <summary>
    /// The sound of the video: whether it has any, at which bit rate and sample rate, and whether another
    /// file takes the place of the main audio in it, such as the lossless original of the beatmap's MP3.
    /// </summary>
    /// <returns>
    /// Why the sound cannot be written as it is set; null when it can. Bit rate and sample rate depend on
    /// each other and on the kind of file, so a pair that was fine stops being so when one of the three
    /// is changed: then nothing is altered behind the user's back, and nothing is exported until it fits.
    /// </returns>
    private string? DrawSound(VideoExportSettings set)
    {
        FinishReadingMusic();
        var style = ImGui.GetStyle();
        ImGui.SeparatorText(L.T("videoexport.sound"));
        bool audio = set.Audio;
        if (ImGui.Checkbox(L.T("videoexport.audio"), ref audio)) set.Audio = audio;

        ImGui.BeginDisabled(!set.Audio);
        bool mp3 = VideoWriter.Mp3Sound(set.Format);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(L.T("videoexport.audioBitrate"));
        ImGui.SameLine();
        int audioKbps = set.AudioBitrateKbps;
        if (NumberField("audiobitrate", L.T("videoexport.audioBitrate"), ref audioKbps, Theme.Px(104),
                mp3 ? VideoLimits.Mp3Kbps(set.AudioSampleRate) : VideoLimits.UsualAacKbps,
                rate => VideoLimits.AudioBitrateProblem(rate, set.AudioSampleRate, mp3), L.T("videoexport.audioBitrate.tip"), rate => $"{rate} kbps"))
            set.AudioBitrateKbps = audioKbps;
        ImGui.SameLine(0, style.ItemInnerSpacing.X);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("kbps");

        ImGui.SameLine(0, Theme.Px(18));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(L.T("videoexport.sampleRate"));
        ImGui.SameLine();
        int sampleRate = set.AudioSampleRate;
        if (NumberField("samplerate", L.T("videoexport.sampleRate"), ref sampleRate, Theme.Px(112), VideoLimits.SampleRates(mp3),
                rate => VideoLimits.SampleRateProblem(rate, mp3), L.T("videoexport.sampleRate.tip"), rate => $"{rate} Hz"))
            set.AudioSampleRate = sampleRate;
        ImGui.SameLine(0, style.ItemInnerSpacing.X);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Hz");
        ShowRefused("audiobitrate", "samplerate");

        string? problem = !set.Audio ? null
            : VideoLimits.SampleRateProblem(set.AudioSampleRate, mp3) ?? VideoLimits.AudioBitrateProblem(set.AudioBitrateKbps, set.AudioSampleRate, mp3);
        if (problem != null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextError);
            Theme.TextWrapped(L.T("videoexport.invalid.sound", problem));
            ImGui.PopStyleColor();
        }

        // The other music goes into the file at the sample rate of the video's sound: when that is another
        // now, it is read again from its file. A delay that was set by hand stays.
        if (replaceMusic && set.Audio && problem == null && musicReading == null && music is { Samples: not null } && music.Rate != set.AudioSampleRate)
            ReadMusic(musicFile, !music.Aligned || Math.Abs(musicDelayMs - music.AutoDelayMs) > 0.049);

        if (ImGui.Checkbox(L.T("videoexport.music.replace"), ref replaceMusic) && replaceMusic && music == null && musicReading == null) ReadMusic(musicFile);
        Theme.Tooltip(L.T("videoexport.music.replace.tip"));
        if (replaceMusic)
        {
            string browse = L.T("common.browse");
            float button = Theme.ButtonWidth(browse, 70);
            ImGui.SetNextItemWidth(-button - style.ItemSpacing.X);
            string typed = musicFile;
            ImGui.InputTextWithHint("##musicfile", L.T("videoexport.music.hint"), ref typed, 512);
            // Read once the name has been typed to its end, not at every letter of it.
            if (ImGui.IsItemDeactivatedAfterEdit()) ReadMusic(typed);
            else if (ImGui.IsItemActive()) musicFile = typed;
            ImGui.SameLine();
            if (ImGui.Button(browse + "##music", new Vector2(button, 0)))
            {
                string? from = File.Exists(musicFile) ? Path.GetDirectoryName(musicFile) : null;
                var result = NativeFileDialogSharp.Dialog.FileOpen("flac,wav,ape,m4a,aiff,aif,wv,tta,tak,mka,ogg,opus,mp3,aac,wma", from);
                if (result.IsOk && !string.IsNullOrEmpty(result.Path)) ReadMusic(result.Path);
            }
            DrawMusicState();
        }
        ImGui.EndDisabled();
        return problem;
    }

    /// <summary>What has become of the audio that was chosen: still being read, not readable, or read and lined up.</summary>
    private void DrawMusicState()
    {
        if (musicReading != null)
        {
            Theme.TextDimmed(L.T("videoexport.music.reading"));
            return;
        }
        if (music == null) return;
        if (music.Samples == null || music.Info == null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextError);
            Theme.TextWrapped(L.T("videoexport.music.failed", music.Error ?? ""));
            ImGui.PopStyleColor();
            return;
        }

        var info = music.Info;
        // The many ways of storing plain samples all go by one name.
        string kind = (info.Codec.StartsWith("pcm_", StringComparison.Ordinal) ? "PCM" : info.Codec.ToUpperInvariant()) +
                      (info.Lossless ? " " + L.T("videoexport.music.lossless") : "");
        Theme.TextDimmed(L.T("videoexport.music.info", kind, info.SampleRate, info.Channels,
            Theme.FormatTime(music.Samples.Length / 2 * 1000.0 / music.Rate)));

        // Where it sits: found by comparing it with the main audio, and open to being set by hand.
        NumberInput.Field(app, "##musicdelay", ref musicDelayMs, Theme.Px(170),
            new NumberSpec(L.T("videoexport.music.delayName"), 0.5f, "%.1f ms", -3_600_000, 3_600_000, Suffix: "ms", Caption: L.T("videoexport.music.delay")));
        Theme.Tooltip(L.T("videoexport.music.delay.tip"));
        if (music.Aligned && Math.Abs(musicDelayMs - music.AutoDelayMs) > 0.049)
        {
            ImGui.SameLine();
            if (ImGui.Button(L.T("videoexport.music.auto"))) musicDelayMs = music.AutoDelayMs;
        }
        ImGui.SameLine(0, Theme.Px(12));
        ImGui.AlignTextToFramePadding();
        if (music.Against == null) Theme.TextDimmed(L.T("videoexport.music.noMain"));
        else if (music.Aligned)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Rgba(110, 210, 130));
            ImGui.TextUnformatted(L.T("videoexport.music.aligned", music.Match * 100));
            ImGui.PopStyleColor();
        }
        else
        {
            ImGui.NewLine();
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Rgba(235, 200, 90));
            Theme.TextWrapped(L.T("videoexport.music.alignedPoor", music.Match * 100));
            ImGui.PopStyleColor();
        }
    }

    private static string SizeLabel(int height, bool widescreen) => $"{height}p   {EditorApp.FrameWidth(height, widescreen)}×{height}";

    // ---------------------------------------------------------------- progress

    /// <summary>Shown for as long as there is an export: while it runs, and afterwards until what became of it has been read.</summary>
    private void DrawProgress(Vector2 centre)
    {
        var job = app.VideoExport;
        if (job != null && !ImGui.IsPopupOpen(ProgressId)) ImGui.OpenPopup(ProgressId);

        ImGui.SetNextWindowPos(centre, ImGuiCond.Always, new Vector2(0.5f, 0.5f));
        // No close button: while it runs the way out is to cancel, afterwards to close.
        string title = L.T(job is { Running: false } ? "dialog.videoExport.title" : "dialog.videoProgress.title");
        if (!ImGui.BeginPopupModal(title + ProgressId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings)) return;
        if (job == null)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        // The frame that was drawn last, so that what is being made can be seen.
        float shown = Theme.Px(520);
        if (app.Settings.VideoExport.ShowRenderPreview)
            ImGui.Image(job.Picture, new Vector2(shown, MathF.Round(shown * job.Height / job.Width)), new Vector2(0, 1), new Vector2(1, 0));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + shown);

        float fraction = (float)job.Frame / job.FrameCount;
        if (job.Running)
        {
            ImGui.ProgressBar(fraction, new Vector2(shown, 0), $"{fraction * 100:0}%");
            double rate = job.Seconds > 0.2 ? job.Frame / job.Seconds : 0;
            string left = rate > 0 ? Clock((job.FrameCount - job.Frame) / rate) : "--:--";
            ImGui.TextUnformatted(job.Frame >= job.FrameCount
                ? L.T("videoexport.finishing")
                : L.T("videoexport.progress", job.Frame, job.FrameCount, rate, left));
            Theme.TextDimmed(job.Request.Path);
            ImGui.Spacing();
            if (Theme.Button(L.T("common.cancel"), 110)) app.ClearVideoExport();
        }
        else if (job.Error != null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextError);
            ImGui.TextUnformatted(L.T("videoexport.failed", job.Error));
            ImGui.PopStyleColor();
            ImGui.Spacing();
            if (Theme.Button(L.T("common.close"), 110)) app.ClearVideoExport();
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Rgba(110, 210, 130));
            ImGui.TextUnformatted(L.T("videoexport.done", job.Request.Path));
            ImGui.PopStyleColor();
            ImGui.TextUnformatted(L.T("videoexport.doneInfo", job.FrameCount, job.Width, job.Height, Theme.FormatSize(job.FileSize), Clock(job.Seconds)));
            ImGui.Spacing();
            if (ImGui.Button(L.T("export.openLocation"))) AssetsPanel.OpenInExplorer(job.Request.Path, true);
            ImGui.SameLine();
            if (Theme.Button(L.T("common.close"), 110)) app.ClearVideoExport();
        }
        ImGui.PopTextWrapPos();
        if (app.VideoExport == null) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawRenderPreviewSwitch()
    {
        bool show = app.Settings.VideoExport.ShowRenderPreview;
        if (ImGui.Checkbox(L.T("videoexport.showRenderPreview"), ref show))
        {
            app.Settings.VideoExport.ShowRenderPreview = show;
            app.Settings.Save();
        }
        Theme.Tooltip(L.T("videoexport.showRenderPreview.tip"));
    }
}
