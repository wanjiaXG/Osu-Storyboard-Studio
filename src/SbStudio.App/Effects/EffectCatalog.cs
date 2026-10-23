using System.Reflection;
using SbStudio.Core;
using SbStudio.Effects;

namespace SbStudio.App.Effects;

public sealed record EffectCatalogEntry(string Directory, PackageManifest Manifest, EffectDefinition Definition, string SourceKind, bool Trusted);
internal sealed class EffectCatalog
{
    internal string Profile { get; }
    internal string Packages => Path.Combine(Profile, "packages");
    internal readonly List<EffectCatalogEntry> Entries = [];
    internal readonly List<string> Errors = [];
    private CatalogSettings settings;
    private string? builtInHash;
    internal EffectCatalog(string? profile = null)
    {
        Profile = profile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OsuStoryboardStudio", "effects");
        try { settings = EffectJson.Deserialize<CatalogSettings>(File.ReadAllText(Path.Combine(Profile, "catalog.json"))); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { settings = new(); }
        settings.TrustedPackages ??= [];
        settings.DevelopmentFolders ??= [];
        settings.Favorites ??= [];
        settings.DevelopmentEntries ??= [];
        using var stream = typeof(EditorApp).Assembly.GetManifestResourceStream("Effects.BuiltIn.Manifest");
        if (stream != null) { using var reader = new StreamReader(stream); builtInHash = EffectJson.Deserialize<PackageManifest>(reader.ReadToEnd()).ContentHash; }
    }
    internal void Refresh(Project? project)
    {
        Errors.Clear();
        Entries.RemoveAll(e => e.SourceKind != "development");
        foreach (var entry in settings.DevelopmentEntries)
            if (Directory.Exists(entry.Directory) && !Entries.Any(e => e.Directory == entry.Directory && e.Definition.EffectId == entry.Definition.EffectId))
                Entries.Add(entry with { Trusted = Trusted(entry.Directory, entry.Manifest, true) });
        ReadPackage(Path.Combine(AppContext.BaseDirectory, "effects", "builtin"), "builtin");
        foreach (string store in new[] { Packages, project == null ? "" : Path.Combine(project.Folder, "effects", "packages") })
            if (Directory.Exists(store)) foreach (string manifest in Directory.EnumerateFiles(store, "effect.json",
                new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }))
                if (!Path.GetRelativePath(store, manifest).Split(Path.DirectorySeparatorChar).Any(p => p.StartsWith('.'))) ReadPackage(Path.GetDirectoryName(manifest)!, store == Packages ? "installed" : "project");
        void ReadPackage(string root, string kind)
        {
            try
            {
                var manifest = PackageFiles.ValidateDirectory(root);
                bool trusted = kind == "builtin" && manifest.ContentHash == builtInHash || settings.TrustedPackages.Contains(manifest.ContentHash);
                var definitions = EffectJson.Deserialize<EffectDefinition[]>(File.ReadAllText(PackageFiles.SafePath(root, "schema.json")));
                foreach (var definition in definitions)
                {
                    if (definition == null || definition.Parameters == null || definition.Name == null || definition.Category == null || definition.Description == null || definition.Parameters.Any(p => p == null || p.Id == null || p.Label == null || p.Description == null || p.Section == null || p.Unit == null)) throw new FormatException("Incomplete effect definition.");
                    if (manifest.Effects.Any(e => e.EffectId == definition.EffectId) && !Entries.Any(e => e.Manifest.ContentHash == manifest.ContentHash && e.Definition.EffectId == definition.EffectId))
                        Entries.Add(new(root, manifest, definition, kind, trusted));
                }
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or FormatException or System.Text.Json.JsonException) { Errors.Add(root + ": " + ex.Message); }
        }
    }
    internal bool Trusted(string root, PackageManifest manifest, bool development) => development
        ? settings.DevelopmentFolders.Contains(Path.GetFullPath(root), StringComparer.OrdinalIgnoreCase)
        : manifest.ContentHash == builtInHash || settings.TrustedPackages.Contains(manifest.ContentHash);
    internal void Trust(string root, PackageManifest manifest, bool development)
    {
        if (development) settings.DevelopmentFolders.Add(Path.GetFullPath(root)); else settings.TrustedPackages.Add(manifest.ContentHash);
        Save();
    }
    internal IReadOnlyList<string> DevelopmentFolders => settings.DevelopmentFolders.ToArray();
    internal void RememberDevelopment(EffectCatalogEntry entry)
    { settings.DevelopmentEntries.RemoveAll(e => e.Directory == entry.Directory && e.Definition.EffectId == entry.Definition.EffectId); settings.DevelopmentEntries.Add(entry); Save(); }
    internal bool Favorite(string id) => settings.Favorites.Contains(id);
    internal void ToggleFavorite(string id) { if (!settings.Favorites.Remove(id)) settings.Favorites.Add(id); Save(); }
    private void Save()
    {
        Directory.CreateDirectory(Profile); string target = Path.Combine(Profile, "catalog.json"), tmp = target + ".tmp";
        File.WriteAllText(tmp, EffectJson.Serialize(settings)); File.Move(tmp, target, true);
    }
    private sealed class CatalogSettings
    {
        public HashSet<string> TrustedPackages { get; set; } = new(StringComparer.Ordinal);
        public HashSet<string> DevelopmentFolders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Favorites { get; set; } = new(StringComparer.Ordinal);
        public List<EffectCatalogEntry> DevelopmentEntries { get; set; } = [];
    }
}
