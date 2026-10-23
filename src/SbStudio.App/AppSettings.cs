using System.Text.Encodings.Web;
using System.Text.Json;

namespace SbStudio.App;

public enum TimingSlowPlaybackStyle { Current, PlaybackClock }

/// <summary>What the video export window was last set to.</summary>
public sealed class VideoExportSettings
{
    /// <summary>Show the last rendered frame in the progress dialog; this never changes the output.</summary>
    public bool ShowRenderPreview { get; set; } = true;
    /// <summary>A 16:9 picture, which shows what a widescreen storyboard shows; otherwise 4:3.</summary>
    public bool Widescreen { get; set; } = true;
    /// <summary>Height of the picture in pixels: any that <see cref="SbStudio.Core.VideoLimits"/> lets through.</summary>
    public int Height { get; set; } = 1080;
    /// <summary>Frames per second, a whole number.</summary>
    public int Fps { get; set; } = 60;
    /// <summary>The bit rate is <see cref="BitrateKbps"/>; otherwise the one that suits the size and frame rate.</summary>
    public bool CustomBitrate { get; set; }
    /// <summary>Kilobits per second of the picture.</summary>
    public int BitrateKbps { get; set; } = 12000;
    /// <summary>"h264" or "hevc".</summary>
    public string Codec { get; set; } = "h264";
    /// <summary>What encodes: "cpu", or the graphics card of a maker: "nvidia", "intel" or "amd".</summary>
    public string Encoder { get; set; } = "cpu";
    /// <summary>The kind of file: "mp4", "flv", "avi" or "mkv".</summary>
    public string Format { get; set; } = "mp4";
    /// <summary>The music and the storyboard's samples go into the file.</summary>
    public bool Audio { get; set; } = true;
    /// <summary>Kilobits per second of the sound.</summary>
    public int AudioBitrateKbps { get; set; } = 320;
    /// <summary>Samples per second of the sound.</summary>
    public int AudioSampleRate { get; set; } = 44100;
    /// <summary>
    /// For the projects whose main audio is replaced in the video, by their folder: the audio file that
    /// takes its place, such as the lossless original of the beatmap's MP3.
    /// </summary>
    public Dictionary<string, string> ReplacementAudio { get; set; } = new();
    public string Folder { get; set; } = "";
}

public enum TimelineWaveformStyle { Classic, Green }

public enum GroupOutlineScope { Group, Members }

/// <summary>Per-user settings kept outside of any project.</summary>
public sealed class AppSettings : System.Text.Json.Serialization.IJsonOnDeserialized
{
    public const int MinHistoryLimit = 100, MaxHistoryLimit = 10000;
    private int historyLimit = MinHistoryLimit;
    /// <summary>Maximum undo/redo steps retained in memory, independent of any project.</summary>
    public int HistoryLimit
    {
        get => historyLimit;
        set => historyLimit = Math.Clamp(value, MinHistoryLimit, MaxHistoryLimit);
    }

    public List<string> RecentProjects { get; set; } = new();
    public int EffectWorkerCount { get; set; } = 2;
    public int EffectTimeoutSeconds { get; set; } = 30;
    public float UiScale { get; set; } = 1f;
    public float Volume { get; set; } = 0.7f;
    public float HitSoundVolume { get; set; } = 0.7f;
    public float MusicVolume { get; set; } = 1f;
    /// <summary>Stable device identifier; empty follows the system default.</summary>
    public string AudioOutputDevice { get; set; } = "";
    public string ProjectsFolder { get; set; } = "";
    public string LastImportFolder { get; set; } = "";
    /// <summary>Where the last screenshot was saved.</summary>
    public string ScreenshotFolder { get; set; } = "";
    /// <summary>Screenshots are saved as JPG; otherwise as PNG.</summary>
    public bool ScreenshotJpg { get; set; }
    /// <summary>Quality of screenshots saved as JPG, from 1 to 100.</summary>
    public int JpegQuality { get; set; } = 80;
    public bool RotationInDegrees { get; set; } = true;
    /// <summary>
    /// The properties panel shows only what is picked in the timeline: the keyframes of a property, a loop
    /// or trigger, or the keyframes of a property of one. Off, it always shows the whole object.
    /// </summary>
    public bool FilterCommands { get; set; } = true;
    /// <summary>
    /// What is dragged, placed or sought in the timeline lands on the ticks of the snap grid. Off, any
    /// millisecond will do: the way to adjust times that are not on the grid, as an imported storyboard
    /// may have them.
    /// </summary>
    public bool Snap { get; set; } = true;
    /// <summary>Play the beatmap's video in the preview.</summary>
    public bool ShowVideo { get; set; } = true;
    /// <summary>Draw the hit objects of the difficulty that is shown in the preview.</summary>
    public bool ShowHitObjects { get; set; }
    /// <summary>Show preview performance figures and cursor coordinates.</summary>
    public bool ShowDebugInfo { get; set; }
    private GroupOutlineScope outlineScope;
    /// <summary>Outline a selected group as one combined rectangle, or outline each of its members.</summary>
    public GroupOutlineScope OutlineScope
    {
        get => outlineScope;
        set => outlineScope = Enum.IsDefined(value) ? value : GroupOutlineScope.Group;
    }
    /// <summary>Allow selecting and editing objects in the full-screen preview.</summary>
    public bool FullscreenEditing { get; set; }
    /// <summary>Show the full-screen preview inspector.</summary>
    public bool FullscreenDebugPanel { get; set; }
    /// <summary>Show read-only beatmap hit-sound lanes in the timeline.</summary>
    public bool ShowHitSoundRecheck { get; set; }
    /// <summary>Show the read-only metronome lane; mutually exclusive with hitsound recheck.</summary>
    public bool ShowTimingRecheck { get; set; }
    /// <summary>Flash Timing beat markers and the lane label during playback and audition.</summary>
    public bool TimingBrightnessFeedback { get; set; } = true;
    /// <summary>Play Timing metronome clicks during preview and marker audition.</summary>
    public bool TimingAssistSound { get; set; } = true;
    /// <summary>Stretch Timing check clicks together with the music; off keeps each click at its original speed.</summary>
    public bool TimingSoundFollowsTempo { get; set; }
    private TimingSlowPlaybackStyle timingSlowPlayback = TimingSlowPlaybackStyle.PlaybackClock;
    /// <summary>Internal playback method for Timing check below 1x.</summary>
    public TimingSlowPlaybackStyle TimingSlowPlayback
    {
        get => timingSlowPlayback;
        set => timingSlowPlayback = Enum.IsDefined(value) ? value : TimingSlowPlaybackStyle.PlaybackClock;
    }

    void System.Text.Json.Serialization.IJsonOnDeserialized.OnDeserialized()
    {
        // Hidden playback preferences use the current defaults even in settings saved by older versions.
        TimingSlowPlayback = TimingSlowPlaybackStyle.PlaybackClock;
        TimingSoundFollowsTempo = false;
    }
    /// <summary>While checking Timing, the metronome also clicks on every snap line of the current divisor.</summary>
    public bool TimingSnapSound { get; set; }
    /// <summary>Preview an image when hovering its object name in the timeline.</summary>
    public bool ShowTimelineThumbnails { get; set; }
    private TimelineWaveformStyle waveformStyle;
    /// <summary>Timeline audio appearance; old settings retain the original blue waveform.</summary>
    public TimelineWaveformStyle WaveformStyle
    {
        get => waveformStyle;
        set => waveformStyle = Enum.IsDefined(value) ? value : TimelineWaveformStyle.Classic;
    }
    /// <summary>Fit newly added images to the current storyboard frame.</summary>
    public bool AutoScaleAddedImages { get; set; } = true;
    /// <summary>Seek to newly copied osu! editor timestamps, including while another window is active.</summary>
    public bool TimelineAutoJump { get; set; } = true;
    /// <summary>Double-clicking a .sbproj file opens it here (a per-user Windows file type registration).</summary>
    public bool OpenProjectFiles { get; set; } = true;
    /// <summary>Milliseconds added to the offsets the BPM analysis finds (positive = later), to the user's preference.</summary>
    public double TimingAnalysisCalibrationMs { get; set; }
    /// <summary>Replace preview sound schedules without restarting the music stream.</summary>
    public bool SeamlessPlaybackRefresh { get; set; } = true;
    /// <summary>Preview sound offset in real milliseconds: positive delays, negative advances. Excludes music and export.</summary>
    public int SoundOffsetMs { get; set; }
    /// <summary>Record preview gestures as keyframes while music is playing.</summary>
    public bool FreeMode { get; set; } = true;
    /// <summary>Flash timeline hit-sound markers and lane labels during playback.</summary>
    public bool HitSoundBrightnessFeedback { get; set; } = true;
    /// <summary>Keep the timeline playhead fixed while the timeline follows the current time.</summary>
    public bool OsuStyleTimelineIndicator { get; set; }
    /// <summary>Built-in stable/lazer style, or a custom skin from the Skins directory.</summary>
    public string PreviewSkin { get; set; } = "stable";
    public string PreviewBeatmapSkinFallback { get; set; } = "lazer";
    public VideoExportSettings VideoExport { get; set; } = new();
    /// <summary>Interface language, such as "zh-Hans" or "en". Empty follows the system.</summary>
    public string Language { get; set; } = "";
    /// <summary>Draw in step with the screen. Off draws as fast as possible.</summary>
    public bool VSync { get; set; } = true;
    public bool CheckUpdatesOnStartup { get; set; } = true;
    public DateTime LastUpdateCheckUtc { get; set; }
    /// <summary>The assets panel lists names in rows; otherwise it shows icons.</summary>
    public bool AssetList { get; set; }
    /// <summary>Size of the icons in the assets panel, before the interface scale.</summary>
    public int AssetIconSize { get; set; } = 64;

    /// <summary>The assets panel lists only the files whose extension is one of <see cref="AssetExtensions"/>.</summary>
    public bool AssetUsableOnly { get; set; } = true;
    /// <summary>The extensions of the files the assets panel takes for usable, each in lower case behind its dot.</summary>
    public List<string> AssetExtensions { get; set; } = new(DefaultAssetExtensions);

    public const int MinAssetIconSize = 48, MaxAssetIconSize = 192;

    public static readonly string[] DefaultAssetExtensions =
        { ".jpg", ".jpeg", ".png", ".bmp", ".mp4", ".avi", ".m4v", ".wmv", ".flv", ".wav", ".ogg", ".mp3", ".mpg" };

    /// <summary>No settings file existed yet.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool FirstRun { get; private set; }

    /// <summary>When set, nothing is written to disk (used by screenshot runs).</summary>
    public static bool ReadOnly;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Folder
    {
        get
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OsuStoryboardStudio");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string FilePath => Path.Combine(Folder, "settings.json");
    public static string LayoutFile => Path.Combine(Folder, "layout.ini");
    public static string LogFile => Path.Combine(Folder, "log.txt");

    public static AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception)
        {
            settings = new AppSettings();
        }
        settings.FirstRun = !File.Exists(FilePath);
        if (string.IsNullOrWhiteSpace(settings.PreviewSkin)) settings.PreviewSkin = "stable";
        if (string.IsNullOrWhiteSpace(settings.PreviewBeatmapSkinFallback) || settings.PreviewBeatmapSkinFallback == "beatmap") settings.PreviewBeatmapSkinFallback = "lazer";

        if (string.IsNullOrWhiteSpace(settings.ProjectsFolder))
            settings.ProjectsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OsuStoryboardStudio");
        settings.UiScale = Math.Clamp(settings.UiScale, 0.75f, 2.5f);
        settings.JpegQuality = Math.Clamp(settings.JpegQuality, 1, 100);
        settings.VideoExport ??= new VideoExportSettings();
        settings.VideoExport.ReplacementAudio ??= new Dictionary<string, string>();
        settings.AssetIconSize = Math.Clamp(settings.AssetIconSize, MinAssetIconSize, MaxAssetIconSize);
        settings.AssetExtensions = (settings.AssetExtensions ?? new List<string>()).Select(NormalizeExtension).OfType<string>().Distinct().ToList();
        return settings;
    }

    /// <summary>
    /// An extension the way it is kept: in lower case behind one dot, so that "PNG", ".png" and "*.png" all
    /// give ".png". Null for what is not the extension of a file.
    /// </summary>
    public static string? NormalizeExtension(string? text)
    {
        string ext = (text ?? "").Trim().TrimStart('*').TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0 || ext.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-' && c != '+')) return null;
        return "." + ext;
    }

    public void Save()
    {
        if (ReadOnly) return;
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (IOException)
        {
            // Settings are a convenience; a locked file must not take the editor down.
        }
    }

    public void AddRecent(string projectFolder)
    {
        RecentProjects.RemoveAll(p => string.Equals(p, projectFolder, StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, projectFolder);
        Save();
    }

    public static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
