using System.Buffers.Binary;

namespace SbStudio.Effects.Host;

internal sealed class EffectAssets(string project, string package, string generated) : IEffectAssets
{
    internal readonly Dictionary<string, string> Reads = new(StringComparer.Ordinal);
    public byte[] Read(AssetReference asset)
    {
        asset.Validate(); string root = asset.Kind switch { AssetKind.Project => project, AssetKind.Package => package, AssetKind.Generated => generated, _ => throw new ArgumentException("Unknown asset kind.") };
        string file = PackageFiles.SafePath(root, asset.Path);
        if (new FileInfo(file).Length > PackageFiles.MaxFileBytes) throw new IOException("Effect asset exceeds size limit.");
        byte[] bytes = File.ReadAllBytes(file); Reads[EffectJson.Serialize(asset)] = PackageFiles.Hash(bytes); return bytes;
    }
    public ImageSize ImageSize(AssetReference asset)
    {
        byte[] b = Read(asset);
        if (b.Length >= 24 && b.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}))
            return Size(BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(20)));
        if (b.Length >= 10 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F')
            return Size(BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(6)), BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(8)));
        if (b.Length >= 26 && b[0] == 'B' && b[1] == 'M')
            return Size(BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(18)), Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(22))));
        if (b.Length >= 4 && b[0] == 255 && b[1] == 216)
        {
            int p = 2;
            while (p + 4 <= b.Length)
            {
                if (b[p++] != 255) break;
                while (p < b.Length && b[p] == 255) p++;
                if (p + 3 > b.Length) break;
                int marker = b[p++]; if (marker == 217 || marker == 218) break;
                int length = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p));
                if (length < 2 || p + length > b.Length) break;
                if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc) && length >= 7)
                    return Size(BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p + 5)), BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p + 3)));
                p += length;
            }
        }
        throw new IOException("Image dimensions require a valid PNG, JPEG, BMP or GIF.");
        static ImageSize Size(int width, int height) => width > 0 && height > 0 && width <= 100000 && height <= 100000 ? new(width, height) : throw new IOException("Invalid image size.");
    }
    public AssetReference Write(string relativePath, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > PackageFiles.MaxFileBytes) throw new IOException("Generated asset exceeds size limit.");
        string path = PackageFiles.SafePath(generated, relativePath); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes.ToArray()); return AssetReference.Generated(relativePath);
    }
}
