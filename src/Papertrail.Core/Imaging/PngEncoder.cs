using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Papertrail.Imaging;

public enum PngPixelFormat
{
    Gray8 = 0,
    Rgb24 = 2,
}

/// <summary>Minimal dependency-free PNG writer (8-bit gray or RGB, no interlacing).</summary>
public static class PngEncoder
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Encode(int width, int height, PngPixelFormat format, ReadOnlySpan<byte> pixels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        int bytesPerPixel = format == PngPixelFormat.Rgb24 ? 3 : 1;
        int stride = width * bytesPerPixel;
        if (pixels.Length != stride * height)
        {
            throw new ArgumentException($"Expected {stride * height} bytes of pixel data, got {pixels.Length}.", nameof(pixels));
        }

        using var output = new MemoryStream();
        output.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8; // bit depth
        header[9] = (byte)format;
        header[10] = 0; // deflate
        header[11] = 0; // adaptive filtering
        header[12] = 0; // no interlace
        WriteChunk(output, "IHDR", header);

        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                for (int y = 0; y < height; y++)
                {
                    zlib.WriteByte(0); // filter: none
                    zlib.Write(pixels.Slice(y * stride, stride));
                }
            }

            WriteChunk(output, "IDAT", compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        }

        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>Reads width/height from a PNG header without decoding the image.</summary>
    public static bool TryReadSize(ReadOnlySpan<byte> png, out int width, out int height)
    {
        width = height = 0;
        if (png.Length < 24 || !png[..8].SequenceEqual(Signature) || !png.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return false;
        }

        width = BinaryPrimitives.ReadInt32BigEndian(png[16..]);
        height = BinaryPrimitives.ReadInt32BigEndian(png[20..]);
        return width > 0 && height > 0;
    }

    internal static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in type)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        output.Write(buffer);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(buffer, Crc32(typeBytes, data));
        output.Write(buffer);
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
