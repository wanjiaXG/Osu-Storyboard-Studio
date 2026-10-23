using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace SbStudio.Core.Updates;

public sealed record PackageFile(string Path, string Sha256);
public sealed record PackageManifest(string Version, PackageFile[] Files);

/// <summary>Our update package owns only application files. Other portable-directory contents are never swept.</summary>
public static class PortablePackage
{
    public const string ManifestName = "portable-manifest.json";
    public const string AppExe = "OsuStoryboardStudio.exe";
    public const string UpdaterExe = "updater/OsuStoryboardStudio.Updater.exe";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static string MutexName(string directory) => "Local\\OsuStoryboardStudio-" +
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(directory).TrimEnd('\\', '/').ToUpperInvariant())));

    public static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Contains(':') ||
            relative.Split('/').Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') ||
                p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                System.Text.RegularExpressions.Regex.IsMatch(p.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            throw new InvalidDataException("Unsafe update path: " + relative);
        root = Path.GetFullPath(root);
        string result = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update path escapes its directory.");
        // Refuse junctions and symbolic links both at the destination and at all ancestor directories.
        for (string? current = result; current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Update paths cannot traverse links: " + current);
        return result;
    }

    public static bool IsApplicationFile(string path)
    {
        if (path.StartsWith("runtimes/", StringComparison.Ordinal) || path.StartsWith("Assets/", StringComparison.Ordinal) ||
            path.StartsWith("Skins/default/", StringComparison.Ordinal) || path == "Skins/README.txt" ||
            path.StartsWith("lang/", StringComparison.Ordinal) || path == UpdaterExe ||
            path.StartsWith("effects/host/", StringComparison.Ordinal) || path.StartsWith("effects/builtin/", StringComparison.Ordinal) ||
            path == "effects/authoring.md" || path == "SbStudio.Effects.Sdk.xml") return true;
        return !path.Contains('/') && (path.StartsWith("OsuStoryboardStudio.", StringComparison.Ordinal) ||
            path.StartsWith("SbStudio.Core.", StringComparison.Ordinal) ||
            path == "createdump.exe" ||
            new[] { ".dll", ".pdb", ".lib", ".exp" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
    }

    public static PackageManifest ReadManifest(string json)
    {
        var manifest = JsonSerializer.Deserialize<PackageManifest>(json, JsonOptions) ?? throw new InvalidDataException("Missing manifest.");
        GithubUpdates.ParseVersion(manifest.Version);
        if (manifest.Files == null || manifest.Files.Length is < 2 or > 20000) throw new InvalidDataException("Invalid manifest file count.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            _ = SafePath(Path.GetTempPath(), file.Path);
            if (!IsApplicationFile(file.Path) || !names.Add(file.Path) ||
                !System.Text.RegularExpressions.Regex.IsMatch(file.Sha256 ?? "", "^[a-fA-F0-9]{64}$"))
                throw new InvalidDataException("Invalid manifest entry: " + file.Path);
        }
        if (!names.Contains(AppExe) || !names.Contains(UpdaterExe)) throw new InvalidDataException("Incomplete portable package.");
        return manifest;
    }

    public static PackageManifest Extract(string zipFile, string destination, string version, CancellationToken cancel = default)
    {
        using var zip = ZipFile.OpenRead(zipFile);
        if (zip.Entries.Count > 25000) throw new InvalidDataException("Too many archive entries.");
        var entry = zip.GetEntry(ManifestName) ?? throw new InvalidDataException("Missing portable manifest.");
        if (entry.Length > 8 * 1024 * 1024) throw new InvalidDataException("Manifest is too large.");
        using var reader = new StreamReader(entry.Open());
        string json = reader.ReadToEnd();
        var manifest = ReadManifest(json);
        if (GithubUpdates.ParseVersion(manifest.Version) != GithubUpdates.ParseVersion(version))
            throw new InvalidDataException("Package version differs from the selected release.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long size = 0;
        foreach (var item in zip.Entries)
        {
            cancel.ThrowIfCancellationRequested();
            string path = item.FullName;
            bool directory = path.EndsWith('/');
            SafePath(destination, directory ? path[..^1] : path);
            if (((item.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("Archive contains a symbolic link.");
            if (directory) continue;
            if (!entries.TryAdd(path, item)) throw new InvalidDataException("Duplicate archive path.");
            size = checked(size + item.Length);
            if (size > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("Expanded update is too large.");
        }
        if (entries.Count != manifest.Files.Length + 1) throw new InvalidDataException("Archive contains unlisted files.");
        Directory.CreateDirectory(destination);
        foreach (var file in manifest.Files)
        {
            cancel.ThrowIfCancellationRequested();
            if (!entries.TryGetValue(file.Path, out var source)) throw new InvalidDataException("Missing package file: " + file.Path);
            string output = SafePath(destination, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            source.ExtractToFile(output, false);
            if (!FileHash(output).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Package file checksum mismatch: " + file.Path);
        }
        File.WriteAllText(SafePath(destination, ManifestName), json);
        return manifest;
    }

    private sealed record Change(string Path, bool Existed);
    private sealed record Journal(string Backup, Change[] Changes);
    public const string JournalName = ".portable-update-journal.json";

    /// <summary>Durably journal before changing any installed file. Recovery can also run after power loss.</summary>
    public static void Install(string prepared, string target, string backup, Action<string>? beforeCopy = null)
    {
        var manifest = ReadManifest(File.ReadAllText(SafePath(prepared, ManifestName)));
        foreach (var file in manifest.Files)
            if (!FileHash(SafePath(prepared, file.Path)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Prepared update checksum mismatch: " + file.Path);
        if (File.Exists(SafePath(target, JournalName))) throw new IOException("An interrupted update needs recovery first.");
        var files = manifest.Files.Select(f => f.Path).Append(ManifestName).ToArray();
        var changes = files.Select(f => new Change(f, File.Exists(SafePath(target, f)))).ToArray();
        Directory.CreateDirectory(backup);
        // Back up everything before the journal is committed, so a failed backup never changes the app.
        foreach (var change in changes.Where(c => c.Existed))
        {
            string saved = SafePath(backup, change.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
            File.Copy(SafePath(target, change.Path), saved, false);
            using var durableBackup = new FileStream(saved, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            durableBackup.Flush(true);
        }
        using (var journal = new FileStream(SafePath(target, JournalName), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(journal, new Journal(Path.GetFullPath(backup), changes), JsonOptions);
            journal.Flush(true);
        }
        try
        {
            foreach (var change in changes)
            {
                beforeCopy?.Invoke(change.Path);
                string output = SafePath(target, change.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.Copy(SafePath(prepared, change.Path), output, true);
            }
            File.Delete(SafePath(target, JournalName));
        }
        catch
        {
            Recover(target);
            throw;
        }
    }

    public static void Recover(string target)
    {
        string journalPath = SafePath(target, JournalName);
        if (!File.Exists(journalPath)) return;
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(journalPath), JsonOptions)
            ?? throw new InvalidDataException("Unreadable update journal.");
        var failures = new List<Exception>();
        foreach (var change in journal.Changes)
        {
            try
            {
                if (change.Path != ManifestName && !IsApplicationFile(change.Path)) throw new InvalidDataException("Invalid recovery path.");
                string output = SafePath(target, change.Path);
                if (change.Existed)
                {
                    string saved = SafePath(journal.Backup, change.Path);
                    // An unchanged locked DLL needs no replacement and must not prevent the other restores.
                    if (!File.Exists(output) || FileHash(output) != FileHash(saved)) File.Copy(saved, output, true);
                }
                else if (File.Exists(output)) File.Delete(output);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures.Add(ex); }
        }
        if (failures.Count > 0) throw new AggregateException("Some files could not be restored; keep the journal and backup.", failures);
        File.Delete(journalPath);
    }
}
