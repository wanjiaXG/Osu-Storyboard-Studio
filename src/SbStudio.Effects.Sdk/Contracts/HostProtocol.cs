using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace SbStudio.Effects;

public sealed record EffectEntry(string EffectId, string EntryType);
public sealed record EffectRequestIdentity(string ProjectSessionId = "", Guid InstanceGuid = default, long Generation = 0,
    string CodeRevision = "", string DesiredInputKey = "");
public enum HostMessageKind { Handshake, Progress, Diagnostic, Completed, Failed }
public sealed record PackageManifest
{
    public int FormatVersion { get; init; } = 1;
    public string PackageId { get; init; } = "";
    public string PackageVersion { get; init; } = "1.0.0";
    public int SdkMajor { get; init; } = 1;
    public string MinHostVersion { get; init; } = "1.0.0";
    public string EntryAssembly { get; init; } = "lib/Effect.dll";
    public IReadOnlyList<EffectEntry> Effects { get; init; } = [];
    public IReadOnlyList<string> Dependencies { get; init; } = [];
    public IReadOnlyDictionary<string, string> Files { get; init; } = new Dictionary<string, string>();
    public string ContentHash { get; init; } = "";
}
public sealed record HostRequest
{
    public int ProtocolVersion { get; init; } = 1;
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
    public EffectRequestIdentity Identity { get; init; } = new();
    public string Operation { get; init; } = "Generate";
    public string SourceDirectory { get; init; } = "";
    public bool Development { get; init; }
    public string EffectId { get; init; } = "";
    public string ProjectAssets { get; init; } = "";
    public string WorkDirectory { get; init; } = "";
    public string BuildDirectory { get; init; } = "";
    public string OutputPackage { get; init; } = "";
    public Dictionary<string, JsonElement> Parameters { get; init; } = new();
    public double Duration { get; init; }
    public double PlacementTime { get; init; }
    public BeatmapInput Beatmap { get; init; } = new(0, "", "", "", [], []);
}
public sealed record EffectDiagnostic(string Severity, string Message, string? File = null, int Line = 0, int Column = 0);
public sealed record HostResponse
{
    public int ProtocolVersion { get; init; } = 1;
    public string RequestId { get; init; } = "";
    public EffectRequestIdentity Identity { get; init; } = new();
    public HostMessageKind Kind { get; init; } = HostMessageKind.Completed;
    public string Stage { get; init; } = "";
    public double Progress { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? OutputFile { get; init; }
    public string? OutputHash { get; init; }
    public string? CodeRevision { get; init; }
    public string? PackageDirectory { get; init; }
    public IReadOnlyList<EffectDefinition> Definitions { get; init; } = [];
    public IReadOnlyList<EffectDiagnostic> Diagnostics { get; init; } = [];
    public IReadOnlyDictionary<string, string> ReadAssets { get; init; } = new Dictionary<string, string>();
    public double ElapsedMs { get; init; }
}
public static class HostWire
{
    public const int MaxMessageBytes = 16 * 1024 * 1024;
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token = default)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(EffectJson.Serialize(value));
        if (bytes.Length > MaxMessageBytes) throw new IOException("Effect control message exceeds limit.");
        byte[] size = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, bytes.Length);
        await stream.WriteAsync(size, token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token = default)
    {
        byte[] size = new byte[4]; await stream.ReadExactlyAsync(size, token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(size);
        if (length < 1 || length > MaxMessageBytes) throw new IOException("Invalid effect control message size.");
        byte[] bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token);
        return EffectJson.Deserialize<T>(Encoding.UTF8.GetString(bytes));
    }
}
