using System.IO.Compression;
using SbStudio.Resources;

namespace SbStudio.App.Gfx;

internal sealed record PreviewSkin(string Id, string Name, string? Folder);

/// <summary>Embedded preview styles and user skins next to the executable.</summary>
internal sealed class SkinLibrary
{
    public string Root { get; }
    public string BuiltIn { get; }
    public string? BeatmapFile { get; private set; }
    public string BeatmapFallbackId { get; set; } = "lazer";
    public int BeatmapMode { get; set; }
    public int BeatmapKeys { get; set; } = 4;
    public IReadOnlyList<PreviewSkin> Skins { get; private set; } = Array.Empty<PreviewSkin>();

    public SkinLibrary(string? root = null, string? builtIn = null)
    {
        Root = Path.GetFullPath(root ?? Path.Combine(AppContext.BaseDirectory, "Skins"));
        BuiltIn = builtIn == null ? ResourceAssets.DefaultSkin
            : ResourceAssets.IsEmbedded(builtIn) ? builtIn : Path.GetFullPath(builtIn);
        Refresh();
    }

    public void Refresh()
    {
        var result = new List<PreviewSkin> { new("stable", "stable", null), new("lazer", "lazer", null) };
        if (BeatmapFile != null && Path.GetDirectoryName(BeatmapFile) is { } beatmapFolder && Directory.Exists(beatmapFolder) && HasBeatmapImages(beatmapFolder))
        {
            result.Add(new("beatmap:stable", "Beatmap + stable", beatmapFolder));
            result.Add(new("beatmap:lazer", "Beatmap + lazer", beatmapFolder));
        }
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { BuiltIn };
        if (Directory.Exists(Root))
        {
            foreach (string folder in Directory.EnumerateDirectories(Root).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (Path.GetFileName(folder).StartsWith('.')) continue;
                if (found.Add(Path.GetFullPath(folder)) && IsSkin(folder)) result.Add(new("custom:" + Path.GetFullPath(folder), Name(folder), folder));
            }
        }
        Skins = result;
    }

    private static bool HasBeatmapImages(string folder)
    {
        try
        {
            if (Directory.EnumerateFiles(folder).Any(f => Path.GetExtension(f).Equals(".png", StringComparison.OrdinalIgnoreCase)
                && assetPrefixes.Any(prefix => Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))) return true;
            var skin = new OsuPreviewSkin(folder);
            if (skin.HasImage(skin.NumberPrefix + "-0")) return true;
            for (int keys = 1; keys <= 18; keys++)
                if (skin.HasManiaConfiguration(keys))
                    for (int column = 0; column < keys; column++)
                        foreach (string kind in new[] { "KeyImage", "KeyImageD", "NoteImage", "NoteImageH", "NoteImageL", "NoteImageT" })
                            if (skin.HasImage(skin.ManiaValue(keys, kind + column, "__missing__"))) return true;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public static bool IsBeatmapSkin(string id) => id == "beatmap" || id is "beatmap:stable" or "beatmap:lazer";
    public string BeatmapBase(string id) => id == "beatmap:stable" ? "stable" : id == "beatmap:lazer" ? "lazer" : BeatmapFallbackId == "lazer" ? "lazer" : "stable";
    public PreviewSkin Find(string id)
    {
        if (id == "beatmap") id = "beatmap:" + BeatmapBase(id);
        return Skins.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? Skins[0];
    }

    public bool SetBeatmap(string? file)
    {
        file = string.IsNullOrEmpty(file) ? null : Path.GetFullPath(file);
        if (string.Equals(file, BeatmapFile, StringComparison.OrdinalIgnoreCase)) return false;
        BeatmapFile = file;
        Refresh();
        return true;
    }

    public OsuPreviewSkin CreateSkin(string id, bool sounds = false)
    {
        var selected = Find(id);
        var stable = new OsuPreviewSkin(BuiltIn, defaultSkin: true);
        string baseId = IsBeatmapSkin(selected.Id) ? BeatmapBase(selected.Id) : selected.Id;
        OsuPreviewSkin parent = baseId == "lazer" && sounds
            ? new OsuPreviewSkin(ResourceAssets.Argon, stable, argonSounds: true, defaultSkin: true)
            : baseId is "stable" or "lazer" ? stable : new OsuPreviewSkin(selected.Folder!, stable);
        if (sounds)
        {
            // Audio always checks beatmap samples first, independently of the selected image skin.
            return BeatmapFile != null && Path.GetDirectoryName(BeatmapFile) is { } folder && Directory.Exists(folder)
                ? new OsuPreviewSkin(folder, parent, beatmapFile: BeatmapFile) : parent;
        }
        if (IsBeatmapSkin(selected.Id))
            return new OsuPreviewSkin(selected.Folder!, baseId == "lazer" && ProvidesLegacy(BeatmapMode, BeatmapKeys, selected.Id) ? stable : parent, beatmapFile: BeatmapFile);
        return parent;
    }

    public bool ProvidesLegacy(int mode, int keys, string id)
    {
        var selected = Find(id);
        if (selected.Id == "lazer") return false;
        var source = new OsuPreviewSkin(selected.Folder ?? BuiltIn, beatmapFile: IsBeatmapSkin(selected.Id) ? BeatmapFile : null);
        return source.HasImage("combo-0") || (mode switch
        {
            0 => source.HasImage("hitcircle"),
            1 => source.HasImage("taikohitcircle") || source.HasImage("taiko-bar-left"),
            2 => source.HasImage("fruit-pear"),
            3 => source.HasManiaConfiguration(keys) || source.HasImage(source.ManiaValue(keys, "KeyImage0", "mania-key1")),
            _ => false
        });
    }

    private static readonly string[] assetPrefixes = { "hitcircle", "default-", "combo-", "score-", "sliderb", "sliderfollowcircle", "approachcircle", "reversearrow", "sliderscorepoint", "followpoint", "spinner-", "mania-", "taiko", "fruit-", "lighting" };
    private static bool IsSkin(string folder) => File.Exists(Path.Combine(folder, "skin.ini")) || Directory.EnumerateFiles(folder).Any(f =>
        Path.GetExtension(f).Equals(".png", StringComparison.OrdinalIgnoreCase)
            ? assetPrefixes.Any(prefix => Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            : new[] { ".wav", ".ogg", ".mp3" }.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) &&
                new[] { "normal-", "soft-", "drum-", "spinner", "taiko-", "metronome" }.Any(prefix => Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));

    private static string Name(string folder)
    {
        string ini = Path.Combine(folder, "skin.ini");
        if (File.Exists(ini))
            foreach (string raw in File.ReadLines(ini))
            {
                string line = raw.Trim();
                if (line.StartsWith("Name:", StringComparison.OrdinalIgnoreCase) && line[5..].Trim() is { Length: > 0 } name) return name;
            }
        return Path.GetFileName(folder);
    }

    public PreviewSkin Import(string file)
    {
        Directory.CreateDirectory(Root);
        string staging = Path.Combine(Root, ".import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using (var zip = ZipFile.OpenRead(file))
            {
                if (zip.Entries.Count > 10000 || zip.Entries.Sum(e => e.Length) > 512L * 1024 * 1024) throw new InvalidDataException("Skin archive is too large.");
                // Validate every entry before extracting any of them (including Windows ADS and absolute paths).
                var entries = zip.Entries.Select(entry =>
                {
                    string name = entry.FullName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                    if (Path.IsPathRooted(name) || name.Contains(':') || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("Invalid skin archive path.");
                    string target = Path.GetFullPath(Path.Combine(staging, name));
                    if (!target.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid skin archive path.");
                    return (entry, target);
                }).ToArray();
                foreach (var (entry, target) in entries)
                {
                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var input = entry.Open();
                    using var output = new FileStream(target, FileMode.CreateNew);
                    input.CopyTo(output);
                }
            }
            string? content = IsSkin(staging) ? staging : Directory.EnumerateDirectories(staging, "*", SearchOption.AllDirectories).FirstOrDefault(IsSkin);
            if (content == null) throw new InvalidDataException("No skin assets found.");
            string name = string.Concat(Name(content).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
            if (name.Equals(Path.GetFileName(staging), StringComparison.OrdinalIgnoreCase))
                name = string.Concat(Path.GetFileNameWithoutExtension(file).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
            if (name.Length == 0 || name.StartsWith('.') || name.Equals(Path.GetFileName(staging), StringComparison.OrdinalIgnoreCase)) name = "Imported skin";
            if (name.Length > 100) name = name[..100];
            string destination = Path.Combine(Root, name);
            for (int n = 2; Directory.Exists(destination) || File.Exists(destination); n++) destination = Path.Combine(Root, name + " (" + n + ")");
            Directory.Move(content, destination);
            Refresh();
            return Find("custom:" + Path.GetFullPath(destination));
        }
        finally
        {
            // Only the staging folder created by this import is removed; existing skins are never overwritten.
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }
}
