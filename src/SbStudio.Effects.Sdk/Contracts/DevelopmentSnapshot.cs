using System.Text;

namespace SbStudio.Effects;

/// <summary>A stable set of source inputs shared by compilation and worker isolation.</summary>
public sealed record DevelopmentSnapshot(string Revision, IReadOnlyDictionary<string, byte[]> Files)
{
    public static DevelopmentSnapshot Capture(string source, CancellationToken token = default)
    {
        var paths = Enumerate(source).Order(StringComparer.Ordinal).ToArray();
        if (paths.Length > PackageFiles.MaxEntries) throw new IOException("Too many development files.");
        var snapshot = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        long size = 0;
        foreach (string relative in paths)
        {
            token.ThrowIfCancellationRequested();
            string path = PackageFiles.SafePath(source, relative);
            if (new FileInfo(path).Length > PackageFiles.MaxFileBytes) throw new IOException("Development file is too large.");
            byte[] bytes = File.ReadAllBytes(path);
            if ((size += bytes.Length) > PackageFiles.MaxTotalBytes) throw new IOException("Development source is too large.");
            snapshot.Add(relative, bytes);
        }
        if (!paths.SequenceEqual(Enumerate(source).Order(StringComparer.Ordinal)) ||
            snapshot.Any(p => PackageFiles.HashFile(PackageFiles.SafePath(source, p.Key)) != PackageFiles.Hash(p.Value)))
            throw new IOException("Development files changed while capturing input; save again or regenerate.");
        string revision = PackageFiles.Hash(Encoding.UTF8.GetBytes("compiler-5.0.0-csharp14-sdk1\n" +
            string.Join('\n', snapshot.Select(p => p.Key + ":" + PackageFiles.Hash(p.Value)))));
        return new(revision, snapshot);
    }

    private static IEnumerable<string> Enumerate(string root, bool includeAll = false)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
        {
            string name = Path.GetFileName(entry);
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) continue;
            if (Directory.Exists(entry))
            {
                if (name is "bin" or "obj" or ".build" or ".git" or ".vs") continue;
                foreach (string child in Enumerate(entry, includeAll || name is "assets" or "lib" or "presets"))
                    yield return name + "/" + child;
            }
            else if (includeAll || name == "effect.json" || name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                yield return name;
        }
    }
}
