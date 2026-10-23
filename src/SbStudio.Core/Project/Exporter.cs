using System.IO.Compression;
using System.Text;

namespace SbStudio.Core;

public sealed class ExportOptions
{
    public string OutputFolder = "";
    public string FileName = "storyboard.osb";
    public ExportAssetMode Assets = ExportAssetMode.Used;
    public bool IncludeAudioAndBackground;
    /// <summary>Write a single .zip next to nothing else instead of loose files.</summary>
    public bool Zip;
    /// <summary>Also write the .osu file of every difficulty, with its own part of the storyboard in it.</summary>
    public bool Difficulties = true;
}

public sealed class ExportReport
{
    public string OsbPath = "";
    /// <summary>The folder, or the zip file when zipping.</summary>
    public string OutputPath = "";
    /// <summary>The difficulty files that were written.</summary>
    public List<string> OsuFiles = new();
    public int FilesCopied;
    public long BytesCopied;
    /// <summary>Files the storyboard references that are not in the assets folder.</summary>
    public List<string> MissingFiles = new();
    /// <summary>Sprites whose group motion was written out as their own commands.</summary>
    public int BakedSprites;
    /// <summary>Things that could not be exported faithfully.</summary>
    public List<string> Warnings = new();
}

public static class Exporter
{
    public static string NormalizeFileName(string name)
    {
        name = name.Trim();
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c.ToString(), "");
        if (name.Length == 0) name = "storyboard";
        if (!name.EndsWith(".osb", StringComparison.OrdinalIgnoreCase)) name += ".osb";
        return name;
    }

    /// <summary>Files that would be copied, plus the referenced files that are missing.</summary>
    public static (List<string> Files, List<string> Missing) CollectAssets(Project project, ExportOptions options)
    {
        var files = new List<string>();
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string path)
        {
            if (path.Length > 0 && seen.Add(path)) files.Add(path);
        }

        foreach (string file in AssetUsage.ReferencedFiles(project.Storyboard, project.AssetExists))
        {
            // Paths that climb out of the assets folder are never copied.
            bool inside = project.ToAssetPath(project.AssetFullPath(file)) != null;
            if (inside && project.AssetExists(file))
            {
                if (options.Assets != ExportAssetMode.None) Add(file);
            }
            else
            {
                missing.Add(file);
            }
        }

        if (options.Assets == ExportAssetMode.All)
        {
            foreach (var asset in project.ScanAssets())
                if (options.IncludeAudioAndBackground || !project.IsMainFile(asset.Path)) Add(asset.Path);
        }

        if (options.IncludeAudioAndBackground)
        {
            // The audio and background in use, and those of every other difficulty.
            foreach (string file in project.MainFiles())
                if (project.AssetExists(file)) Add(file);
        }

        return (files, missing);
    }

    /// <summary>The objects of one difficulty, or with null the ones all difficulties share, wherever they are kept.</summary>
    private static Storyboard PartOf(Project project, string? difficulty) => new()
    {
        Objects = project.Storyboard.AllObjects.Where(o => o.Difficulty == difficulty).ToList(),
        Groups = project.Storyboard.Groups,
    };

    /// <summary>How many objects belong to a difficulty rather than to the shared storyboard.</summary>
    public static int DifficultyObjects(Project project) => project.Storyboard.AllObjects.Count(o => o.Difficulty != null);

    public static ExportReport Export(Project project, ExportOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.OutputFolder)) throw new ArgumentException(L.T("export.noFolder"));

        string fileName = NormalizeFileName(options.FileName);
        var writeOptions = new OsbWriteOptions { IncludeEditorMeta = false };
        // osu! knows nothing about groups: their motion is written into each member.
        var warnings = new List<string>();
        // The .osb is what all difficulties share. What belongs to one difficulty goes into its .osu.
        var shared = PartOf(project, null);

        string osbText = OsbWriter.WriteFile(GroupBaker.Bake(shared, warnings: warnings), writeOptions);
        int baked = shared.Sprites.Count(s => s.Group != null);

        var osuTexts = new List<(string File, string Text)>();
        int left = 0;
        foreach (var difficulty in project.Difficulties)
        {
            var part = PartOf(project, difficulty.File);
            if (!options.Difficulties)
            {
                left += part.Objects.Count;
                continue;
            }
            // Update an existing destination in place; otherwise read the latest referenced .osu.
            string existing = Path.Combine(options.OutputFolder, difficulty.File);
            string kept = project.DifficultyFile(difficulty.File);
            string? source = !options.Zip && File.Exists(existing) ? existing : File.Exists(kept) ? kept : null;
            if (source == null)
            {
                if (part.Objects.Count > 0) warnings.Add(L.T("export.osuMissing", difficulty.File, part.Objects.Count));
                continue;
            }
            var bakedPart = GroupBaker.Bake(part, warnings: warnings);
            string layers = OsbWriter.WriteLayers(bakedPart, writeOptions);
            osuTexts.Add((difficulty.File, OsuFile.ReplaceStoryboard(File.ReadAllText(source), layers, OsbWriter.WriteVideos(bakedPart, writeOptions))));
            baked += part.Sprites.Count(s => s.Group != null);
        }
        if (left > 0) warnings.Add(L.T("export.osuSkipped", left));

        var (files, missing) = CollectAssets(project, options);
        var report = new ExportReport
        {
            MissingFiles = missing,
            Warnings = warnings,
            BakedSprites = baked,
            OsuFiles = osuTexts.Select(o => o.File).ToList(),
        };

        Directory.CreateDirectory(options.OutputFolder);

        if (options.Zip)
        {
            string zipPath = Path.Combine(options.OutputFolder, Path.GetFileNameWithoutExtension(fileName) + ".zip");
            using (var stream = new FileStream(zipPath, FileMode.Create))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry(fileName, CompressionLevel.Optimal);
                using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                    writer.Write(osbText);
                foreach (var (file, text) in osuTexts)
                {
                    var osuEntry = zip.CreateEntry(file, CompressionLevel.Optimal);
                    using var writer = new StreamWriter(osuEntry.Open(), new UTF8Encoding(false));
                    writer.Write(text);
                }

                foreach (string file in files)
                {
                    string source = project.AssetFullPath(file);
                    zip.CreateEntryFromFile(source, file, CompressionLevel.Fastest);
                    report.FilesCopied++;
                    report.BytesCopied += new FileInfo(source).Length;
                }
            }
            report.OutputPath = zipPath;
            report.OsbPath = zipPath;
            return report;
        }

        string osbPath = Path.Combine(options.OutputFolder, fileName);
        File.WriteAllText(osbPath, osbText, new UTF8Encoding(false));
        foreach (var (file, text) in osuTexts)
        {
            string osuPath = Path.Combine(options.OutputFolder, file);
            if (File.Exists(osuPath))
            {
                // A beatmap file is more than its storyboard: one that changes is kept next to the new one.
                if (File.ReadAllText(osuPath) == text) continue;
                File.Copy(osuPath, osuPath + ".bak", true);
            }
            File.WriteAllText(osuPath, text, new UTF8Encoding(false));
        }

        string outRoot = Path.GetFullPath(options.OutputFolder);
        foreach (string file in files)
        {
            string source = project.AssetFullPath(file);
            string target = Path.GetFullPath(Path.Combine(outRoot, file.Replace('/', Path.DirectorySeparatorChar)));
            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, true);
            report.FilesCopied++;
            report.BytesCopied += new FileInfo(source).Length;
        }

        report.OutputPath = outRoot;
        report.OsbPath = osbPath;
        return report;
    }
}
