using System.Text;
using SbStudio.Core;
using SbStudio.Effects;

namespace SbStudio.App.Effects;

internal static class EffectClipboard
{
    private static readonly object gate = new();
    private static readonly Dictionary<string, string> sources = new();
    private static readonly Queue<string> order = new();
    internal static void Remember(string script, Project project)
    {
        string key = PackageFiles.Hash(Encoding.UTF8.GetBytes(script));
        lock (gate)
        {
            if (!sources.ContainsKey(key)) order.Enqueue(key);
            sources[key] = project.Folder;
            while (order.Count > 8) sources.Remove(order.Dequeue());
        }
    }
    internal static void Collect(string script, Project target, Storyboard pasted)
    {
        string? source;
        lock (gate) sources.TryGetValue(PackageFiles.Hash(Encoding.UTF8.GetBytes(script)), out source);
        if (source == null || source == target.Folder) return;
        foreach (var group in pasted.Groups.Where(g => g.Plugin?.Effect != null))
        {
            var link = group.Plugin!; var binding = link.Effect!;
            foreach (string relative in pasted.SpritesIn(group, true).SelectMany(AssetUsage.FilesOf).Distinct()) CopyAsset(relative);
            foreach (var parameter in binding.Definition?.Parameters ?? [])
            {
                if (parameter.Type is not (ParameterType.Image or ParameterType.Animation or ParameterType.File)) continue;
                var values = EffectBinding.Parameters(link);
                var asset = System.Text.Json.JsonSerializer.Deserialize<AssetReference>(values.GetValueOrDefault(parameter.Id, parameter.Default), EffectJson.Options)!;
                if (asset.Kind != SbStudio.Effects.AssetKind.Project) continue;
                if (parameter.Type != ParameterType.Animation) CopyAsset(asset.Path);
                else
                {
                    string path = PackageFiles.SafePath(Path.Combine(source, "assets"), asset.Path), stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
                    foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(path)!))
                        if (System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(file), "^" + System.Text.RegularExpressions.Regex.Escape(stem) + "[0-9]+" + System.Text.RegularExpressions.Regex.Escape(ext) + "$"))
                            CopyAsset(Path.GetRelativePath(Path.Combine(source, "assets"), file).Replace('\\', '/'));
                }
            }
            string package = Path.GetFullPath(Path.Combine(source, binding.SourceLocation));
            if (binding.SourceKind == "package" && Directory.Exists(package))
            {
                string archive = Path.Combine(Path.GetTempPath(), "SbStudio-clipboard-" + Guid.NewGuid().ToString("N") + ".sbeffect");
                try
                { PackageFiles.Pack(package, archive); string installed = PackageFiles.Install(archive, Path.Combine(target.Folder,"effects","packages")); binding.SourceLocation = Path.GetRelativePath(target.Folder, installed).Replace('\\','/'); }
                finally { if (File.Exists(archive)) File.Delete(archive); }
            }
            else binding.SourceLocation = package;
            if (link.Key is {} key && System.Text.RegularExpressions.Regex.IsMatch(key,"^effect-[a-f0-9]{64}$"))
            {
                string relative = "effects/results/" + key[7..] + "/result.json";
                string original = PackageFiles.SafePath(source, relative);
                if (File.Exists(original)) CopyImmutable(original, PackageFiles.SafePath(target.Folder, relative));
            }
        }
        void CopyAsset(string relative)
        {
            string? actual = ImageAssets.Find(relative, path => File.Exists(PackageFiles.SafePath(Path.Combine(source, "assets"), path)));
            if (actual == null) throw new IOException("A copied effect asset is missing: " + relative);
            CopyImmutable(PackageFiles.SafePath(Path.Combine(source, "assets"), actual), PackageFiles.SafePath(target.AssetsDir, actual));
        }
        static void CopyImmutable(string from, string to)
        {
            if (File.Exists(to)) { if (PackageFiles.HashFile(from) != PackageFiles.HashFile(to)) throw new IOException("Copied effect asset conflicts with an existing file."); return; }
            if (new FileInfo(from).Length > PackageFiles.MaxTotalBytes) throw new IOException("Copied effect resource exceeds limit.");
            Directory.CreateDirectory(Path.GetDirectoryName(to)!); EffectOutputStore.WriteAtomic(to, File.ReadAllBytes(from));
        }
    }
}
