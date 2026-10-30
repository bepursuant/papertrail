using System.Buffers.Binary;
using System.IO.Compression;

namespace Papertrail.Imaging;

/// <summary>
/// Minimal dependency-free PNG reader that produces 8-bit luminance, which is all barcode detection
/// needs. Handles every non-interlaced PNG: gray, RGB, palette, with or without alpha, 1-16 bits.
/// Transparent pixels are composited onto white.
/// </summary>
public static class PngDecoder
{
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    public static LuminanceImage DecodeLuminance(ReadOnlySpan<byte> png)
    {
        if (png.Length < 8 || !png[..8].SequenceEqual(Signature))
        {
            throw new InvalidDataException("Not a PNG image.");
        }

        int width = 0, height = 0, bitDepth = 0, colorType = -1;
        byte[]? palette = null;
        byte[]? paletteAlpha = null;
        using var compressed = new MemoryStream();
        int offset = 8;
        while (offset + 12 <= png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png[offset..]);
            if (length < 0 || offset + 12 + length > png.Length)
            {
                throw new InvalidDataException("Truncated PNG chunk.");
            }

            var type = png.Slice(offset + 4, 4);
            var data = png.Slice(offset + 8, length);
            offset += 12 + length;
            if (type.SequenceEqual("IHDR"u8))
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                bitDepth = data[8];
                colorType = data[9];
                if (data[12] != 0)
                {
                    throw new NotSupportedException("Interlaced PNGs are not supported.");
                }
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                palette = data.ToArray();
            }
            else if (type.SequenceEqual("tRNS"u8) && colorType == 3)
            {
                paletteAlpha = data.ToArray();
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                compressed.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }
        }

        int channels = colorType switch
        {
            0 or 3 => 1,
            2 => 3,
            4 => 2,
            6 => 4,
            _ => throw new InvalidDataException($"Unknown PNG color type {colorType}."),
        };
        if (width <= 0 || height <= 0 || bitDepth is not (1 or 2 or 4 or 8 or 16) || (colorType == 3 && palette is null))
        {
            throw new InvalidDataException("Invalid PNG header.");
        }

        byte[]? paletteLuma = palette is null ? null : PaletteLuminance(palette, paletteAlpha);
        int bitsPerPixel = channels * bitDepth;
        int stride = ((width * bitsPerPixel) + 7) / 8;
        int filterStep = Math.Max(1, bitsPerPixel / 8);
        var output = new byte[checked(width * height)];
        var current = new byte[stride];
        var previous = new byte[stride];

        compressed.Position = 0;
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        for (int y = 0; y < height; y++)
        {
            int filter = zlib.ReadByte();
            zlib.ReadExactly(current);
            Unfilter(filter, current, previous, filterStep);
            ConvertRow(current, output.AsSpan(y * width, width), colorType, bitDepth, paletteLuma);
            (current, previous) = (previous, current);
        }

        return new LuminanceImage(width, height, output);
    }

    private static void Unfilter(int filter, Span<byte> row, ReadOnlySpan<byte> prior, int bpp)
    {
        switch (filter)
        {
            case 0:
                break;
            case 1:
                for (int i = bpp; i < row.Length; i++)
                {
                    row[i] += row[i - bpp];
                }

                break;
            case 2:
                for (int i = 0; i < row.Length; i++)
                {
                    row[i] += prior[i];
                }

                break;
            case 3:
                for (int i = 0; i < row.Length; i++)
                {
                    int left = i >= bpp ? row[i - bpp] : 0;
                    row[i] += (byte)((left + prior[i]) >> 1);
                }

                break;
            case 4:
                for (int i = 0; i < row.Length; i++)
                {
                    int a = i >= bpp ? row[i - bpp] : 0;
                    int b = prior[i];
                    int c = i >= bpp ? prior[i - bpp] : 0;
                    int p = a + b - c;
                    int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    row[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                }

                break;
            default:
                throw new InvalidDataException($"Unknown PNG filter {filter}.");
        }
    }

    private static void ConvertRow(ReadOnlySpan<byte> row, Span<byte> luma, int colorType, int bitDepth, byte[]? paletteLuma)
    {
        if (bitDepth < 8)
        {
            int perByte = 8 / bitDepth;
            int mask = (1 << bitDepth) - 1;
            int scale = 255 / mask;
            for (int x = 0; x < luma.Length; x++)
            {
                int shift = 8 - bitDepth - ((x % perByte) * bitDepth);
                int value = (row[x / perByte] >> shift) & mask;
                luma[x] = paletteLuma is not null ? paletteLuma[Math.Min(value, paletteLuma.Length - 1)] : (byte)(value * scale);
            }

            return;
        }

        // 16-bit samples: the high byte is enough for luminance.
        int sampleBytes = bitDepth / 8;
        switch (colorType)
        {
            case 0:
                for (int x = 0; x < luma.Length; x++)
                {
                    luma[x] = row[x * sampleBytes];
                }

                break;
            case 3:
                for (int x = 0; x < luma.Length; x++)
                {
                    luma[x] = paletteLuma![Math.Min(row[x], paletteLuma.Length - 1)];
                }

                break;
            case 2:
                for (int x = 0; x < luma.Length; x++)
                {
                    int i = x * 3 * sampleBytes;
                    luma[x] = Luma(row[i], row[i + sampleBytes], row[i + (2 * sampleBytes)]);
                }

                break;
            case 4:
                for (int x = 0; x < luma.Length; x++)
                {
                    int i = x * 2 * sampleBytes;
                    luma[x] = OnWhite(row[i], row[i + sampleBytes]);
                }

                break;
            default:
                for (int x = 0; x < luma.Length; x++)
                {
                    int i = x * 4 * sampleBytes;
                    luma[x] = OnWhite(Luma(row[i], row[i + sampleBytes], row[i + (2 * sampleBytes)]), row[i + (3 * sampleBytes)]);
                }

                break;
        }
    }

    private static byte[] PaletteLuminance(byte[] palette, byte[]? alpha)
    {
        var luma = new byte[palette.Length / 3];
        for (int i = 0; i < luma.Length; i++)
        {
            byte value = Luma(palette[i * 3], palette[(i * 3) + 1], palette[(i * 3) + 2]);
            luma[i] = alpha is not null && i < alpha.Length ? OnWhite(value, alpha[i]) : value;
        }

        return luma;
    }

    private static byte Luma(int r, int g, int b) => (byte)(((r * 299) + (g * 587) + (b * 114)) / 1000);

    private static byte OnWhite(int luma, int alpha) => (byte)(((luma * alpha) + (255 * (255 - alpha))) / 255);
}
