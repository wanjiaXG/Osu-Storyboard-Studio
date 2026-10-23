using System.Text.RegularExpressions;
using SbStudio.Core;
using SbStudio.Effects;

namespace SbStudio.App.Effects;

/// <summary>References shared by editor history, pending results and export snapshots.</summary>
internal static class EffectResultPins
{
    private static readonly object gate = new();
    private static readonly Dictionary<string, Dictionary<string, int>> projects = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, FileStream> sessions = new(StringComparer.OrdinalIgnoreCase);
    private static Mutex EnterProject(string folder)
    {
        string identity = PackageFiles.Hash(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(folder).ToUpperInvariant()));
        var mutex = new Mutex(false, "Local\\SbStudio.EffectCache." + identity);
        try { mutex.WaitOne(); }
        catch (AbandonedMutexException) { }
        return mutex;
    }
    private static string SessionFolder(string folder) => PackageFiles.SafePath(folder, "effects/leases");
    internal static IDisposable Pin(Project project, IEnumerable<string?> keys, CancellationToken token = default)
    {
        var valid = keys.Where(k => k != null && (k == "*" || Regex.IsMatch(k, "^effect-[a-f0-9]{64}$"))).Cast<string>().Distinct().ToArray();
        if (valid.Length == 0) return EmptyPin.Instance;
        var lease = new Lease(project.Folder, valid);
        lock (gate)
        {
            if (!projects.TryGetValue(project.Folder, out var counts))
            {
                using var mutex = EnterProject(project.Folder);
                try
                {
                    string directory = SessionFolder(project.Folder);
                    Directory.CreateDirectory(directory);
                    sessions.Add(project.Folder, new FileStream(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".lease"),
                        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.DeleteOnClose));
                    projects.Add(project.Folder, counts = new());
                }
                finally { mutex.ReleaseMutex(); }
            }
            foreach (string key in valid) counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        lease.Cancellation = token.Register(lease.Dispose);
        return lease;
    }
    private sealed class EmptyPin : IDisposable
    {
        internal static readonly EmptyPin Instance = new();
        public void Dispose() { }
    }
    internal static IReadOnlySet<string> Pinned(Project project)
    {
        lock (gate) return projects.TryGetValue(project.Folder, out var counts) ? counts.Keys.ToHashSet() : new HashSet<string>();
    }
    private sealed class Lease(string folder, string[] keys) : IDisposable
    {
        private int released;
        internal CancellationTokenRegistration Cancellation;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            Cancellation.Unregister();
            lock (gate)
            {
                if (!projects.TryGetValue(folder, out var counts)) return;
                foreach (string key in keys) if (--counts[key] == 0) counts.Remove(key);
                if (counts.Count == 0)
                {
                    projects.Remove(folder);
                    if (sessions.Remove(folder, out var session)) session.Dispose();
                }
            }
        }
    }
    // Persistent assets and fixed packages are deliberately outside cache collection.
    internal static void Collect(Project project, TimeSpan? retention = null, long maxBytes = 1024L * 1024 * 1024)
    {
        try
        {
            string root = PackageFiles.SafePath(project.Folder, "effects/results");
            if (!Directory.Exists(root)) return;
            var saved = new HashSet<string>();
            foreach (string file in new[] { project.StoryboardFile, project.StoryboardFile + ".bak", Path.Combine(project.Folder, "storyboard.autosave.osb") })
            {
                if (!File.Exists(file)) continue;
                // Reading only metadata avoids materializing a second large storyboard.
                foreach (string line in File.ReadLines(file))
                    if (line.TrimStart().StartsWith("//@", StringComparison.Ordinal))
                        foreach (Match match in Regex.Matches(line, "effect-[a-f0-9]{64}")) saved.Add(match.Value);
            }
            var candidates = new List<(string Directory, string File, string Key, long Size, DateTime Stamp)>();
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                string name = Path.GetFileName(directory);
                if (!Regex.IsMatch(name, "^[a-f0-9]{64}$")) continue;
                string file = PackageFiles.SafePath(root, name + "/result.json");
                if (!File.Exists(file)) continue;
                var info = new FileInfo(file);
                candidates.Add((directory, file, "effect-" + name, info.Length, info.LastWriteTimeUtc));
            }
            long total = candidates.Sum(c => c.Size);
            DateTime cutoff = DateTime.UtcNow - (retention ?? TimeSpan.FromDays(1));
            lock (gate)
            {
                using var mutex = EnterProject(project.Folder);
                try
                {
                // Another editor's in-memory undo/export references are private to that process.
                // Keep every result while it holds a session lease; an abandoned lease is removable.
                string leases = SessionFolder(project.Folder);
                if (Directory.Exists(leases))
                    foreach (string file in Directory.EnumerateFiles(leases, "*.lease"))
                    {
                        if (sessions.TryGetValue(project.Folder, out var own) && own.Name == file) continue;
                        if (!Regex.IsMatch(Path.GetFileName(file), "^[a-f0-9]{32}\\.lease$")) continue;
                        string safe = PackageFiles.SafePath(leases, Path.GetFileName(file));
                        try { using var unused = new FileStream(safe, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
                        catch (IOException) { return; }
                    }
                var pinned = Pinned(project);
                if (pinned.Contains("*")) return;
                foreach (var c in candidates.OrderBy(c => c.Stamp))
                {
                    if (saved.Contains(c.Key) || pinned.Contains(c.Key) || c.Stamp >= cutoff) continue;
                    if (total <= maxBytes) break;
                    // Only delete the known cache file; unexpected contents are left intact.
                    File.Delete(c.File); total -= c.Size;
                    if (!Directory.EnumerateFileSystemEntries(c.Directory).Any()) Directory.Delete(c.Directory);
                }
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { AppSettings.Log("Effect cache cleanup: " + ex.Message); }
    }
}
