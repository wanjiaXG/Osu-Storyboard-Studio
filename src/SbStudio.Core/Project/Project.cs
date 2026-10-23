using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SbStudio.Core;

public enum ExportAssetMode
{
    None,
    Used,
    All,
}

public sealed class ExportSettings
{
    public string FileName { get; set; } = "";
    public string Folder { get; set; } = "";
    public ExportAssetMode Assets { get; set; } = ExportAssetMode.Used;
    public bool IncludeAudioAndBackground { get; set; }
    public bool Zip { get; set; }
    /// <summary>Also write the difficulties' .osu files, each with its own part of the storyboard.</summary>
    public bool Difficulties { get; set; } = true;
}

/// <summary>
/// One difficulty of the beatmap set: a .osu file. It may bring storyboard objects of its own, shown with
/// that difficulty only, and has its own audio, background, picture format and timing.
/// </summary>
public sealed class DifficultySettings
{
    /// <summary>Name of the .osu file. The difficulty's objects are tagged with it.</summary>
    public string File { get; set; } = "";
    /// <summary>The difficulty name the .osu gives itself ("Version").</summary>
    public string Name { get; set; } = "";
    public string Audio { get; set; } = "";
    public string Background { get; set; } = "";
    public bool Widescreen { get; set; }
    public List<TimingPoint> Timing { get; set; } = new();

    /// <summary>Last external file and storyboard read, so project edits survive refresh and reopening.</summary>
    public string SourceFileHash { get; set; } = "";
    public string SourceStoryboardHash { get; set; } = "";

    [JsonIgnore]
    public string DisplayName => Name.Length > 0 ? Name : Path.GetFileNameWithoutExtension(File);
}

public sealed class ProjectSettings
{
    /// <summary>Original parsing errors/warnings, including skipped commands, travel with the project.</summary>
    public List<SourceDiagnostic> SourceDiagnostics { get; set; } = new();
    public bool SourceDiagnosticsInitialized { get; set; }
    /// <summary>
    /// Version 3 stores videos as storyboard objects; version 4 references a live beatmap directory.
    /// </summary>
    public int Version { get; set; } = CurrentVersion;
    public const int CurrentVersion = 4;
    /// <summary>Referenced beatmap directory, absolute or relative to the project. Empty means unlinked.</summary>
    public string BeatmapFolder { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Main audio file, relative to the assets folder.</summary>
    public string Audio { get; set; } = "";
    /// <summary>Beatmap background, relative to the assets folder.</summary>
    public string Background { get; set; } = "";
    public bool Widescreen { get; set; } = true;
    public List<TimingPoint> Timing { get; set; } = new();
    public int BeatDivisor { get; set; } = 4;
    public double EditorTime { get; set; }
    public bool TriggerSimulationEnabled { get; set; } = true;
    public List<TriggerSimulationRule> TriggerSimulationRules { get; set; } = new();
    /// <summary>
    /// Legacy project-wide precision, read once into groups without precision metadata.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? GroupStep { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? GroupTolerance { get; set; }
    public ExportSettings Export { get; set; } = new();
    /// <summary>The difficulties of the beatmap set the project was imported from. Empty for a project of its own.</summary>
    public List<DifficultySettings> Difficulties { get; set; } = new();
    /// <summary>
    /// File name of the selected difficulty, the one that is shown. <see cref="Audio"/>, <see cref="Background"/>,
    /// <see cref="Widescreen"/> and <see cref="Timing"/> are its values while it is selected. Empty when
    /// none is: then only the storyboard all difficulties share is shown.
    /// </summary>
    public string Difficulty { get; set; } = "";
    /// <summary>
    /// What the audio, background, picture format and timing were while no difficulty was selected, in a
    /// project that has difficulties. Null until that view has been left once.
    /// </summary>
    public DifficultySettings? Shared { get; set; }
}

public enum AssetKind
{
    Image,
    Audio,
    Video,
    Other,
}

public sealed record AssetInfo(string Path, AssetKind Kind, long Size);

/// <summary>
/// A project is a folder:
///   project.sbproj   settings (JSON)
///   storyboard.osb   the storyboard, in osb format plus "//@" editor lines
///   assets/          images, audio and video, laid out exactly like the beatmap folder
///   .osu files are read directly from the directory named in project.sbproj
/// </summary>
public sealed partial class Project
{
    public const string ProjectFileName = "project.sbproj";
    public const string StoryboardFileName = "storyboard.osb";
    public const string AssetsFolderName = "assets";
    public const string DifficultiesFolderName = "difficulties";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Folder { get; }
    public ProjectSettings Settings { get; private set; } = new();
    public Storyboard Storyboard { get; set; } = new();
    public List<Diagnostic> LoadDiagnostics { get; private set; } = new();

    public string ProjectFile => Path.Combine(Folder, ProjectFileName);
    public string StoryboardFile => Path.Combine(Folder, StoryboardFileName);
    public string AssetsDir => Path.Combine(Folder, AssetsFolderName);
    public string DifficultiesDir => Path.Combine(Folder, DifficultiesFolderName);

    /// <summary>A difficulty in the referenced directory. Unlinking does not fall back to old copies.</summary>
    public string DifficultyFile(string file) => BeatmapDirectory is { } directory ? Path.Combine(directory, Path.GetFileName(file)) : "";

    public DifficultySettings? CurrentDifficulty => Difficulties.FirstOrDefault(d => d.File == Settings.Difficulty);

    private string[] DifficultyFiles() => Settings.Difficulties.Select(d => d.File).ToArray();

    /// <summary>Sorts the storyboard's objects for the selected difficulty; see <see cref="Storyboard.Arrange"/>.</summary>
    public bool ArrangeStoryboard()
    {
        NormalizeBackgroundPlaceholders(Storyboard, IsBeatmapBackground);
        return Storyboard.Arrange(Settings.Difficulty, DifficultyFiles(), keepMissing: true);
    }

    /// <summary>
    /// Writes the audio, background, picture format and timing in use back to where they belong: the
    /// selected difficulty, or the view of the shared storyboard alone. A project without difficulties has
    /// one set of them and nothing to keep apart.
    /// </summary>
    private void KeepDifficultySettings()
    {
        var current = CurrentDifficulty;
        if (current == null && Settings.Difficulties.Count > 0) current = Settings.Shared ??= new DifficultySettings();
        if (current == null) return;
        current.Audio = Settings.Audio;
        current.Background = Settings.Background;
        current.Widescreen = Settings.Widescreen;
        current.Timing = CopyTiming(Settings.Timing);
    }

    private static List<TimingPoint> CopyTiming(List<TimingPoint> timing) =>
        timing.Select(t => new TimingPoint { Offset = t.Offset, BeatLength = t.BeatLength, Meter = t.Meter }).ToList();

    /// <summary>
    /// Selects a difficulty without touching the storyboard: its audio, background, picture format and
    /// timing become the ones in use. For when the objects are put in place by other means, such as undo.
    /// </summary>
    /// <param name="file">The difficulty's .osu file, or empty for none: the shared storyboard alone.</param>
    /// <returns>False when the project has no such difficulty.</returns>
    public bool UseDifficulty(string file)
    {
        var next = file.Length == 0 ? Settings.Shared : Difficulties.FirstOrDefault(d => d.File == file);
        if (next == null && file.Length > 0) return false;
        KeepDifficultySettings();
        Settings.Difficulty = file;
        // The shared storyboard alone, shown for the first time, goes on with what was in use.
        if (next == null) return true;
        Use(next);
        return true;
    }

    /// <summary>Makes what a difficulty brings the audio, background, picture format and timing in use.</summary>
    public void Use(DifficultySettings difficulty)
    {
        Settings.Audio = difficulty.Audio;
        Settings.Background = difficulty.Background;
        Settings.Widescreen = difficulty.Widescreen;
        Settings.Timing = CopyTiming(difficulty.Timing);
    }

    /// <summary>
    /// Selects the difficulty to show: its own storyboard objects join the shared ones, those of the
    /// difficulty shown so far are parked.
    /// </summary>
    public bool SelectDifficulty(string file)
    {
        if (!UseDifficulty(file)) return false;
        ArrangeStoryboard();
        return true;
    }

    private Project(string folder) => Folder = Path.GetFullPath(folder);
    /// <summary>Freezes document and settings for an export without touching files or the live editor.</summary>
    public Project ExportSnapshot()
    {
        var copy=Storyboard.CloneContent();
        return new Project(Folder) {Settings=JsonSerializer.Deserialize<ProjectSettings>(JsonSerializer.Serialize(Settings,JsonOptions),JsonOptions)!,
            Storyboard=new Storyboard {Objects=copy.Objects,Parked=copy.Parked,Groups=copy.Groups,Variables=Storyboard.Variables.ToList()}};
    }

    public static Project Create(string folder, string name)
    {
        var project = new Project(folder);
        if (File.Exists(project.ProjectFile))
            throw new IOException(L.T("project.exists", project.ProjectFile));
        Directory.CreateDirectory(project.AssetsDir);
        project.Settings.Name = name;
        project.Save();
        return project;
    }

    /// <param name="path">The project folder or its project.sbproj file.</param>
    /// <param name="progress">Told from time to time how much of the storyboard has been read, from 0 to 1.</param>
    public static Project Load(string path, Action<double>? progress = null)
    {
        string folder = Directory.Exists(path) ? path : Path.GetDirectoryName(Path.GetFullPath(path))!;
        var project = new Project(folder);
        if (!File.Exists(project.ProjectFile))
            throw new FileNotFoundException(L.T("project.fileMissing", ProjectFileName), project.ProjectFile);

        project.Settings = JsonSerializer.Deserialize<ProjectSettings>(File.ReadAllText(project.ProjectFile), JsonOptions) ?? new ProjectSettings();
        project.Settings.SourceDiagnostics ??= new();
        project.NormalizeTriggerSimulation();
        project.Settings.Timing.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        if (project.CurrentDifficulty == null) project.Settings.Difficulty = "";
        Directory.CreateDirectory(project.AssetsDir);

        if (File.Exists(project.StoryboardFile))
        {
            var parsed = OsbParser.Parse(File.ReadAllText(project.StoryboardFile), progress, project.StoryboardFile);
            project.Storyboard = parsed.Storyboard;
            project.LoadDiagnostics = parsed.Diagnostics;
            project.RecordDiagnostics(parsed.Diagnostics, DiagnosticOrigin.Load);
        }
        // Old projects did not record their source directory. Keep their copied files usable as a
        // relative reference until the user chooses the original beatmap directory.
        if (project.Settings.Version < ProjectSettings.CurrentVersion)
        {
            if (project.Settings.BeatmapFolder.Length == 0 && project.Settings.Difficulties.Count > 0)
                project.Settings.BeatmapFolder = DifficultiesFolderName;
            project.ReadVideos();
            project.Settings.Version = ProjectSettings.CurrentVersion;
        }
        project.RefreshBeatmaps();
        project.MigrateGroupPrecision();
        project.RecoverSourceDiagnostics();
        // The file holds the objects of every difficulty; only those of the selected one are shown.
        project.ArrangeStoryboard();
        return project;
    }

    /// <summary>
    /// A project from before videos were objects of the storyboard gets them from the .osu files it
    /// holds: each difficulty its own. The video file itself was not copied back then; it shows once it
    /// has been added to the assets.
    /// </summary>
    private void ReadVideos()
    {
        if (Settings.Version >= 3) return;
        Settings.Version = 3;
        foreach (var difficulty in Settings.Difficulties)
        {
            string file = DifficultyFile(difficulty.File);
            if (!File.Exists(file) || Storyboard.AllObjects.Any(o => o is SbVideo && o.Difficulty == difficulty.File)) continue;
            try
            {
                var parsed = OsbParser.Parse(File.ReadAllText(file), sourceFile: file);
                RecordDiagnostics(parsed.Diagnostics, DiagnosticOrigin.Load);
                foreach (var video in parsed.Storyboard.Videos)
                {
                    video.Difficulty = difficulty.File;
                    Storyboard.Objects.Add(video);
                }
            }
            catch (IOException)
            {
                // Without the file the difficulty simply has no video, as before.
            }
        }

    }

    public void Save()
    {
        NormalizeTriggerSimulation();
        MigrateGroupPrecision();
        Directory.CreateDirectory(Folder);
        Directory.CreateDirectory(AssetsDir);
        KeepDifficultySettings();
        WriteAtomic(ProjectFile, JsonSerializer.Serialize(Settings, JsonOptions));

        if (File.Exists(StoryboardFile))
            File.Copy(StoryboardFile, StoryboardFile + ".bak", true);
        WriteAtomic(StoryboardFile, OsbWriter.WriteFile(Storyboard, new OsbWriteOptions { IncludeEditorMeta = true, ConcreteVariableReferences = true }));
    }

    private void NormalizeTriggerSimulation()
    {
        Settings.TriggerSimulationRules ??= new();
        Settings.TriggerSimulationRules.RemoveAll(rule => rule == null);
        foreach (var rule in Settings.TriggerSimulationRules)
        {
            rule.Name ??= "";
            rule.Migrate();
            if (!Enum.IsDefined(rule.Kind)) { rule.Condition = rule.Kind.ToString(); rule.Kind = TriggerRuleKind.Unknown; }
            if (!Settings.TriggerSimulationEnabled) rule.Enabled = false;
        }
        Settings.TriggerSimulationEnabled = true;
    }

    private void MigrateGroupPrecision()
    {
        if (Settings.GroupStep == null && Settings.GroupTolerance == null) return;
        var legacy = BakeOptions.Of(Settings.GroupStep ?? BakeOptions.DefaultMinStep, Settings.GroupTolerance ?? BakeOptions.DefaultTolerance);
        foreach (var group in Storyboard.Groups.Where(g => !g.HasBakePrecision))
        {
            group.BakeStep = legacy.MinStep;
            group.BakeTolerance = legacy.PositionTolerance;
            group.HasBakePrecision = true;
        }
        Settings.GroupStep = null;
        Settings.GroupTolerance = null;
    }

    private static void WriteAtomic(string path, string content)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, true);
    }

    public string AssetFullPath(string relativePath) =>
        Path.GetFullPath(Path.Combine(AssetsDir, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    public bool AssetExists(string relativePath) => relativePath.Length > 0 && File.Exists(AssetFullPath(relativePath));

    /// <summary>The actual image file, or the original name when no candidate exists.</summary>
    public string ImagePath(string relativePath) => ImageAssets.Find(relativePath, AssetExists) ?? relativePath;

    public string ImageFullPath(string relativePath) => AssetFullPath(ImagePath(relativePath));

    /// <summary>The audio and background of the beatmap: those in use, and those of every difficulty.</summary>
    public IEnumerable<string> MainFiles()
    {
        yield return Settings.Audio;
        yield return ImagePath(Settings.Background);
        foreach (var difficulty in Settings.Shared == null ? Settings.Difficulties : Settings.Difficulties.Append(Settings.Shared))
        {
            yield return difficulty.Audio;
            yield return ImagePath(difficulty.Background);
        }
    }

    /// <summary>Whether a file is the audio or the background of the beatmap: the one in use, or that of any difficulty.</summary>
    public bool IsMainFile(string assetPath) =>
        MainFiles().Any(file => file.Length > 0 && file.Equals(assetPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>Path relative to the assets folder with forward slashes, or null when outside of it.</summary>
    public string? ToAssetPath(string fullPath)
    {
        string root = AssetsDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(fullPath);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        return full.Substring(root.Length).Replace('\\', '/');
    }

    /// <summary>
    /// Copies a file into the assets folder and returns its asset path. A file that is already an asset is
    /// left alone; an identical existing copy is reused; a different file with the same name gets a suffix.
    /// </summary>
    public string ImportAsset(string sourceFile, string subFolder = "")
    {
        string? existing = ToAssetPath(sourceFile);
        if (existing != null) return existing;

        string dir = AssetFullPath(subFolder);
        Directory.CreateDirectory(dir);
        string name = Path.GetFileNameWithoutExtension(sourceFile);
        string ext = Path.GetExtension(sourceFile);
        string target = Path.Combine(dir, name + ext);
        for (int n = 2; File.Exists(target); n++)
        {
            if (SameContent(sourceFile, target)) return ToAssetPath(target)!;
            target = Path.Combine(dir, $"{name} ({n}){ext}");
        }
        File.Copy(sourceFile, target);
        return ToAssetPath(target)!;
    }

    private static bool SameContent(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        using var sa = fa.OpenRead();
        using var sb = fb.OpenRead();
        var ba = new byte[81920];
        var bb = new byte[81920];
        while (true)
        {
            int ra = sa.ReadAtLeast(ba, ba.Length, false);
            int rb = sb.ReadAtLeast(bb, bb.Length, false);
            if (ra != rb) return false;
            if (ra == 0) return true;
            if (!ba.AsSpan(0, ra).SequenceEqual(bb.AsSpan(0, rb))) return false;
        }
    }

    public static AssetKind KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".bmp" => AssetKind.Image,
        ".mp3" or ".ogg" or ".wav" => AssetKind.Audio,
        // The kinds of file osu! takes for a video.
        ".avi" or ".flv" or ".mpg" or ".wmv" or ".m4v" or ".mp4" => AssetKind.Video,
        _ => AssetKind.Other,
    };

    public List<AssetInfo> ScanAssets()
    {
        var list = new List<AssetInfo>();
        if (!Directory.Exists(AssetsDir)) return list;
        foreach (string file in Directory.EnumerateFiles(AssetsDir, "*", SearchOption.AllDirectories))
        {
            string? rel = ToAssetPath(file);
            if (rel == null) continue;
            list.Add(new AssetInfo(rel, KindOf(rel), new FileInfo(file).Length));
        }
        list.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>Asset paths of the folders below the assets folder, the empty ones among them.</summary>
    public List<string> ScanAssetFolders()
    {
        var list = new List<string>();
        if (!Directory.Exists(AssetsDir)) return list;
        foreach (string dir in Directory.EnumerateDirectories(AssetsDir, "*", SearchOption.AllDirectories))
            if (ToAssetPath(dir) is { } rel) list.Add(rel);
        return list;
    }

    /// <summary>
    /// Orders names the way a file manager lists them: letters without regard to case, and a run of digits
    /// by its value, so that "a2.png" comes before "a10.png".
    /// </summary>
    public static int CompareNames(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                // Leading zeros do not change the value; the longer number is then the larger one.
                while (i < a.Length && a[i] == '0') i++;
                while (j < b.Length && b[j] == '0') j++;
                int endA = i, endB = j;
                while (endA < a.Length && char.IsAsciiDigit(a[endA])) endA++;
                while (endB < b.Length && char.IsAsciiDigit(b[endB])) endB++;
                int byValue = (endA - i).CompareTo(endB - j);
                if (byValue == 0) byValue = string.CompareOrdinal(a, i, b, j, endA - i);
                if (byValue != 0) return byValue;
                i = endA;
                j = endB;
            }
            else
            {
                int byLetter = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                if (byLetter != 0) return byLetter;
                i++;
                j++;
            }
        }
        int byLength = (a.Length - i).CompareTo(b.Length - j);
        return byLength != 0 ? byLength : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }
}

public static class AssetUsage
{
    /// <summary>Every file an object needs: the image, each animation frame, or the sample.</summary>
    public static IEnumerable<string> FilesOf(SbObject obj)
    {
        if (obj.Path.Length == 0) yield break;
        if (obj is SbSprite { IsAnimation: true } anim)
        {
            for (int i = 0; i < Math.Max(1, anim.FrameCount); i++)
                yield return SbSprite.FramePath(anim.Path, i);
        }
        else
        {
            yield return obj.Path;
        }
    }

    /// <summary>Distinct files referenced by the storyboard, whichever difficulty shows them, in first-use order.</summary>
    public static List<string> ReferencedFiles(Storyboard storyboard, Func<string, bool>? exists = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (string file in ResolvedFiles(storyboard, exists))
            if (seen.Add(file)) list.Add(file);
        return list;
    }

    /// <summary>How many objects use each file, counting every difficulty.</summary>
    public static Dictionary<string, int> CountByFile(Storyboard storyboard, Func<string, bool>? exists = null)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in ResolvedFiles(storyboard, exists))
            counts[file] = counts.GetValueOrDefault(file) + 1;
        return counts;
    }

    private static IEnumerable<string> ResolvedFiles(Storyboard storyboard, Func<string, bool>? exists)
    {
        // Large storyboards reuse a handful of images many thousands of times.
        var images = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var obj in storyboard.AllObjects)
            foreach (string file in FilesOf(obj))
            {
                if (obj is not SbSprite || exists == null) { yield return file; continue; }
                if (!images.TryGetValue(file, out string? resolved))
                    images[file] = resolved = ImageAssets.Find(file, exists) ?? file;
                yield return resolved;
            }
    }
}
