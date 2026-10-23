namespace SbStudio.Effects;

public interface IEffectModule
{
    void Define(EffectDefinitionBuilder definition);
    void Generate(EffectContext context, StoryboardBuilder storyboard);
}
public sealed record TimingInput(double Time, double BeatLength, int Meter, bool Kiai);
public sealed record HitObjectInput(double Time, double EndTime, Point2 Position, string Kind);
public sealed record BeatmapInput(int Mode, string Title, string Artist, string Difficulty,
    IReadOnlyList<TimingInput> Timing, IReadOnlyList<HitObjectInput> HitObjects);
public sealed record ImageSize(int Width, int Height);
public interface IEffectAssets
{
    byte[] Read(AssetReference asset);
    ImageSize ImageSize(AssetReference asset);
    AssetReference Write(string relativePath, ReadOnlySpan<byte> bytes);
}
public sealed class EffectContext
{
    public ParameterValues Parameters { get; }
    public double Duration { get; }
    public double PlacementTime { get; }
    public BeatmapInput Beatmap { get; }
    public IEffectAssets Assets { get; }
    public CancellationToken Cancellation { get; }
    public Point2 ScreenMin { get; } = new(-107, 0);
    public Point2 ScreenMax { get; } = new(747, 480);
    private readonly Action<string> log;
    public EffectContext(ParameterValues parameters, double duration, double placementTime, BeatmapInput beatmap,
        IEffectAssets assets, CancellationToken cancellation = default, Action<string>? log = null)
    {
        Guard.Finite(duration); Guard.Finite(placementTime);
        Parameters = parameters; Duration = duration; PlacementTime = placementTime; Beatmap = beatmap;
        Assets = assets; Cancellation = cancellation; this.log = log ?? (_ => { });
    }
    public double LocalTime(double absoluteTime) => absoluteTime - PlacementTime;
    public double AbsoluteTime(double localTime) => localTime + PlacementTime;
    public Point2 StoryboardPosition(HitObjectInput hit) => Beatmap.Mode == 0 ? new(hit.Position.X + 64, hit.Position.Y + 56) : hit.Position;
    public StableRandom Random(int seed) => new(seed);
    public void Log(string text) => log(text);
}

/// <summary>Version 1: SplitMix64 with an unsigned 32-bit seed and 53-bit double output.</summary>
public sealed class StableRandom
{
    public const int Version = 1;
    private ulong state;
    public StableRandom(int seed) => state = unchecked((uint)seed);
    public ulong NextUInt64()
    {
        unchecked
        {
            ulong z = state += 0x9E3779B97F4A7C15;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
            return z ^ (z >> 31);
        }
    }
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);
    public double Between(double min, double max)
    {
        Guard.Finite(min); Guard.Finite(max);
        if (min > max || !double.IsFinite(max - min)) throw new ArgumentOutOfRangeException(nameof(max));
        return min + (max - min) * NextDouble();
    }
    public int Next(int min, int max)
    {
        if (min >= max) throw new ArgumentOutOfRangeException(nameof(max));
        ulong range = (ulong)((long)max - min), limit = unchecked(0UL - range) % range, value;
        do { value = NextUInt64(); } while (value < limit);
        return (int)(min + (long)(value % range));
    }
}
