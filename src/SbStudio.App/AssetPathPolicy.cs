using SbStudio.Core;

namespace SbStudio.App;

/// <summary>Asset references accepted by the editor UI; the core format remains unrestricted.</summary>
internal static class AssetPathPolicy
{
    public static bool Allows(Project project, string path)
    {
        if (path.Length == 0) return true;
        string relative = path.Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split('/').Contains("..")) return false;
        try
        {
            string full = project.AssetFullPath(relative);
            if (project.ToAssetPath(full) == null) return false;
            // A folder link must not make an apparently local asset refer outside the project.
            for (string? current = full; current != null; current = Path.GetDirectoryName(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
                if (string.Equals(current, project.AssetsDir, StringComparison.OrdinalIgnoreCase)) break;
            }
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    public static string? InvalidPath(Project project, Storyboard storyboard) =>
        storyboard.AllObjects.Select(o => o.Path).FirstOrDefault(path => !Allows(project, path));

    public static string? InvalidPath(Project project) => InvalidPath(project, project.Storyboard)
        ?? project.MainFiles().FirstOrDefault(path => !Allows(project, path));

    public static string? InvalidPath(Project project, ExportOptions options) => InvalidPath(project)
        ?? (options.Assets == ExportAssetMode.All
            ? project.ScanAssets().Select(asset => asset.Path).FirstOrDefault(path => !Allows(project, path))
            : null);

    public static string Message(string path) => L.T("assets.pathInside", path);
}
