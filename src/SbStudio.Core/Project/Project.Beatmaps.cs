using System.Security.Cryptography;
using System.Text;

namespace SbStudio.Core;

/// <summary>A complete read performed off the UI thread before replacing any live state.</summary>
public sealed record BeatmapReferenceSnapshot(string Directory, bool Exists, IReadOnlyList<ReferencedDifficulty> Files)
{
    public static BeatmapReferenceSnapshot Read(string directory)
    {
        if (!System.IO.Directory.Exists(directory)) return new(directory, false, Array.Empty<ReferencedDifficulty>());
        var files = new List<ReferencedDifficulty>();
        foreach (string file in BeatmapImporter.Difficulties(directory))
        {
            var before = new FileInfo(file);
            long length = before.Length, stamp = before.LastWriteTimeUtc.Ticks;
            string text = File.ReadAllText(file);
            var after = new FileInfo(file);
            if (after.Length != length || after.LastWriteTimeUtc.Ticks != stamp)
                throw new IOException(L.T("reference.changing", Path.GetFileName(file)));
            string name = Path.GetFileName(file);
            var osu = OsuFile.Parse(text);
            var parsed = OsbParser.Parse(text, sourceFile: file);
            parsed.Storyboard.Variables = parsed.Storyboard.Variables.Select(v => v with { Difficulty = name }).ToList();
            foreach (var obj in parsed.Storyboard.Objects)
            {
                obj.Difficulty = name;
                if (obj is SbSprite sprite) _ = sprite.Timeline;
            }
            files.Add(new(name, osu, parsed.Storyboard, parsed.Diagnostics, Project.BeatmapHash(text)));
        }
        return new(directory, true, files);
    }
}

public sealed record ReferencedDifficulty(string File, OsuFile Osu, Storyboard Storyboard, List<Diagnostic> Diagnostics, string FileHash);

public sealed partial class Project
{
    private List<DifficultySettings>? availableDifficulties;
    private readonly HashSet<Diagnostic> referencedDiagnostics = new(ReferenceEqualityComparer.Instance);
    public IReadOnlyList<DifficultySettings> Difficulties => availableDifficulties ?? Settings.Difficulties;
    public string? BeatmapDirectory => string.IsNullOrWhiteSpace(Settings.BeatmapFolder) ? null : Path.GetFullPath(Path.Combine(Folder, Settings.BeatmapFolder));
    public bool BeatmapReferenceMissing => BeatmapDirectory is { } directory && !Directory.Exists(directory);
    public List<string> PreservedDifficultyEdits { get; } = new();

    internal void SetAvailableDifficulties(IEnumerable<string> files)
    {
        availableDifficulties = files.Select(file => Settings.Difficulties.First(d => d.File.Equals(file, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    internal static string BeatmapHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    internal static string DifficultyStoryboardHash(Storyboard storyboard, string file)
    {
        var own = new Storyboard { Objects = storyboard.AllObjects.Where(o => o.Difficulty == file).ToList() };
        var used = new HashSet<SbObjectGroup>();
        foreach (var sprite in own.Sprites)
            for (var group = sprite.Group; group != null; group = group.Parent) used.Add(group);
        own.Groups = storyboard.Groups.Where(used.Contains).ToList();
        own.Variables = storyboard.Variables.Where(v => v.Difficulty == file).ToList();
        return BeatmapHash(OsbWriter.WriteFile(own, new OsbWriteOptions { IncludeEditorMeta = true, PreserveVariables = false }));
    }

    public void ReferenceBeatmap(string directory)
    {
        string full = Path.GetFullPath(directory);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException(L.T("import.noFolder", full));
        var snapshot = BeatmapReferenceSnapshot.Read(full);
        string previous = Settings.BeatmapFolder;
        Settings.BeatmapFolder = full;
        try { RefreshBeatmaps(snapshot); }
        catch { Settings.BeatmapFolder = previous; throw; }
        if (Settings.Difficulty.Length == 0 && Difficulties.Count > 0) SelectDifficulty(Difficulties[0].File);
    }

    public void UnlinkBeatmap()
    {
        KeepDifficultySettings();
        Settings.BeatmapFolder = "";
        SetAvailableDifficulties(Array.Empty<string>());
        Settings.Difficulty = "";
        if (Settings.Shared != null) Use(Settings.Shared);
        ArrangeStoryboard();
    }

    /// <summary>Refresh external metadata and unchanged difficulty storyboards, preserving the shared storyboard and project edits.</summary>
    public void RefreshBeatmaps(BeatmapReferenceSnapshot? snapshot = null)
    {
        KeepDifficultySettings();
        PreservedDifficultyEdits.Clear();
        string selected = Settings.Difficulty;
        bool hadDifficulties = Difficulties.Count > 0;
        snapshot ??= BeatmapDirectory is { } directory ? BeatmapReferenceSnapshot.Read(directory) : null;
        if (snapshot != null && !string.Equals(snapshot.Directory, BeatmapDirectory, StringComparison.OrdinalIgnoreCase)) return;
        // Copy before changing the document: a failed file read or copy leaves its contents intact.
        foreach (var read in snapshot?.Files ?? Array.Empty<ReferencedDifficulty>())
            CopyReferencedAssets(read, snapshot!.Directory);
        LoadDiagnostics.RemoveAll(referencedDiagnostics.Contains);
        referencedDiagnostics.Clear();
        foreach (var diagnostic in snapshot?.Files.SelectMany(f => f.Diagnostics) ?? Enumerable.Empty<Diagnostic>())
        {
            referencedDiagnostics.Add(diagnostic);
            LoadDiagnostics.Add(diagnostic);
        }
        RecordDiagnostics(snapshot?.Files.SelectMany(f => f.Diagnostics) ?? Enumerable.Empty<Diagnostic>(), DiagnosticOrigin.Reference);
        foreach (var read in snapshot?.Files ?? Array.Empty<ReferencedDifficulty>())
        {
            var difficulty = Settings.Difficulties.FirstOrDefault(d => d.File.Equals(read.File, StringComparison.OrdinalIgnoreCase));
            if (difficulty == null) Settings.Difficulties.Add(difficulty = new DifficultySettings { File = read.File });
            if (difficulty.SourceFileHash == read.FileHash) continue;
            var owned = Storyboard.AllObjects.Where(o => o.Difficulty == difficulty.File).ToList();
            NormalizeBackgroundPlaceholders(read.Storyboard, obj => obj.Layer == SbLayer.Background && obj.Path.Length > 0 && obj.Path == read.Osu.Background);
            string sourceHash = DifficultyStoryboardHash(read.Storyboard, read.File);
            string currentHash = DifficultyStoryboardHash(Storyboard, difficulty.File);
            bool unchanged = difficulty.SourceStoryboardHash.Length > 0 ? currentHash == difficulty.SourceStoryboardHash : owned.Count == 0;
            if (unchanged)
            {
                Storyboard.Objects.RemoveAll(o => o.Difficulty == difficulty.File);
                Storyboard.Parked.RemoveAll(o => o.Difficulty == difficulty.File);
                foreach (var obj in read.Storyboard.Objects) obj.Difficulty = difficulty.File;
                Storyboard.Variables.RemoveAll(v => v.Difficulty == difficulty.File);
                Storyboard.Variables.AddRange(read.Storyboard.Variables.Select(v => v with { Difficulty = difficulty.File }));
                Storyboard.Objects.AddRange(read.Storyboard.Objects);
                Storyboard.Groups.AddRange(read.Storyboard.Groups);
            }
            else if (currentHash != sourceHash && difficulty.SourceStoryboardHash != sourceHash)
                PreservedDifficultyEdits.Add(difficulty.File);
            difficulty.Name = read.Osu.Version;
            difficulty.Audio = read.Osu.AudioFilename;
            difficulty.Background = read.Osu.Background ?? "";
            difficulty.Widescreen = read.Osu.Widescreen;
            difficulty.Timing = read.Osu.Timing;
            difficulty.SourceFileHash = read.FileHash;
            difficulty.SourceStoryboardHash = sourceHash;
        }
        SetAvailableDifficulties(snapshot?.Files.Select(f => f.File) ?? Array.Empty<string>());
        Settings.Difficulty = Difficulties.FirstOrDefault(d => d.File == selected)?.File ?? "";
        if (!hadDifficulties && Difficulties.Count > 0) Settings.Difficulty = Difficulties[0].File;
        if (CurrentDifficulty is { } current) Use(current);
        else if (selected.Length > 0 && Settings.Shared != null) Use(Settings.Shared);
        ArrangeStoryboard();
    }

    private void CopyReferencedAssets(ReferencedDifficulty difficulty, string directory)
    {
        string root = Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
        bool SourceExists(string file)
        {
            string path = Path.GetFullPath(Path.Combine(directory, file.Replace('/', Path.DirectorySeparatorChar)));
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(path);
        }
        var files = new List<(string Path, bool Image)> { (difficulty.Osu.AudioFilename, false), (difficulty.Osu.Background ?? "", true) };
        foreach (var obj in difficulty.Storyboard.AllObjects)
            files.AddRange(AssetUsage.FilesOf(obj).Select(file => (file, obj is SbSprite)));
        foreach (var (path, image) in files.Distinct())
        {
            if (path.Length == 0) continue;
            string resolved = image ? ImageAssets.Find(path, SourceExists) ?? path : path;
            if (!SourceExists(resolved)) continue;
            string target = AssetFullPath(resolved);
            if (ToAssetPath(target) == null || File.Exists(target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(directory, resolved.Replace('/', Path.DirectorySeparatorChar)), target, false);
        }
    }
}
