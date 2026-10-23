using System.Text;
using System.Text.Json;
using SbStudio.Core;
using SbStudio.Effects;
using AssetKind = SbStudio.Effects.AssetKind;

namespace SbStudio.App.Effects;

internal static class EffectPresets
{
    internal sealed record Preset(int FormatVersion, string EffectId, int SchemaVersion, string PackageVersion,
        Dictionary<string, JsonElement> Parameters, Dictionary<string, byte[]> Assets);
    internal static IEnumerable<string> Bundled(Project project, PluginLink link)
    {
        string root = EffectPluginSource.Root(project, link);
        if (!Directory.Exists(Path.Combine(root, "presets"))) yield break;
        foreach (string file in PackageFiles.EnumerateFiles(Path.Combine(root, "presets")))
        {
            if (!file.EndsWith(".sbpreset", StringComparison.OrdinalIgnoreCase)) continue;
            bool matches;
            try
            {
                if (new FileInfo(file).Length > PackageFiles.MaxFileBytes) continue;
                var preset = EffectJson.Deserialize<Preset>(File.ReadAllText(file));
                matches = preset.EffectId == link.Effect?.EffectId;
            }
            catch (Exception ex) when (ex is IOException or JsonException) { continue; }
            if (matches) yield return file;
        }
    }
    internal static void Save(Project project, PluginLink link, string destination)
    {
        var binding = link.Effect ?? throw new InvalidOperationException("Missing effect binding.");
        var values = EffectBinding.Parameters(link).Where(v => binding.Definition!.Parameters.Any(p => p.Id == v.Key)).ToDictionary();
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var p in binding.Definition!.Parameters.Where(p => p.Type is ParameterType.Image or ParameterType.Animation or ParameterType.File))
        {
            var asset = values.GetValueOrDefault(p.Id, p.Default).Deserialize<AssetReference>(EffectJson.Options)!;
            string root = asset.Kind == AssetKind.Project ? project.AssetsDir : EffectPluginSource.Root(project, link);
            string original = PackageFiles.SafePath(root, asset.Path);
            var collected = new List<string>();
            if (p.Type == ParameterType.Animation)
            {
                string directory = Path.GetDirectoryName(original)!, stem = Path.GetFileNameWithoutExtension(original), extension = Path.GetExtension(original);
                collected.AddRange(Directory.GetFiles(directory).Where(f => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f), "^" + System.Text.RegularExpressions.Regex.Escape(stem) + "[0-9]+" + System.Text.RegularExpressions.Regex.Escape(extension) + "$")));
            }
            else collected.Add(original);
            if (collected.Count == 0 || collected.Count > PackageFiles.MaxEntries) throw new IOException("Preset asset count is invalid.");
            var bytes = collected.Select(file =>
            {
                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (new FileInfo(file).Length > PackageFiles.MaxFileBytes) throw new IOException("Preset asset is too large.");
                return (Name: Path.GetFileName(file), Bytes: File.ReadAllBytes(PackageFiles.SafePath(root, relative)));
            }).ToArray();
            if (bytes.Any(b => b.Bytes.LongLength > PackageFiles.MaxFileBytes)) throw new IOException("Preset asset is too large.");
            string hash = PackageFiles.Hash(Encoding.UTF8.GetBytes(string.Join('\n', bytes.OrderBy(b => b.Name).Select(b => b.Name + ":" + PackageFiles.Hash(b.Bytes)))));
            foreach (var item in bytes) files[hash + "/" + item.Name] = item.Bytes;
            values[p.Id] = JsonSerializer.SerializeToElement(AssetReference.Project("generated/effects/" + hash + "/" + Path.GetFileName(asset.Path)), EffectJson.Options);
        }
        if (files.Values.Sum(b => b.LongLength) > PackageFiles.MaxTotalBytes) throw new IOException("Preset is too large.");
        byte[] json = Encoding.UTF8.GetBytes(EffectJson.Serialize(new Preset(1, binding.EffectId, binding.SchemaVersion, binding.PackageVersion, values, files)));
        EffectOutputStore.WriteAtomic(destination, json);
    }
    internal static Dictionary<string, JsonElement> Read(Project project, PluginLink link, string file)
    {
        if (new FileInfo(file).Length > PackageFiles.MaxTotalBytes * 2) throw new IOException("Preset is too large.");
        var preset = EffectJson.Deserialize<Preset>(File.ReadAllText(file)); var definition = link.Effect?.Definition ?? throw new InvalidOperationException("Unknown effect schema.");
        if (preset.FormatVersion != 1 || preset.EffectId != definition.EffectId || preset.SchemaVersion > definition.SchemaVersion ||
            preset.Parameters == null || preset.Assets == null || preset.Assets.Values.Any(b => b == null)) throw new FormatException("Incompatible preset.");
        foreach (var p in definition.Parameters) if (preset.Parameters.TryGetValue(p.Id, out var v)) ParameterValues.Validate(p, v);
        if (preset.Assets.Count > PackageFiles.MaxEntries || preset.Assets.Values.Any(b => b.LongLength > PackageFiles.MaxFileBytes) || preset.Assets.Values.Sum(b => b.LongLength) > PackageFiles.MaxTotalBytes) throw new IOException("Preset assets exceed limits.");
        foreach (var entry in preset.Assets)
        {
            string relative = "generated/effects/" + entry.Key, target = PackageFiles.SafePath(project.AssetsDir, relative);
            if (File.Exists(target) && PackageFiles.HashFile(target) != PackageFiles.Hash(entry.Value)) throw new IOException("Preset conflicts with an immutable asset.");
        }
        foreach (var entry in preset.Assets)
        {
            string target = PackageFiles.SafePath(project.AssetsDir, "generated/effects/" + entry.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); if (!File.Exists(target)) EffectOutputStore.WriteAtomic(target, entry.Value);
        }
        return preset.Parameters;
    }
}
