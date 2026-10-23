using System.Numerics;

namespace SbStudio.Core;

/// <summary>
/// Minimal baseline JPEG encoder: 8 bits, colour kept at full resolution (no chroma subsampling), the
/// quantisation and Huffman tables of the JPEG standard.
/// </summary>
public static class JpegWriter
{
    // Quantisation tables of the standard (Annex K.1) in row order. They are what quality 50 uses.
    private static readonly byte[] LumaQuant =
    {
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77,
        24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103, 99,
    };

    private static readonly byte[] ChromaQuant =
    {
        17, 18, 24, 47, 99, 99, 99, 99,
        18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99,
        47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
    };

    /// <summary>A Huffman table as a JPEG file states it: how many codes have each length from 1 to 16, and the symbols in code order.</summary>
    private sealed class Huffman
    {
        public readonly byte[] Counts;
        public readonly byte[] Symbols;
        public readonly ushort[] Code = new ushort[256];
        public readonly byte[] Length = new byte[256];

        public Huffman(byte[] counts, byte[] symbols, IEnumerable<int> needed)
        {
            Counts = counts;
            Symbols = symbols;
            if (counts.Length != 16 || counts.Sum(c => c) != symbols.Length) throw new InvalidOperationException("Huffman table: counts and symbols disagree");

            // Codes are handed out in order, shortest first (Annex C).
            int code = 0, next = 0;
            for (int length = 1; length <= 16; length++)
            {
                for (int i = 0; i < counts[length - 1]; i++, code++)
                {
                    byte symbol = symbols[next++];
                    if (code >= 1 << length || Length[symbol] != 0) throw new InvalidOperationException("Huffman table: not a prefix code");
                    Code[symbol] = (ushort)code;
                    Length[symbol] = (byte)length;
                }
                code <<= 1;
            }
            // A symbol without a code would silently corrupt the picture that needs it.
            foreach (int symbol in needed)
                if (Length[symbol] == 0) throw new InvalidOperationException($"Huffman table: no code for symbol {symbol:X2}");
        }
    }

    // DC symbols are the bit size of a difference; AC symbols a run of zeros (high half) and a bit size (low half).
    private static IEnumerable<int> DcSymbols => Enumerable.Range(0, 12);

    private static IEnumerable<int> AcSymbols
    {
        get
        {
            yield return 0x00; // end of block
            yield return 0xF0; // sixteen zeros
            for (int run = 0; run < 16; run++)
                for (int size = 1; size <= 10; size++)
                    yield return run << 4 | size;
        }
    }

    // The Huffman tables of the standard (Annex K.3).
    private static readonly Huffman DcLuma = new(
        new byte[] { 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 },
        new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }, DcSymbols);

    private static readonly Huffman DcChroma = new(
        new byte[] { 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 },
        new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }, DcSymbols);

    private static readonly Huffman AcLuma = new(
        new byte[] { 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7D },
        new byte[]
        {
            0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
            0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08, 0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0,
            0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28,
            0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
            0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
            0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
            0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7,
            0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5,
            0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2,
            0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
            0xF9, 0xFA,
        }, AcSymbols);

    private static readonly Huffman AcChroma = new(
        new byte[] { 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77 },
        new byte[]
        {
            0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
            0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xA1, 0xB1, 0xC1, 0x09, 0x23, 0x33, 0x52, 0xF0,
            0x15, 0x62, 0x72, 0xD1, 0x0A, 0x16, 0x24, 0x34, 0xE1, 0x25, 0xF1, 0x17, 0x18, 0x19, 0x1A, 0x26,
            0x27, 0x28, 0x29, 0x2A, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
            0x49, 0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
            0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
            0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5,
            0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3,
            0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA,
            0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
            0xF9, 0xFA,
        }, AcSymbols);

    /// <summary>For each position of the zigzag walk through a block, the row-order index it visits.</summary>
    private static readonly int[] ZigZag = BuildZigZag();

    /// <summary>The 8-point cosine transform as a matrix: row u holds the weights of the samples for frequency u.</summary>
    private static readonly float[] Cosine = BuildCosine();

    private static int[] BuildZigZag()
    {
        var order = new int[64];
        int n = 0;
        for (int sum = 0; sum < 15; sum++)
        {
            // Diagonals are walked alternately down-left and up-right.
            int first = Math.Max(0, sum - 7), last = Math.Min(sum, 7);
            if (sum % 2 == 1)
                for (int row = first; row <= last; row++) order[n++] = row * 8 + (sum - row);
            else
                for (int row = last; row >= first; row--) order[n++] = row * 8 + (sum - row);
        }
        return order;
    }

    private static float[] BuildCosine()
    {
        var matrix = new float[64];
        for (int u = 0; u < 8; u++)
            for (int x = 0; x < 8; x++)
                matrix[u * 8 + x] = (float)(Math.Sqrt(u == 0 ? 1.0 / 8 : 2.0 / 8) * Math.Cos((2 * x + 1) * u * Math.PI / 16));
        return matrix;
    }

    /// <param name="quality">1 (smallest file) to 100 (best picture).</param>
    public static void Write(string path, int width, int height, ReadOnlySpan<byte> rgba, int quality)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, Encode(width, height, rgba, quality));
    }

    /// <summary>Encodes top-down RGBA rows; the alpha is ignored.</summary>
    public static byte[] Encode(int width, int height, ReadOnlySpan<byte> rgba, int quality)
    {
        if (width < 1 || height < 1 || width > 65535 || height > 65535) throw new ArgumentException("picture size out of range");
        if (rgba.Length < width * height * 4) throw new ArgumentException("pixel buffer too small");

        // The usual meaning of the quality number: 50 is the tables as they are, 100 divides by one.
        quality = Math.Clamp(quality, 1, 100);
        int percent = quality < 50 ? 5000 / quality : 200 - quality * 2;
        var luma = new byte[64];
        var chroma = new byte[64];
        for (int i = 0; i < 64; i++)
        {
            luma[i] = (byte)Math.Clamp((LumaQuant[i] * percent + 50) / 100, 1, 255);
            chroma[i] = (byte)Math.Clamp((ChromaQuant[i] * percent + 50) / 100, 1, 255);
        }

        var output = new MemoryStream(Math.Max(1024, width * height / 3));
        void Marker(byte marker, int length)
        {
            output.WriteByte(0xFF);
            output.WriteByte(marker);
            output.WriteByte((byte)(length >> 8));
            output.WriteByte((byte)length);
        }

        output.WriteByte(0xFF);
        output.WriteByte(0xD8);

        // JFIF header: version 1.1, no density, no thumbnail.
        Marker(0xE0, 16);
        output.Write(new byte[] { (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0 });

        Marker(0xDB, 2 + 65 * 2);
        output.WriteByte(0);
        foreach (int index in ZigZag) output.WriteByte(luma[index]);
        output.WriteByte(1);
        foreach (int index in ZigZag) output.WriteByte(chroma[index]);

        // Baseline frame: three components, each sampled once per block.
        Marker(0xC0, 17);
        output.Write(new byte[]
        {
            8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3,
            1, 0x11, 0,
            2, 0x11, 1,
            3, 0x11, 1,
        });

        Marker(0xC4, 2 + 17 * 4 + DcLuma.Symbols.Length + AcLuma.Symbols.Length + DcChroma.Symbols.Length + AcChroma.Symbols.Length);
        foreach (var (id, table) in new[] { (0x00, DcLuma), (0x10, AcLuma), (0x01, DcChroma), (0x11, AcChroma) })
        {
            output.WriteByte((byte)id);
            output.Write(table.Counts);
            output.Write(table.Symbols);
        }

        Marker(0xDA, 12);
        output.Write(new byte[] { 3, 1, 0x00, 2, 0x11, 3, 0x11, 0, 63, 0 });

        var bits = new BitWriter(output);
        var y = new float[64];
        var cb = new float[64];
        var cr = new float[64];
        var scratch = new float[64];
        int lastY = 0, lastCb = 0, lastCr = 0;
        for (int blockY = 0; blockY < height; blockY += 8)
        {
            for (int blockX = 0; blockX < width; blockX += 8)
            {
                for (int row = 0; row < 8; row++)
                {
                    // Blocks that reach past the picture repeat its last row and column.
                    int line = Math.Min(blockY + row, height - 1) * width;
                    for (int column = 0; column < 8; column++)
                    {
                        int p = (line + Math.Min(blockX + column, width - 1)) * 4;
                        float r = rgba[p], g = rgba[p + 1], b = rgba[p + 2];
                        int k = row * 8 + column;
                        y[k] = 0.299f * r + 0.587f * g + 0.114f * b - 128f;
                        cb[k] = -0.168736f * r - 0.331264f * g + 0.5f * b;
                        cr[k] = 0.5f * r - 0.418688f * g - 0.081312f * b;
                    }
                }
                lastY = WriteBlock(bits, y, scratch, luma, lastY, DcLuma, AcLuma);
                lastCb = WriteBlock(bits, cb, scratch, chroma, lastCb, DcChroma, AcChroma);
                lastCr = WriteBlock(bits, cr, scratch, chroma, lastCr, DcChroma, AcChroma);
            }
        }
        bits.Flush();

        output.WriteByte(0xFF);
        output.WriteByte(0xD9);
        return output.ToArray();
    }

    /// <summary>Transforms, quantises and writes one 8x8 block of one component.</summary>
    /// <returns>The block's DC value, which the next block of the component is written relative to.</returns>
    private static int WriteBlock(BitWriter bits, float[] block, float[] scratch, byte[] quant, int lastDc, Huffman dc, Huffman ac)
    {
        // Rows, then columns.
        for (int row = 0; row < 8; row++)
        {
            for (int u = 0; u < 8; u++)
            {
                float sum = 0;
                for (int x = 0; x < 8; x++) sum += Cosine[u * 8 + x] * block[row * 8 + x];
                scratch[row * 8 + u] = sum;
            }
        }
        for (int column = 0; column < 8; column++)
        {
            for (int v = 0; v < 8; v++)
            {
                float sum = 0;
                for (int row = 0; row < 8; row++) sum += Cosine[v * 8 + row] * scratch[row * 8 + column];
                block[v * 8 + column] = sum;
            }
        }

        Span<int> values = stackalloc int[64];
        for (int i = 0; i < 64; i++)
        {
            int index = ZigZag[i];
            values[i] = Math.Clamp((int)MathF.Round(block[index] / quant[index]), -1023, 1023);
        }

        int difference = values[0] - lastDc;
        int size = Size(difference);
        bits.Write(dc.Code[size], dc.Length[size]);
        if (size > 0) bits.Write(Tail(difference, size), size);

        int end = 63;
        while (end > 0 && values[end] == 0) end--;
        int run = 0;
        for (int i = 1; i <= end; i++)
        {
            if (values[i] == 0)
            {
                run++;
                continue;
            }
            for (; run >= 16; run -= 16) bits.Write(ac.Code[0xF0], ac.Length[0xF0]);
            size = Size(values[i]);
            int symbol = run << 4 | size;
            bits.Write(ac.Code[symbol], ac.Length[symbol]);
            bits.Write(Tail(values[i], size), size);
            run = 0;
        }
        if (end < 63) bits.Write(ac.Code[0x00], ac.Length[0x00]);
        return values[0];
    }

    /// <summary>How many bits the magnitude of a value takes.</summary>
    private static int Size(int value) => 32 - BitOperations.LeadingZeroCount((uint)Math.Abs(value));

    /// <summary>The bits that follow a size: the value itself, or for a negative one its ones' complement.</summary>
    private static int Tail(int value, int size) => value >= 0 ? value : value + (1 << size) - 1;

    private sealed class BitWriter
    {
        private readonly Stream output;
        private int buffer, count;

        public BitWriter(Stream output) => this.output = output;

        public void Write(int code, int length)
        {
            buffer = buffer << length | code & (1 << length) - 1;
            count += length;
            while (count >= 8)
            {
                byte value = (byte)(buffer >> count - 8);
                output.WriteByte(value);
                // A data byte of all ones is told apart from a marker by the zero after it.
                if (value == 0xFF) output.WriteByte(0);
                count -= 8;
            }
            buffer &= (1 << count) - 1;
        }

        /// <summary>Fills the last byte up with ones.</summary>
        public void Flush()
        {
            if (count > 0) Write((1 << 8 - count) - 1, 8 - count);
        }
    }
}
