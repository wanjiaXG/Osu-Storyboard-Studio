using System.Numerics;
using StbImageSharp;
using SbStudio.Resources;

namespace SbStudio.App.Gfx;

/// <summary>Original lazer texture and bitmap-font resources, independent of its graphics framework.</summary>
internal static class ArgonPreviewAssets
{
    internal sealed record Glyph(OsuPreviewSkin.Bitmap Image, float Advance, Vector2 Offset, float FontSize);
    private static string Folder => ResourceAssets.Argon;

    public static OsuPreviewSkin.Bitmap Read(string name) => Decode(ResourceAssets.ReadAllBytes(ResourceAssets.Combine(Folder, name + ".png")));

    public static Glyph[] ReadIcons() => ReadFont(ResourceAssets.ReadAllBytes(ResourceAssets.Combine(Folder, "FontAwesome-Solid.bin")),
        page => ResourceAssets.ReadAllBytes(ResourceAssets.Combine(Folder, $"FontAwesome-Solid_{page}.png")), new uint[] { 0xf054, 0xf105, 0xf101 });

    public static Glyph ReadIcon(uint character) => ReadFont(ResourceAssets.ReadAllBytes(ResourceAssets.Combine(Folder, "FontAwesome-Solid.bin")),
        page => ResourceAssets.ReadAllBytes(ResourceAssets.Combine(Folder, $"FontAwesome-Solid_{page}.png")), new[] { character })[0];

    internal static Glyph[] ReadFont(byte[] bytes, Func<int, byte[]> readPage, uint[] characters)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt32() != 0x03464d42) throw new InvalidDataException("Expected BMFont v3.");
        float fontSize = 100;
        var glyphs = new Dictionary<uint, Glyph>();
        var pages = new Dictionary<int, OsuPreviewSkin.Bitmap>();
        while (stream.Position < stream.Length)
        {
            byte type = reader.ReadByte();
            int length = reader.ReadInt32();
            long end = stream.Position + length;
            if (length < 0 || end > stream.Length) throw new InvalidDataException("Invalid BMFont block.");
            if (type == 1) fontSize = Math.Abs(reader.ReadInt16());
            if (type == 4)
            {
                if (length % 20 != 0) throw new InvalidDataException("Invalid BMFont characters.");
                while (stream.Position < end)
                {
                    uint id = reader.ReadUInt32();
                    int x = reader.ReadUInt16(), y = reader.ReadUInt16(), width = reader.ReadUInt16(), height = reader.ReadUInt16();
                    int ox = reader.ReadInt16(), oy = reader.ReadInt16(), advance = reader.ReadInt16(), page = reader.ReadByte();
                    reader.ReadByte();
                    if (!characters.Contains(id)) continue;
                    if (!pages.TryGetValue(page, out var image)) pages[page] = image = Decode(readPage(page));
                    if (width <= 0 || height <= 0 || x + width > image.Width || y + height > image.Height) throw new InvalidDataException("Invalid glyph bounds.");
                    var pixels = new byte[width * height * 4];
                    for (int row = 0; row < height; row++) Array.Copy(image.Pixels, ((y + row) * image.Width + x) * 4, pixels, row * width * 4, width * 4);
                    glyphs[id] = new(new(width, height, 1, pixels), advance, new(ox, oy), fontSize);
                }
            }
            stream.Position = end;
        }
        return characters.Select(c => glyphs.TryGetValue(c, out var glyph) ? glyph : throw new InvalidDataException("Missing font glyph.")).ToArray();
    }

    private static OsuPreviewSkin.Bitmap Decode(byte[] bytes)
    {
        var image = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlueAlpha);
        for (int p = 0; p < image.Data.Length; p += 4)
            for (int c = 0; c < 3; c++) image.Data[p + c] = (byte)(image.Data[p + c] * image.Data[p + 3] / 255);
        return new(image.Width, image.Height, 1, image.Data);
    }
}
