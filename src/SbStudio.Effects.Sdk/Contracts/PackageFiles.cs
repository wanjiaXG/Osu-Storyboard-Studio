using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace SbStudio.Effects;

/// <summary>Shared package validation; this never loads or executes assemblies.</summary>
public static class PackageFiles
{
    public static IEnumerable<string> EnumerateFiles(string root, bool development = false)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
        {
            if (development && Path.GetFileName(entry) is "bin" or "obj" or ".git" or ".vs" or ".build") continue;
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Linked effect paths are not allowed.");
            if (attributes.HasFlag(FileAttributes.Directory)) { foreach (string file in EnumerateFiles(entry, development)) yield return file; }
            else yield return entry;
        }
    }
    public const int MaxEntries = 4096;
    public const long MaxFileBytes = 128L * 1024 * 1024, MaxTotalBytes = 512L * 1024 * 1024;
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    public static string SafePath(string root, string relative)
    {
        new AssetReference(AssetKind.Package, relative).Validate();
        if (relative.Split('/').Any(p => p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            System.Text.RegularExpressions.Regex.IsMatch(p, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            throw new IOException("Unsafe effect package path.");
        string basePath = Path.GetFullPath(root), path = Path.GetFullPath(Path.Combine(basePath, relative));
        if (!path.StartsWith(basePath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Package path escapes root.");
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked effect paths are not allowed.");
            if (string.Equals(current, basePath, StringComparison.OrdinalIgnoreCase)) break;
        }
        return path;
    }
    public static PackageManifest ReadManifest(string root)
    {
        string file = SafePath(root, "effect.json");
        if (new FileInfo(file).Length > 1024 * 1024) throw new IOException("Effect manifest is too large.");
        var manifest = EffectJson.Deserialize<PackageManifest>(File.ReadAllText(file)); ValidateManifest(manifest); return manifest;
    }
    public static void ValidateManifest(PackageManifest m)
    {
        if (m.PackageId == null || m.PackageVersion == null || m.EntryAssembly == null || m.Effects == null || m.Dependencies == null || m.Files == null)
            throw new FormatException("Incomplete effect manifest.");
        if (m.FormatVersion != 1 || m.SdkMajor != 1 || !Version.TryParse(m.MinHostVersion, out var minimum) || minimum > new Version(1, 0, 0)) throw new NotSupportedException("Incompatible effect package.");
        EffectDefinitionBuilder.ValidateId(m.PackageId);
        if (!System.Text.RegularExpressions.Regex.IsMatch(m.PackageVersion, "^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[a-zA-Z0-9.-]+)?$")) throw new FormatException("Invalid package version.");
        if (m.Effects.Count == 0 || m.Effects.Count > 128 || m.Effects.Any(e => e == null) || m.Effects.Select(e => e.EffectId).Distinct().Count() != m.Effects.Count) throw new FormatException("Invalid package entries.");
        foreach (var e in m.Effects)
        {
            if (e == null) throw new FormatException("Invalid effect entry.");
            EffectDefinitionBuilder.ValidateId(e.EffectId);
            if (string.IsNullOrWhiteSpace(e.EntryType)) throw new FormatException("Missing effect entry type.");
        }
        new AssetReference(AssetKind.Package, m.EntryAssembly).Validate();
        if (!m.EntryAssembly.StartsWith("lib/", StringComparison.Ordinal) || !m.EntryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) throw new FormatException("Entry assembly must be a lib DLL.");
        foreach (var d in m.Dependencies)
            if (d == null || !d.StartsWith("lib/", StringComparison.Ordinal) || !d.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) throw new FormatException("Dependencies must be lib DLLs.");
        var names = m.Dependencies.Select(Path.GetFileNameWithoutExtension).ToArray();
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length ||
            names.Any(n => string.Equals(n, "SbStudio.Effects.Sdk", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n, Path.GetFileNameWithoutExtension(m.EntryAssembly), StringComparison.OrdinalIgnoreCase)))
            throw new FormatException("Duplicate dependency identity or privately bundled effect SDK.");
    }
    public static string ContentHash(PackageManifest manifest)
    {
        var normalized = manifest with { ContentHash = "", Files = new SortedDictionary<string, string>(manifest.Files.ToDictionary(), StringComparer.Ordinal) };
        return Hash(Encoding.UTF8.GetBytes(EffectJson.Serialize(normalized)));
    }
    public static PackageManifest ValidateDirectory(string root)
    {
        var m = ReadManifest(root);
        if (m.ContentHash != ContentHash(m)) throw new IOException("Effect package manifest hash mismatch.");
        var paths = EnumerateFiles(root).Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).Where(p => p != "effect.json").Take(MaxEntries + 1).ToArray();
        if (paths.Length > MaxEntries || !paths.Order().SequenceEqual(m.Files.Keys.Order()) || paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
            throw new IOException("Effect package file list mismatch.");
        long total = 0;
        foreach (var relative in paths)
        {
            string path = SafePath(root, relative); long length = new FileInfo(path).Length;
            if (length > MaxFileBytes || (total += length) > MaxTotalBytes || HashFile(path) != m.Files[relative]) throw new IOException("Effect package file validation failed: " + relative);
        }
        if (!m.Files.ContainsKey(m.EntryAssembly) || !m.Files.ContainsKey("schema.json") || m.Dependencies.Any(d => !m.Files.ContainsKey(d))) throw new IOException("Effect package is incomplete.");
        return m;
    }
    public static PackageManifest SealDirectory(string root, PackageManifest manifest)
    {
        var files = EnumerateFiles(root).Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).Where(p => p != "effect.json");
        var table = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string relative in files) table.Add(relative, HashFile(SafePath(root, relative)));
        var m = manifest with { Files = table, ContentHash = "" }; m = m with { ContentHash = ContentHash(m) };
        File.WriteAllText(Path.Combine(root, "effect.json"), EffectJson.Serialize(m)); ValidateDirectory(root); return m;
    }
    public static string Install(string archive, string store)
    {
        Directory.CreateDirectory(store); string stage = Path.Combine(store, ".install-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        try
        {
            using (var zip = ZipFile.OpenRead(archive))
            {
                if (zip.Entries.Count > MaxEntries) throw new IOException("Too many effect files.");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.EndsWith('/')) { SafePath(stage, entry.FullName.TrimEnd('/')); continue; }
                    if (!seen.Add(entry.FullName) || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || (entry.ExternalAttributes & 0x400) != 0 ||
                        entry.Length > MaxFileBytes || (total += entry.Length) > MaxTotalBytes) throw new IOException("Invalid effect archive entry.");
                    string dest = SafePath(stage, entry.FullName); Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    using var input = entry.Open(); using var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write);
                    var buffer = new byte[65536]; long actual = 0; int count;
                    while ((count = input.Read(buffer)) != 0)
                    { if ((actual += count) > entry.Length) throw new IOException("Invalid decompressed size."); output.Write(buffer, 0, count); }
                    if (actual != entry.Length) throw new IOException("Incomplete effect file."); output.Flush(true);
                }
            }
            var m = ValidateDirectory(stage); string versions = Path.Combine(store, m.PackageId, m.PackageVersion); Directory.CreateDirectory(versions);
            if (Directory.EnumerateDirectories(versions).Any(d => Path.GetFileName(d) != m.ContentHash)) throw new IOException("This effect version already has different content.");
            string target = Path.Combine(versions, m.ContentHash);
            if (Directory.Exists(target)) ValidateDirectory(target); else Directory.Move(stage, target);
            return target;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
    public static void Pack(string root, string destination)
    {
        ValidateDirectory(root); string tmp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { ZipFile.CreateFromDirectory(root, tmp, CompressionLevel.Optimal, false); File.Move(tmp, destination, true); }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}
