using System.Text.Json;
using System.Text.Json.Serialization;

namespace SbStudio.Effects;

public static class EffectJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        MaxDepth = 64,
    };
    static EffectJson() => Options.MakeReadOnly(populateMissingResolver: true);
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, Options) ?? throw new FormatException("Empty effect data.");
}

public enum Layer { Background, Fail, Pass, Foreground, Overlay }
public enum Origin { TopLeft, Centre, CentreLeft, TopRight, BottomCentre, TopCentre, Custom, CentreRight, BottomLeft, BottomRight }
public enum AnimationLoop { LoopForever, LoopOnce }
public enum Ease
{
    Linear, Out, In, QuadIn, QuadOut, QuadInOut, CubicIn, CubicOut, CubicInOut,
    QuartIn, QuartOut, QuartInOut, QuintIn, QuintOut, QuintInOut, SineIn, SineOut, SineInOut,
    ExpoIn, ExpoOut, ExpoInOut, CircIn, CircOut, CircInOut, ElasticIn, ElasticOut,
    ElasticHalfOut, ElasticQuarterOut, ElasticInOut, BackIn, BackOut, BackInOut, BounceIn, BounceOut, BounceInOut,
}
public enum AssetKind { Project, Package, Generated }
public sealed record AssetReference(AssetKind Kind, string Path)
{
    public static AssetReference Project(string path) => new(AssetKind.Project, path);
    public static AssetReference Package(string path) => new(AssetKind.Package, path);
    public static AssetReference Generated(string path) => new(AssetKind.Generated, path);
    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || string.IsNullOrWhiteSpace(Path) || System.IO.Path.IsPathRooted(Path) ||
            Path.Contains(':') || Path.Contains('\\') || Path.Split('/').Any(p => p is "" or "." or "..") || Path.Any(char.IsControl))
            throw new ArgumentException("Assets must use a relative, slash-separated path.");
    }
}
public readonly record struct Point2(double X, double Y);
public readonly record struct ColorValue(byte R, byte G, byte B)
{
    public static ColorValue White => new(255, 255, 255);
}
public sealed record CurvePoint(double X, double Y);
public sealed record OutputCommand(string Kind, double Start, double End, IReadOnlyList<double> From, IReadOnlyList<double> To,
    Ease Ease = Ease.Linear, char Parameter = 'A', IReadOnlyList<CurvePoint>? Curve = null);
public sealed record OutputBlock(string Kind, double Start, double End, int Count, string? Trigger, int TriggerGroup,
    IReadOnlyList<OutputCommand> Commands);
public sealed record OutputNode(OutputCommand? Command = null, OutputBlock? Block = null);
public sealed record OutputSprite(AssetReference Asset, int? Group, Layer Layer, Origin Origin, Point2 Position,
    string? Name, int Frames, double FrameDelay, AnimationLoop Loop, bool FaceCamera,
    IReadOnlyList<OutputNode> Nodes, IReadOnlyList<OutputCommand> Depth);
public sealed record OutputGroup(int? Parent, string Name, Point2 Pivot, Point2 Position, IReadOnlyList<OutputCommand> Commands);
public sealed record StoryboardOutput(int FormatVersion, IReadOnlyList<OutputGroup> Groups, IReadOnlyList<OutputSprite> Sprites);

internal static class Guard
{
    internal static double Finite(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "A finite number is required.");
        return value;
    }
    internal static void Time(double start, double end)
    {
        Finite(start); Finite(end);
        if (start > end || start < int.MinValue + 1d || end > int.MaxValue - 1d)
            throw new ArgumentOutOfRangeException(nameof(end), "Invalid command time range.");
    }
    internal static void Point(Point2 value) { Finite(value.X); Finite(value.Y); }
}
