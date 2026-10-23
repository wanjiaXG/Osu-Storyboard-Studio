using System.Text.RegularExpressions;
using SbStudio.Effects;

namespace SbStudio.App.Effects;

/// <summary>Owns a request directory until its complete output has been collected.</summary>
internal sealed class EffectWorkDirectory : IDisposable
{
    private const string Marker = "SbStudio effect work v1";
    internal string Path { get; }
    private readonly FileStream lease;

    internal EffectWorkDirectory(string parent)
    {
        Directory.CreateDirectory(parent);
        Cleanup(parent);
        Path = System.IO.Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        lease = new(System.IO.Path.Combine(Path, ".lease"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        File.WriteAllText(System.IO.Path.Combine(Path, ".owner"), Marker);
    }

    internal static void Cleanup(string parent)
    {
        if (!Directory.Exists(parent)) return;
        foreach (string directory in Directory.EnumerateDirectories(parent))
        {
            if (!Regex.IsMatch(System.IO.Path.GetFileName(directory), "^[a-f0-9]{32}$")) continue;
            try
            {
                string marker = PackageFiles.SafePath(parent, System.IO.Path.GetFileName(directory) + "/.owner");
                if (!File.Exists(marker) || File.ReadAllText(marker) != Marker) continue;
                // A separate editor or an export can still own the directory.
                using (new FileStream(System.IO.Path.Combine(directory, ".lease"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                // Inspect every child before recursive deletion; never follow junctions out of the work root.
                _ = PackageFiles.EnumerateFiles(directory).ToArray();
                Directory.Delete(directory, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
    }

    public void Dispose()
    {
        lease.Dispose();
        Cleanup(System.IO.Path.GetDirectoryName(Path)!);
    }
}
