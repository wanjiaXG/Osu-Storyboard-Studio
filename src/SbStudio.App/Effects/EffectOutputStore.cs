using System.Text;
using SbStudio.Core;
using SbStudio.Effects;
using AssetKind = SbStudio.Effects.AssetKind;

namespace SbStudio.App.Effects;

internal static class EffectOutputStore
{
    internal static PluginOutput Materialize(Project project, HostResponse response, string work, IEnumerable<string> sourceFiles, CancellationToken token)
    {
        if (response.OutputFile == null || !Path.GetFullPath(response.OutputFile).StartsWith(Path.GetFullPath(work).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Effect result path is outside its request directory.");
        if (new FileInfo(response.OutputFile).Length > PackageFiles.MaxTotalBytes || PackageFiles.HashFile(response.OutputFile) != response.OutputHash) throw new IOException("Invalid effect result file.");
        var output = EffectJson.Deserialize<StoryboardOutput>(File.ReadAllText(response.OutputFile));
        var assets = new Dictionary<string, string>();
        var watched = sourceFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Validate all model data first. Resource collection can leave unused files, never a half-applied document.
        var doc = EffectOutputAdapter.Convert(output, (asset, _) => asset.Path);
        string Resolve(AssetReference asset, int frames)
        {
            token.ThrowIfCancellationRequested(); string identity = EffectJson.Serialize(asset) + ":" + frames;
            if (assets.TryGetValue(identity, out string? existing)) return existing;
            string root = asset.Kind switch { AssetKind.Project => project.AssetsDir, AssetKind.Package => response.PackageDirectory!, AssetKind.Generated => Path.Combine(work, "generated"), _ => throw new IOException("Unknown asset kind.") };
            var files = new List<(string Name, byte[] Bytes)>();
            for (int i = 0; i < Math.Max(1, frames); i++)
            {
                string relative = frames > 0 ? FramePath(asset.Path, i) : asset.Path;
                string path = PackageFiles.SafePath(root, relative);
                if (asset.Kind == AssetKind.Project) watched.Add(path);
                if (new FileInfo(path).Length > PackageFiles.MaxFileBytes) throw new IOException("Effect image too large.");
                files.Add((Path.GetFileName(relative), File.ReadAllBytes(path)));
            }
            string hash = PackageFiles.Hash(Encoding.UTF8.GetBytes(string.Join('\n', files.Select(f => f.Name + ":" + PackageFiles.Hash(f.Bytes)))));
            string folder = "generated/effects/" + hash;
            Directory.CreateDirectory(Path.Combine(project.AssetsDir, folder));
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested(); string destination = PackageFiles.SafePath(project.AssetsDir, folder + "/" + file.Name);
                if (File.Exists(destination)) { if (PackageFiles.HashFile(destination) != PackageFiles.Hash(file.Bytes)) throw new IOException("Immutable effect asset has been modified."); }
                else WriteAtomic(destination, file.Bytes);
            }
            return assets[identity] = folder + "/" + Path.GetFileName(asset.Path);
        }
        for (int i = 0; i < output.Sprites.Count; i++)
        {
            var sprite = output.Sprites[i];
            doc.Objects[i].Path = Resolve(sprite.Asset, sprite.Frames);
        }
        string json = EffectJson.Serialize(new StoredOutput(output, assets)); string key = "effect-" + PackageFiles.Hash(Encoding.UTF8.GetBytes(json));
        string result = Path.Combine(project.Folder, "effects", "results", key[7..], "result.json");
        Directory.CreateDirectory(Path.GetDirectoryName(result)!);
        if (!File.Exists(result) || PackageFiles.HashFile(result) != key[7..]) WriteAtomic(result, Encoding.UTF8.GetBytes(json));
        return new(key, doc, response.Diagnostics.Select(d => d.Message), watched);
    }
    internal static PluginOutput? Restore(Project project, string? key)
    {
        if (key == null || !System.Text.RegularExpressions.Regex.IsMatch(key, "^effect-[a-f0-9]{64}$")) return null;
        string path = Path.Combine(project.Folder, "effects", "results", key[7..], "result.json");
        if (!File.Exists(path)) return null;
        try
        {
            if (new FileInfo(path).Length > PackageFiles.MaxTotalBytes) return null;
            string json = File.ReadAllText(path);
            if (PackageFiles.Hash(Encoding.UTF8.GetBytes(json)) != key[7..]) return null;
            var stored = EffectJson.Deserialize<StoredOutput>(json);
            return new(key, EffectOutputAdapter.Convert(stored.Output, (asset, frames) => stored.Assets[EffectJson.Serialize(asset) + ":" + frames]));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { return null; }
    }
    internal static string FramePath(string path, int frame) => path[..^Path.GetExtension(path).Length] + frame + Path.GetExtension(path);
    internal static void WriteAtomic(string target, byte[] bytes)
    {
        string tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (var file = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write)) { file.Write(bytes); file.Flush(true); } File.Move(tmp, target, true); }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
    private sealed record StoredOutput(StoryboardOutput Output, Dictionary<string, string> Assets);
}
