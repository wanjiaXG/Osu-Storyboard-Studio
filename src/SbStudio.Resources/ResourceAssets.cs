namespace SbStudio.Resources;

/// <summary>Built-in artwork, fonts and samples, read from this assembly without extracting files.</summary>
public static class ResourceAssets
{
    private const string Prefix = "sbasset://";
    public const string DefaultSkin = Prefix + "DefaultSkin";
    public const string Argon = Prefix + "Argon";
    private static readonly Dictionary<string, string> names = typeof(ResourceAssets).Assembly.GetManifestResourceNames()
        .ToDictionary(n => n, n => n, StringComparer.OrdinalIgnoreCase);

    public static bool IsEmbedded(string path) => path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static string Combine(string folder, string relative)
    {
        if (!IsEmbedded(folder)) return Path.Combine(folder, relative);
        string name = relative.Replace('\\', '/');
        if (Path.IsPathRooted(name) || name.Contains(':') || name.Split('/').Any(p => p.Length == 0 || p is "." or ".."))
            throw new ArgumentException("Invalid embedded asset path.", nameof(relative));
        return folder.TrimEnd('/', '\\') + "/" + name;
    }

    private static string? ResourceName(string path)
    {
        string name = path[Prefix.Length..].Replace('\\', '/');
        if (name.Contains(':') || name.Split('/').Any(p => p.Length == 0 || p is "." or "..")) return null;
        return names.GetValueOrDefault(name);
    }

    public static bool Exists(string path) => IsEmbedded(path) ? ResourceName(path) != null : File.Exists(path);

    public static Stream OpenRead(string path)
    {
        if (!IsEmbedded(path)) return File.OpenRead(path);
        string name = ResourceName(path) ?? throw new FileNotFoundException("Embedded asset not found.", path);
        return typeof(ResourceAssets).Assembly.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException("Embedded asset not found.", path);
    }

    public static byte[] ReadAllBytes(string path)
    {
        using var input = OpenRead(path);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    public static IEnumerable<string> ReadLines(string path)
    {
        using var input = OpenRead(path);
        using var reader = new StreamReader(input);
        while (reader.ReadLine() is { } line) yield return line;
    }
}
