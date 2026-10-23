namespace SbStudio.Core;

public sealed class BeatmapImportReport
{
    public int Objects;
    public int FilesCopied;
    public List<string> MissingFiles = new();
    public List<Diagnostic> Diagnostics = new();
    /// <summary>The difficulties that were read, in the order they are listed.</summary>
    public List<string> OsuFiles = new();
    /// <summary>The .osb that was read: the one that goes with the difficulties, see <see cref="BeatmapImporter.MatchOsb"/>.</summary>
    public string? OsbFile;
    /// <summary>The other .osb files of the folder, by name. They were not read.</summary>
    public List<string> OsbSkipped = new();
    /// <summary>The name osu! looks for the shared storyboard under, when the folder has .osb files and none that goes with its difficulties.</summary>
    public string? OsbExpected;
    /// <summary>The audio file taken as the main audio because no difficulty names one.</summary>
    public string? Audio;
}

/// <summary>Brings an existing beatmap's storyboard, timing and files into a project.</summary>
public static class BeatmapImporter
{
    /// <summary>
    /// Reads a beatmap folder: the storyboard lines of every difficulty (.osu), each kept apart as that
    /// difficulty's own, and the storyboard all of them share: the .osb that goes with them by its name,
    /// see <see cref="MatchOsb"/>. The first difficulty is the one shown. A folder that is new and has no
    /// more than its song in it gives a project with that song.
    /// </summary>
    /// <param name="path">The beatmap folder, or any file inside it. A .osb that is named this way is the one that is read.</param>
    /// <param name="replace">Replace the project's storyboard and difficulties instead of adding to them.</param>
    public static BeatmapImportReport Import(Project project, string path, bool replace)
    {
        var report = new BeatmapImportReport();
        string dir = Directory.Exists(path) ? Path.GetFullPath(path) : Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException(L.T("import.noFolder", dir));
        // The file that was pointed at, when it was a file and not the folder.
        string named = Directory.Exists(path) ? "" : Path.GetFileName(Path.GetFullPath(path));

        var osuPaths = Difficulties(dir);
        var osbPaths = Directory.EnumerateFiles(dir, "*.osb").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
        // Without a difficulty to say which file is the song, it is the first audio file of the folder.
        string? audioPath = osuPaths.Count > 0 ? null : Directory.EnumerateFiles(dir)
            .Where(f => Project.KindOf(f) == AssetKind.Audio)
            .OrderBy(f => Path.GetExtension(f).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (osuPaths.Count == 0 && osbPaths.Count == 0 && audioPath == null) throw new FileNotFoundException(L.T("import.nothing", dir));

        var settings = project.Settings;
        settings.BeatmapFolder = dir;
        if (replace)
        {
            project.Storyboard.Objects.Clear();
            project.Storyboard.Parked.Clear();
            project.Storyboard.Variables.Clear();
            settings.Difficulties.Clear();
            settings.Difficulty = "";
            settings.Shared = null;
        }

        // osu! reads a difficulty's own storyboard before the shared one, so that is the order here too.
        var objects = new List<SbObject>();
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string root = dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var images = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool SourceExists(string file)
        {
            string source = Path.GetFullPath(Path.Combine(dir, file.Replace('/', Path.DirectorySeparatorChar)));
            return source.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(source);
        }
        void Need(string file, bool image = false)
        {
            if (image && file.Length > 0)
            {
                if (!images.TryGetValue(file, out string? resolved))
                    images[file] = resolved = ImageAssets.Find(file, SourceExists) ?? file;
                file = resolved;
            }
            if (file.Length > 0 && seen.Add(file)) files.Add(file);
        }

        string? firstTitle = null;
        var difficulties = new List<(string File, OsuFile Osu)>();
        foreach (string osuPath in osuPaths)
        {
            string name = Path.GetFileName(osuPath);
            string text = File.ReadAllText(osuPath);
            var osu = OsuFile.Parse(text);
            difficulties.Add((name, osu));
            var parsed = OsbParser.Parse(text, sourceFile: osuPath);
            foreach (var obj in parsed.Storyboard.Objects) obj.Difficulty = name;
            parsed.Storyboard.Variables = parsed.Storyboard.Variables.Select(v => v with { Difficulty = name }).ToList();
            project.Storyboard.Variables.RemoveAll(v => v.Difficulty == name);
            project.Storyboard.Variables.AddRange(parsed.Storyboard.Variables.Select(v => v with { Difficulty = name }));
            objects.AddRange(parsed.Storyboard.Objects);
            report.Diagnostics.AddRange(parsed.Diagnostics);
            project.RecordDiagnostics(parsed.Diagnostics, DiagnosticOrigin.Import);
            report.OsuFiles.Add(name);
            if (firstTitle == null && osu.Title.Length > 0) firstTitle = osu.OsbFileName();

            var difficulty = settings.Difficulties.FirstOrDefault(d => d.File.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (difficulty == null)
            {
                difficulty = new DifficultySettings { File = name };
                settings.Difficulties.Add(difficulty);
            }
            difficulty.Name = osu.Version;
            difficulty.Audio = osu.AudioFilename;
            difficulty.Background = osu.Background ?? "";
            difficulty.Widescreen = osu.Widescreen;
            difficulty.Timing = osu.Timing;
            difficulty.SourceFileHash = Project.BeatmapHash(text);
            Project.NormalizeBackgroundPlaceholders(parsed.Storyboard, obj => obj.Layer == SbLayer.Background && obj.Path.Length > 0 && obj.Path == osu.Background);
            difficulty.SourceStoryboardHash = Project.DifficultyStoryboardHash(parsed.Storyboard, name);
            Need(difficulty.Audio);
            Need(difficulty.Background, image: true);

        }

        // A .osb that was pointed at is the one; a difficulty that was is the first to be asked which it is.
        var osbNames = osbPaths.Select(p => Path.GetFileName(p)).ToList();
        string? osbName = osbNames.Find(n => n.Equals(named, StringComparison.OrdinalIgnoreCase)) ??
                          MatchOsb(osbNames, difficulties.OrderByDescending(d => d.File.Equals(named, StringComparison.OrdinalIgnoreCase)).ToList());
        string? osbPath = osbName == null ? null : Path.Combine(dir, osbName);
        report.OsbFile = osbPath;
        report.OsbSkipped = osbNames.Where(n => n != osbName).ToList();
        if (osbPath == null && osbNames.Count > 0 && difficulties.Count > 0) report.OsbExpected = difficulties[0].Osu.OsbFileName();

        if (osbPath != null)
        {
            var parsed = OsbParser.Parse(File.ReadAllText(osbPath), sourceFile: osbPath);
            project.Storyboard.Variables.RemoveAll(v => v.Difficulty == null);
            project.Storyboard.Variables.AddRange(parsed.Storyboard.Variables);
            objects.AddRange(parsed.Storyboard.Objects);
            report.Diagnostics.AddRange(parsed.Diagnostics);
            project.RecordDiagnostics(parsed.Diagnostics, DiagnosticOrigin.Import);
        }
        if (string.IsNullOrEmpty(settings.Export.FileName))
            settings.Export.FileName = firstTitle ?? (osbPath != null ? Path.GetFileName(osbPath) : "");
        if (audioPath != null && (replace || settings.Audio.Length == 0))
        {
            settings.Audio = report.Audio = Path.GetFileName(audioPath);
            Need(settings.Audio);
        }

        foreach (var obj in objects)
            foreach (string file in AssetUsage.FilesOf(obj)) Need(file, image: obj is SbSprite);

        foreach (string file in files)
        {
            string source = Path.GetFullPath(Path.Combine(dir, file.Replace('/', Path.DirectorySeparatorChar)));
            string target = project.AssetFullPath(file);
            bool safe = source.StartsWith(root, StringComparison.OrdinalIgnoreCase) && project.ToAssetPath(target) != null;
            if (!safe || !File.Exists(source))
            {
                report.MissingFiles.Add(file);
                project.RecordDiagnostics(new[] { new Diagnostic(0, L.T("diagnostics.importMissing", file), false, source) }, DiagnosticOrigin.Import);
                continue;
            }
            if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(source).Length) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, true);
            report.FilesCopied++;
        }

        project.Storyboard.Objects.AddRange(objects);
        report.Objects = objects.Count;
        project.SetAvailableDifficulties(report.OsuFiles);

        // The first difficulty of the folder is shown, unless the project already shows one of its own.
        if (project.CurrentDifficulty == null && report.OsuFiles.Count > 0)
        {
            settings.Difficulty = "";
            project.UseDifficulty(report.OsuFiles[0]);
            // What was in use before belongs to nothing when the project started afresh.
            if (replace) settings.Shared = null;
        }
        else if (project.CurrentDifficulty is { } current && report.OsuFiles.Contains(current.File, StringComparer.OrdinalIgnoreCase))
        {
            // The difficulty in use was read again: what the file says now is what is in use.
            project.Use(current);
        }
        project.ArrangeStoryboard();
        settings.SourceDiagnosticsInitialized = true;
        return report;
    }

    /// <summary>
    /// The difficulty files (.osu) of a beatmap folder in the order they are read, which is by name: the
    /// first is the one a project made from the folder shows, with its timing. None when there is no such folder.
    /// </summary>
    /// <param name="path">The beatmap folder, or any file inside it.</param>
    public static List<string> Difficulties(string path)
    {
        string? dir = Directory.Exists(path) ? Path.GetFullPath(path) : File.Exists(path) ? Path.GetDirectoryName(Path.GetFullPath(path)) : null;
        if (dir == null || !Directory.Exists(dir)) return new List<string>();
        return Directory.EnumerateFiles(dir, "*.osu").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Which of the .osb files of a beatmap folder is the shared storyboard of its difficulties. osu! does
    /// not take whatever .osb it finds: it looks for the one named after the beatmap, artist, title and
    /// mapper as the difficulty has them (<see cref="OsuFile.OsbFileName"/>), and so a folder may well hold
    /// others that the game never reads: an older version, or the storyboard of another set.
    ///
    /// The difficulties are asked in turn for that name. When none of them finds its file, one named like
    /// a .osu file without its "[difficulty]" will do, which is the same thing unless the files were
    /// renamed. A folder without difficulties has nothing to tell its .osb files apart by: there the first
    /// is taken.
    /// </summary>
    /// <returns>The name of the file, or null when none goes with the difficulties.</returns>
    public static string? MatchOsb(IReadOnlyList<string> osbFiles, IReadOnlyList<(string File, OsuFile Osu)> difficulties)
    {
        if (osbFiles.Count == 0) return null;
        if (difficulties.Count == 0) return osbFiles[0];
        string? Named(string name) => osbFiles.FirstOrDefault(f => f.Equals(name, StringComparison.OrdinalIgnoreCase));
        foreach (var (_, osu) in difficulties)
            if (Named(osu.OsbFileName()) is { } match) return match;
        foreach (var (file, _) in difficulties)
            if (Named(WithoutDifficulty(Path.GetFileNameWithoutExtension(file)) + ".osb") is { } match) return match;
        return null;
    }

    /// <summary>The name of a .osu file without the "[difficulty]" at its end, which may have brackets of its own in it.</summary>
    private static string WithoutDifficulty(string name)
    {
        name = name.TrimEnd();
        if (!name.EndsWith(']')) return name;
        int depth = 0;
        for (int i = name.Length - 1; i >= 0; i--)
        {
            if (name[i] == ']') depth++;
            else if (name[i] == '[' && --depth == 0) return name.Substring(0, i).TrimEnd();
        }
        return name;
    }
}
