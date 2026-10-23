namespace SbStudio.Effects.Host;

internal static class CompilationCache
{
    private const string Marker = "SbStudio compilation v1";
    private sealed record Entry(string DllHash, string PdbHash, IReadOnlyList<EffectDiagnostic> Diagnostics);
    private static readonly string compilerHash = PackageFiles.HashFile(typeof(CompilationCache).Assembly.Location) +
        PackageFiles.HashFile(typeof(IEffectModule).Assembly.Location);

    private static string DirectoryOf(string cache, string revision) =>
        PackageFiles.SafePath(cache, PackageFiles.Hash(System.Text.Encoding.UTF8.GetBytes(compilerHash + revision)));

    internal static bool Restore(string cache, string revision, string assembly, out IReadOnlyList<EffectDiagnostic> diagnostics)
    {
        diagnostics = [];
        if (cache.Length == 0) return false;
        try
        {
            string root = DirectoryOf(cache, revision);
            if (!File.Exists(Path.Combine(root, "compilation.json"))) return false;
            var entry = EffectJson.Deserialize<Entry>(System.Text.Encoding.UTF8.GetString(ReadBounded(root, "compilation.json", 65536)));
            if (entry == null || entry.Diagnostics == null) return false;
            byte[] dll = ReadBounded(root, "module.dll", PackageFiles.MaxFileBytes), pdb = ReadBounded(root, "module.pdb", PackageFiles.MaxFileBytes);
            if (PackageFiles.Hash(dll) != entry.DllHash || PackageFiles.Hash(pdb) != entry.PdbHash) return false;
            File.WriteAllBytes(assembly, dll); File.WriteAllBytes(Path.ChangeExtension(assembly, ".pdb"), pdb);
            File.SetLastWriteTimeUtc(Path.Combine(root, "compilation.json"), DateTime.UtcNow);
            diagnostics = entry.Diagnostics;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException) { return false; }
    }
    private static byte[] ReadBounded(string root, string name, long limit)
    {
        using var file = File.OpenRead(PackageFiles.SafePath(root, name));
        if (file.Length > limit) throw new IOException("Compilation cache entry exceeds its limit.");
        byte[] bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        return bytes;
    }

    internal static void Save(string cache, string revision, byte[] dll, byte[] pdb, IReadOnlyList<EffectDiagnostic> diagnostics)
    {
        if (cache.Length == 0) return;
        try
        {
            string root = DirectoryOf(cache, revision);
            Directory.CreateDirectory(root);
            Write(Path.Combine(root, "module.dll"), dll); Write(Path.Combine(root, "module.pdb"), pdb);
            Write(Path.Combine(root, ".owner"), System.Text.Encoding.UTF8.GetBytes(Marker));
            Write(Path.Combine(root, "compilation.json"), System.Text.Encoding.UTF8.GetBytes(
                EffectJson.Serialize(new Entry(PackageFiles.Hash(dll), PackageFiles.Hash(pdb), diagnostics))));
            Prune(cache);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static void Write(string path, byte[] bytes)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write)) { file.Write(bytes); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void Prune(string cache)
    {
        var entries = new List<(string Root, DateTime Stamp, long Size)>();
        foreach (string directory in Directory.EnumerateDirectories(cache))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(directory), "^[a-f0-9]{64}$")) continue;
            string owner = PackageFiles.SafePath(cache, Path.GetFileName(directory) + "/.owner");
            if (!File.Exists(owner) || File.ReadAllText(owner) != Marker) continue;
            var files = PackageFiles.EnumerateFiles(directory).Select(p => new FileInfo(p)).ToArray();
            entries.Add((directory, File.GetLastWriteTimeUtc(Path.Combine(directory, "compilation.json")), files.Sum(f => f.Length)));
        }
        long total = entries.Sum(e => e.Size);
        foreach (var entry in entries.OrderBy(e => e.Stamp))
        {
            if (total <= 256L * 1024 * 1024 || entry.Stamp > DateTime.UtcNow.AddMinutes(-5)) continue;
            // Consumers copy and verify cache bytes before loading from their private request directory.
            try { Directory.Delete(entry.Root, true); total -= entry.Size; }
            catch (IOException) { }
        }
    }
}
