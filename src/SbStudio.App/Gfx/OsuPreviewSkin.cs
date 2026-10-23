using System.Globalization;
using System.Numerics;
using StbImageSharp;
using SbStudio.App.Audio;
using SbStudio.Resources;

namespace SbStudio.App.Gfx;

/// <summary>Legacy skin images and configuration, with per-asset fallback to the bundled skin.</summary>
internal sealed class OsuPreviewSkin
{
    internal sealed record Bitmap(int Width, int Height, float Density, byte[] Pixels)
    {
        public Vector2 Size => new(Width / Density, Height / Density);
    }

    private readonly Dictionary<string, string?> soundPaths = new(StringComparer.Ordinal);
    private bool cacheSoundLookups = true;
    internal bool CacheSoundLookups
    {
        get => cacheSoundLookups;
        set { cacheSoundLookups = value; if (fallback != null) fallback.CacheSoundLookups = value; }
    }
    private readonly string folder;
    private readonly OsuPreviewSkin? fallback;
    private readonly bool beatmap;
    private readonly bool defaultSkin;
    private readonly OsuPreviewSkin? beatmapMania;
    private readonly Dictionary<string, string> config = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, Dictionary<string, string>> mania = new();

    public bool HasManiaConfiguration(int keys) => mania.ContainsKey(keys) || (beatmapMania?.mania.ContainsKey(keys) ?? false);
    public string ManiaValue(int keys, string key, string defaultValue) => beatmapMania?.ManiaValue(keys, key, defaultValue)
        ?? (mania.TryGetValue(keys, out var values) && values.TryGetValue(key, out var value) ? value : fallback?.ManiaValue(keys, key, defaultValue) ?? defaultValue);
    public float ManiaNumber(int keys, string key, float defaultValue, int column = -1)
    {
        string[] values = ManiaValue(keys, key, "").Split(',');
        int index = column < 0 ? 0 : column;
        return index < values.Length && float.TryParse(values[index], CultureInfo.InvariantCulture, out float value) && float.IsFinite(value) ? value : defaultValue;
    }
    public Vector4 ManiaColour(int keys, string key, Vector4 defaultValue)
    {
        string[] values = ManiaValue(keys, key, "").Split(',');
        if (values.Length < 3) return defaultValue;
        var colour = new float[4];
        colour[3] = 1;
        for (int i = 0; i < Math.Min(4, values.Length); i++)
            if (float.TryParse(values[i], CultureInfo.InvariantCulture, out float value)) colour[i] = Math.Clamp(value / 255, 0, 1);
            else return defaultValue;
        return new(colour[0], colour[1], colour[2], colour[3]);
    }
    public Vector3? CustomColour(string name) => Colour("Colours." + name);
    public static string ManiaColumnType(int keys, int column)
    {
        int stageKeys = keys > 10 ? keys / 2 : keys;
        int localColumn = column % stageKeys;
        return stageKeys % 2 == 1 && localColumn == stageKeys / 2 ? "S"
            : Math.Min(localColumn, stageKeys - 1 - localColumn) % 2 == 0 ? "1" : "2";
    }
    public string NumberPrefix => Get("Fonts.HitCirclePrefix", "default");
    public bool LayeredHitSounds => Get("General.LayeredHitSounds", "1") != "0";
    public bool ArgonSounds { get; }
    public bool BeatmapSamples => beatmap;
    public bool HasBeatmapImage(string name) => beatmap && HasImage(name);
    public float Version => Number("General.Version", 1);

    // LegacyBeatmapSkin disables @2x and mania configuration lookups, and reads .osu colours.
    public bool HasImage(string name)
    {
        if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return ImagePath(name, false) != null || ImagePath(name + "-0", false) != null || ImagePath(name + "0", false) != null
            || !beatmap && (ImagePath(name, true) != null || ImagePath(name + "-0", true) != null || ImagePath(name + "0", true) != null);
    }
    private string? ImagePath(string name, bool highResolution)
    {
        if (Path.IsPathRooted(name) || name.Contains(':')) return null;
        if (ResourceAssets.IsEmbedded(folder))
        {
            if (name.Replace('\\', '/').Split('/').Any(p => p.Length == 0 || p is "." or "..")) return null;
            string resource = ResourceAssets.Combine(folder, name + (highResolution ? "@2x.png" : ".png"));
            return ResourceAssets.Exists(resource) ? resource : null;
        }
        string path = Path.GetFullPath(Path.Combine(folder, name.Replace('/', Path.DirectorySeparatorChar) + (highResolution ? "@2x.png" : ".png")));
        return path.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
    }

    public string? Sound(string name)
    {
        return OwnSound(name) ?? fallback?.Sound(name);
    }
    private string? OwnSound(string name)
    {
        if (!CacheSoundLookups) return FindOwnSound(name);
        if (!soundPaths.TryGetValue(name, out var path)) soundPaths[name] = path = FindOwnSound(name);
        return path;
    }
    private string? FindOwnSound(string name)
    {
        // Named beatmap samples may be in subfolders, but cannot escape the skin/resource folder.
        if (Path.IsPathRooted(name) || name.Contains(':') || name is "." or "..") return null;
        foreach (string extension in new[] { ".wav", ".ogg", ".mp3" })
        {
            if (ResourceAssets.IsEmbedded(folder))
            {
                if (name.Replace('\\', '/').Split('/').Any(p => p.Length == 0 || p is "." or "..")) return null;
                string resource = ResourceAssets.Combine(folder, Path.HasExtension(name) ? name : name + extension);
                if (ResourceAssets.Exists(resource)) return resource;
                continue;
            }
            string path = Path.GetFullPath(Path.Combine(folder, Path.HasExtension(name) ? name : name + extension));
            if (!path.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
            if (File.Exists(path)) return path;
        }
        return null;
    }
    public string? BeatmapSample(string name, int index)
    {
        if (!beatmap) return null;
        string suffix = index > 1 ? index.ToString(CultureInfo.InvariantCulture) : "";
        // LegacyBeatmapSkin may NOT fall back from numbered banks to its own unnumbered bank.
        return OwnSound(name + suffix)
            ?? (name.StartsWith("taiko-", StringComparison.Ordinal) ? OwnSound(name[6..] + suffix) : null)
            ?? OwnSound(name[(name.LastIndexOf('-') + 1)..]);
    }
    public string? SampleSound(string name, int index) => beatmap ? BeatmapSample(name, index) ?? fallback?.SampleSound(name, index) : Sound(name);
    public HitSoundSource SourceOf(string? file)
    {
        if (file == null) return HitSoundSource.Missing;
        if (ResourceAssets.IsEmbedded(file)) return HitSoundSource.Default;
        if (ResourceAssets.IsEmbedded(folder)) return fallback?.SourceOf(file) ?? HitSoundSource.Skin;
        if (file.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return beatmap ? HitSoundSource.Beatmap : defaultSkin ? HitSoundSource.Default : HitSoundSource.Skin;
        return fallback?.SourceOf(file) ?? HitSoundSource.Skin;
    }
    public float NumberOverlap => Number("Fonts.HitCircleOverlap", -2);
    public int BallFrames => Math.Clamp((int)Number("General.SliderBallFrames", 10), 1, 256);
    public bool TintBall => Get("General.AllowSliderBallTint", "1") == "1";
    public bool OverlayAboveNumber => Get("General.HitCircleOverlayAboveNumber", "1") == "1";
    public float AnimationRate => Number("General.AnimationFramerate", 0);
    public Vector3? SliderBorder => Colour("Colours.SliderBorder");
    public Vector3? SliderTrack => Colour("Colours.SliderTrackOverride");
    public Vector3[] ComboColours => config.ContainsKey("Colours.Combo1")
        ? Enumerable.Range(1, 8).Select(i => Colour("Colours.Combo" + i, false)).TakeWhile(c => c != null).Select(c => c!.Value).ToArray()
        : fallback?.ComboColours ?? Array.Empty<Vector3>();
    private Vector3? Colour(string key, bool inherit = true)
    {
        var parts = (inherit ? Get(key, "") : config.GetValueOrDefault(key, "")).Split(',');
        if (parts.Length != 3) return null;
        var result = new float[3];
        for (int i = 0; i < 3; i++)
            if (!float.TryParse(parts[i], CultureInfo.InvariantCulture, out float value)) return null;
            else result[i] = Math.Clamp(value, 0, 255) / 255;
        return new(result[0], result[1], result[2]);
    }
    public Vector3 BallColour
    {
        get
        {
            var parts = Get("Colours.SliderBall", "255,255,255").Split(',');
            if (parts.Length != 3) return Vector3.One;
            var values = parts.Select(p => float.TryParse(p, CultureInfo.InvariantCulture, out float v) ? Math.Clamp(v, 0, 255) / 255 : 1).ToArray();
            return new(values[0], values[1], values[2]);
        }
    }

    public OsuPreviewSkin(string folder, OsuPreviewSkin? fallback = null, bool argonSounds = false, string? beatmapFile = null, bool defaultSkin = false)
    {
        this.folder = folder;
        this.fallback = fallback;
        this.defaultSkin = defaultSkin;
        beatmap = beatmapFile != null;
        // Preview legacy mania follows skin.ini positioning, including a beatmap-provided [Mania]
        // section. Other beatmap configuration/colours continue to come from the .osu file.
        beatmapMania = beatmap ? new OsuPreviewSkin(folder, fallback) : null;
        ArgonSounds = argonSounds || (beatmap && (fallback?.ArgonSounds ?? false));
        string ini = beatmapFile ?? ResourceAssets.Combine(folder, "skin.ini");
        if (!ResourceAssets.Exists(ini)) return;
        string section = "";
        Dictionary<string, string>? maniaSection = null;
        foreach (string raw in ResourceAssets.ReadLines(ini))
        {
            string line = raw.Split(new[] { "//" }, 2, StringSplitOptions.None)[0].Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                if (beatmap && section != "Colours") section = "";
                maniaSection = section.Equals("Mania", StringComparison.OrdinalIgnoreCase) ? new(StringComparer.OrdinalIgnoreCase) : null;
            }
            else if (line.IndexOf(':') is int split && split > 0)
            {
                if (beatmap && section.Length == 0) continue;
                string key = line[..split].Trim(), value = line[(split + 1)..].Trim();
                if (maniaSection != null)
                {
                    maniaSection[key] = value;
                    if (key.Equals("Keys", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int keys))
                        mania.TryAdd(keys, maniaSection);
                }
                else config[section + "." + key] = value;
            }
        }
    }

    private string Get(string key, string defaultValue) => config.TryGetValue(key, out string? value) ? value : fallback?.Get(key, defaultValue) ?? defaultValue;
    private float Number(string key, float fallback) => float.TryParse(Get(key, ""), CultureInfo.InvariantCulture, out float value) && float.IsFinite(value) ? value : fallback;

    public Bitmap? Read(string name)
    {
        if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        string? path = beatmap ? null : ImagePath(name, true);
        float density = path != null ? 2 : 1;
        path ??= ImagePath(name, false);
        if (path == null) return fallback?.Read(name);
        ImageResult image;
        try { image = ImageResult.FromMemory(ResourceAssets.ReadAllBytes(path), ColorComponents.RedGreenBlueAlpha); }
        catch (Exception) { return fallback?.Read(name); }
        for (int i = 0; i < image.Data.Length; i += 4)
        {
            int alpha = image.Data[i + 3];
            for (int channel = 0; channel < 3; channel++) image.Data[i + channel] = (byte)(image.Data[i + channel] * alpha / 255);
        }
        return new(image.Width, image.Height, density, image.Data);
    }

    public Bitmap? ReadDigit(int digit) => Read(NumberPrefix + "-" + digit) ?? fallback?.ReadDigit(digit);

    /// <summary>Skin animations may use dash-separated frame names or the legacy sliderb0 convention.</summary>
    public Bitmap[] ReadAnimation(string name, int limit = 256)
    {
        if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        // A static custom image takes precedence over the default skin's animation.
        bool Has(string n) => ImagePath(n, false) != null || (!beatmap && ImagePath(n, true) != null);
        if (Has(name)) return Read(name) is { } image ? new[] { image } : fallback?.ReadAnimation(name, limit) ?? Array.Empty<Bitmap>();
        string prefix = Has(name + "-0") ? name + "-" : name;
        if (!Has(prefix + "0")) return fallback?.ReadAnimation(name, limit) ?? Array.Empty<Bitmap>();
        var frames = new List<Bitmap>();
        for (int i = 0; i < limit && Has(prefix + i); i++)
            if (Read(prefix + i) is { } frame) frames.Add(frame);
        return frames.ToArray();
    }

    public static int AnimationFrame(double elapsed, double fps, int count) =>
        (int)(Math.Floor(Math.Max(0, elapsed) * (fps > 0 ? fps : count) / 1000) % count);

    public static int BallFrame(double timeSinceAppear, double velocity, int count)
    {
        // Equivalent to lazer's max(0.15 / velocity * 1000 / 60, 1000 / 60) frame delay.
        // Multiplying by FPS avoids dropping the preceding frame at exact 60 FPS boundaries.
        double fps = Math.Min(60, Math.Max(velocity, 0.000001) * 60 / 0.15);
        return (int)(Math.Floor(Math.Max(0, timeSinceAppear) * fps / 1000) % count);
    }
}
