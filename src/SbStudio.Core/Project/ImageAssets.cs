namespace SbStudio.Core;

/// <summary>Image lookup as in osu!lazer's TextureLoaderStore / ResourceStore: the name itself,
/// then the name with .png and .jpg appended. A dot in the name does not suppress the fallback.
/// The script keeps its original name; copying assets uses the resolved file name.</summary>
public static class ImageAssets
{
    public static string? Find(string path, Func<string, bool> exists)
    {
        if (path.Length == 0) return null;
        if (exists(path)) return path;
        if (exists(path + ".png")) return path + ".png";
        if (exists(path + ".jpg")) return path + ".jpg";
        return null;
    }
}
