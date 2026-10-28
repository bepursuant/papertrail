namespace Papertrail.Imaging;

/// <summary>Tiny raster surface used to draw mock pages. Always RGB internally.</summary>
internal sealed class Canvas
{
    private readonly byte[] _rgb;

    public Canvas(int width, int height)
    {
        Width = width;
        Height = height;
        _rgb = new byte[width * height * 3];
        Array.Fill(_rgb, (byte)255);
    }

    public int Width { get; }

    public int Height { get; }

    public void FillRect(int x, int y, int w, int h, (byte R, byte G, byte B) color)
    {
        int x0 = Math.Clamp(x, 0, Width), x1 = Math.Clamp(x + w, 0, Width);
        int y0 = Math.Clamp(y, 0, Height), y1 = Math.Clamp(y + h, 0, Height);
        for (int py = y0; py < y1; py++)
        {
            int row = py * Width * 3;
            for (int px = x0; px < x1; px++)
            {
                int i = row + (px * 3);
                _rgb[i] = color.R;
                _rgb[i + 1] = color.G;
                _rgb[i + 2] = color.B;
            }
        }
    }

    public void StrokeRect(int x, int y, int w, int h, int thickness, (byte R, byte G, byte B) color)
    {
        FillRect(x, y, w, thickness, color);
        FillRect(x, y + h - thickness, w, thickness, color);
        FillRect(x, y, thickness, h, color);
        FillRect(x + w - thickness, y, thickness, h, color);
    }

    /// <returns>The rendered width in pixels.</returns>
    public int DrawText(string text, int x, int y, int scale, (byte R, byte G, byte B) color)
    {
        int cursor = x;
        foreach (char c in text.ToUpperInvariant())
        {
            var glyph = BitmapFont.Get(c);
            for (int row = 0; row < BitmapFont.GlyphHeight; row++)
            {
                for (int col = 0; col < BitmapFont.GlyphWidth; col++)
                {
                    if (glyph[(row * BitmapFont.GlyphWidth) + col])
                    {
                        FillRect(cursor + (col * scale), y + (row * scale), scale, scale, color);
                    }
                }
            }

            cursor += (BitmapFont.GlyphWidth + 1) * scale;
        }

        return cursor - x;
    }

    public static int MeasureText(string text, int scale) => text.Length * (BitmapFont.GlyphWidth + 1) * scale;

    public byte[] ToPng(bool grayscale, bool threshold)
    {
        if (!grayscale)
        {
            return PngEncoder.Encode(Width, Height, PngPixelFormat.Rgb24, _rgb);
        }

        var gray = new byte[Width * Height];
        for (int i = 0; i < gray.Length; i++)
        {
            int luma = ((_rgb[i * 3] * 299) + (_rgb[(i * 3) + 1] * 587) + (_rgb[(i * 3) + 2] * 114)) / 1000;
            gray[i] = threshold ? (byte)(luma < 160 ? 0 : 255) : (byte)luma;
        }

        return PngEncoder.Encode(Width, Height, PngPixelFormat.Gray8, gray);
    }
}

internal static class BitmapFont
{
    public const int GlyphWidth = 5;
    public const int GlyphHeight = 7;

    private static readonly Dictionary<char, bool[]> Glyphs = Build();

    public static bool[] Get(char c) => Glyphs.TryGetValue(c, out var g) ? g : Glyphs['?'];

    private static Dictionary<char, bool[]> Build()
    {
        var source = new Dictionary<char, string>
        {
            ['A'] = ".###.|#...#|#...#|#####|#...#|#...#|#...#",
            ['B'] = "####.|#...#|#...#|####.|#...#|#...#|####.",
            ['C'] = ".###.|#...#|#....|#....|#....|#...#|.###.",
            ['D'] = "####.|#...#|#...#|#...#|#...#|#...#|####.",
            ['E'] = "#####|#....|#....|####.|#....|#....|#####",
            ['F'] = "#####|#....|#....|####.|#....|#....|#....",
            ['G'] = ".###.|#...#|#....|#.###|#...#|#...#|.####",
            ['H'] = "#...#|#...#|#...#|#####|#...#|#...#|#...#",
            ['I'] = ".###.|..#..|..#..|..#..|..#..|..#..|.###.",
            ['J'] = "..###|...#.|...#.|...#.|...#.|#..#.|.##..",
            ['K'] = "#...#|#..#.|#.#..|##...|#.#..|#..#.|#...#",
            ['L'] = "#....|#....|#....|#....|#....|#....|#####",
            ['M'] = "#...#|##.##|#.#.#|#.#.#|#...#|#...#|#...#",
            ['N'] = "#...#|#...#|##..#|#.#.#|#..##|#...#|#...#",
            ['O'] = ".###.|#...#|#...#|#...#|#...#|#...#|.###.",
            ['P'] = "####.|#...#|#...#|####.|#....|#....|#....",
            ['Q'] = ".###.|#...#|#...#|#...#|#.#.#|#..#.|.##.#",
            ['R'] = "####.|#...#|#...#|####.|#.#..|#..#.|#...#",
            ['S'] = ".####|#....|#....|.###.|....#|....#|####.",
            ['T'] = "#####|..#..|..#..|..#..|..#..|..#..|..#..",
            ['U'] = "#...#|#...#|#...#|#...#|#...#|#...#|.###.",
            ['V'] = "#...#|#...#|#...#|#...#|#...#|.#.#.|..#..",
            ['W'] = "#...#|#...#|#...#|#.#.#|#.#.#|#.#.#|.#.#.",
            ['X'] = "#...#|#...#|.#.#.|..#..|.#.#.|#...#|#...#",
            ['Y'] = "#...#|#...#|.#.#.|..#..|..#..|..#..|..#..",
            ['Z'] = "#####|....#|...#.|..#..|.#...|#....|#####",
            ['0'] = ".###.|#...#|#..##|#.#.#|##..#|#...#|.###.",
            ['1'] = "..#..|.##..|..#..|..#..|..#..|..#..|.###.",
            ['2'] = ".###.|#...#|....#|...#.|..#..|.#...|#####",
            ['3'] = "#####|...#.|..#..|...#.|....#|#...#|.###.",
            ['4'] = "...#.|..##.|.#.#.|#..#.|#####|...#.|...#.",
            ['5'] = "#####|#....|####.|....#|....#|#...#|.###.",
            ['6'] = "..##.|.#...|#....|####.|#...#|#...#|.###.",
            ['7'] = "#####|....#|...#.|..#..|.#...|.#...|.#...",
            ['8'] = ".###.|#...#|#...#|.###.|#...#|#...#|.###.",
            ['9'] = ".###.|#...#|#...#|.####|....#|...#.|.##..",
            ['/'] = "....#|...#.|...#.|..#..|.#...|.#...|#....",
            ['-'] = ".....|.....|.....|#####|.....|.....|.....",
            [':'] = ".....|..##.|..##.|.....|..##.|..##.|.....",
            ['.'] = ".....|.....|.....|.....|.....|.##..|.##..",
            ['?'] = ".###.|#...#|....#|...#.|..#..|.....|..#..",
            [' '] = ".....|.....|.....|.....|.....|.....|.....",
        };

        var glyphs = new Dictionary<char, bool[]>();
        foreach (var (c, pattern) in source)
        {
            glyphs[c] = pattern.Replace("|", string.Empty, StringComparison.Ordinal).Select(ch => ch == '#').ToArray();
        }

        return glyphs;
    }
}
