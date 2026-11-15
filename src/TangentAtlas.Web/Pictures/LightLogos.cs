using System.Buffers.Binary;
using System.IO.Compression;

namespace TangentAtlas.Web.Pictures;

/// <summary>
/// Makes the icon set's logos fit the site's pages.  They are 250 pixels square and shown at most at 56, so they are
/// served at 112 (twice, for sharp screens).  And the set is drawn for dark backgrounds: about a quarter of its logos
/// are white (Apple, Visa, Disney), which would vanish on this site's light pages, so a logo that is mostly white has
/// its white drawn in the site's dark ink instead; a logo in colour is left as it is. Works on the 8-bit, non-interlaced PNGs the set is made of (RGBA or RGB);
/// anything else is served unchanged.
/// </summary>
public static class LightLogos
{
    /// <summary>The ink white is redrawn in: the site's darkest text colour.</summary>
    private static readonly (byte R, byte G, byte B) Ink = (31, 42, 42);

    /// <summary>A pixel is white if each of its channels is at least this.</summary>
    private const int White = 216;

    /// <summary>A logo is white if at least this share of its visible pixels are.</summary>
    private const double WhiteLogo = 0.5;

    private static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The largest a picture is served: twice the largest it is shown (a tile's 56 pixels), for sharp screens.</summary>
    public const int LargestSide = 112;

    /// <summary>
    /// The picture to show on a light page: no larger than <see cref="LargestSide"/> (the set's are 250 pixels square,
    /// about 13 KB; at 112 about 6 KB), with a white logo's white redrawn.  Anything this can't read is served as it is.
    /// </summary>
    public static byte[] ForLightPage(byte[] png)
    {
        if (Decode(png) is not { } image)
        {
            return png;
        }

        var smaller = Shrink(image, LargestSide);
        var recolored = Recolor(smaller);
        return ReferenceEquals(smaller, image) && !recolored ? png : Encode(smaller);
    }

    /// <summary>
    /// The picture at most <paramref name="side"/> pixels on its longer side: each new pixel the average of the old ones
    /// it covers, weighted by their opacity so a transparent pixel's colour doesn't bleed into the edge.
    /// </summary>
    public static Image Shrink(Image image, int side)
    {
        var scale = (double)side / Math.Max(image.Width, image.Height);
        if (scale >= 1)
        {
            return image;
        }

        int width = Math.Max(1, (int)Math.Round(image.Width * scale)), height = Math.Max(1, (int)Math.Round(image.Height * scale));
        var source = image.Rgba;
        var result = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            int top = y * image.Height / height, bottom = Math.Max(top + 1, (y + 1) * image.Height / height);
            for (var x = 0; x < width; x++)
            {
                int left = x * image.Width / width, right = Math.Max(left + 1, (x + 1) * image.Width / width);
                double r = 0, g = 0, b = 0, a = 0;
                for (var sy = top; sy < bottom; sy++)
                {
                    for (var sx = left; sx < right; sx++)
                    {
                        var i = (sy * image.Width + sx) * 4;
                        double alpha = source[i + 3];
                        r += source[i] * alpha;
                        g += source[i + 1] * alpha;
                        b += source[i + 2] * alpha;
                        a += alpha;
                    }
                }

                var o = (y * width + x) * 4;
                var count = (bottom - top) * (right - left);
                if (a > 0)
                {
                    result[o] = (byte)Math.Round(r / a);
                    result[o + 1] = (byte)Math.Round(g / a);
                    result[o + 2] = (byte)Math.Round(b / a);
                }

                result[o + 3] = (byte)Math.Round(a / count);
            }
        }

        return new Image(width, height, result);
    }

    /// <summary>A picture's pixels: red, green, blue and opacity, a byte each, row by row.</summary>
    public sealed record Image(int Width, int Height, byte[] Rgba);

    private static bool Recolor(Image image)
    {
        int visible = 0, white = 0;
        var pixels = image.Rgba;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] < 128)
            {
                continue;
            }

            visible++;
            if (IsWhite(pixels, i))
            {
                white++;
            }
        }

        if (visible == 0 || white < visible * WhiteLogo)
        {
            return false;
        }

        // Every white pixel, the soft edges included (their transparency is kept).
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] > 0 && IsWhite(pixels, i))
            {
                (pixels[i], pixels[i + 1], pixels[i + 2]) = Ink;
            }
        }

        return true;
    }

    private static bool IsWhite(byte[] pixels, int i) => pixels[i] >= White && pixels[i + 1] >= White && pixels[i + 2] >= White;

    /// <summary>A PNG's pixels, or null if it isn't one this reads.</summary>
    public static Image? Decode(byte[] png)
    {
        if (png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(Signature))
        {
            return null;
        }

        int width = 0, height = 0, channels = 0;
        using var data = new MemoryStream();
        for (var offset = 8; offset + 12 <= png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            if (length < 0 || offset + 12 + length > png.Length)
            {
                return null;
            }

            var body = png.AsSpan(offset + 8, length);
            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(body);
                    height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                    var (depth, colour, interlace) = (body[8], body[9], body[12]);
                    channels = colour switch { 6 => 4, 2 => 3, _ => 0 };
                    if (depth != 8 || channels == 0 || interlace != 0 || width is <= 0 or > 4096 || height is <= 0 or > 4096)
                    {
                        return null;
                    }

                    break;
                case "IDAT":
                    data.Write(body);
                    break;
                case "IEND":
                    offset = png.Length;
                    continue;
            }

            offset += 12 + length;
        }

        if (channels == 0)
        {
            return null;
        }

        var stride = width * channels;
        var raw = new byte[(stride + 1) * height];
        data.Position = 0;
        using (var inflate = new ZLibStream(data, CompressionMode.Decompress))
        {
            try
            {
                inflate.ReadExactly(raw);
            }
            catch (Exception exception) when (exception is EndOfStreamException or InvalidDataException)
            {
                return null;
            }
        }

        // Undo each row's filter (PNG specification, section 9).
        var rows = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            var source = raw.AsSpan(y * (stride + 1) + 1, stride);
            var row = rows.AsSpan(y * stride, stride);
            var above = y > 0 ? rows.AsSpan((y - 1) * stride, stride) : new byte[stride];
            for (var x = 0; x < stride; x++)
            {
                int a = x >= channels ? row[x - channels] : 0, b = above[x], c = x >= channels ? above[x - channels] : 0;
                row[x] = (byte)(source[x] + filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => 0,
                });
            }

            if (filter > 4)
            {
                return null;
            }
        }

        var rgba = new byte[width * height * 4];
        for (int i = 0, j = 0; i < rows.Length; i += channels, j += 4)
        {
            rgba[j] = rows[i];
            rgba[j + 1] = rows[i + 1];
            rgba[j + 2] = rows[i + 2];
            rgba[j + 3] = channels == 4 ? rows[i + 3] : (byte)255;
        }

        return new Image(width, height, rgba);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>Pixels as a PNG.</summary>
    public static byte[] Encode(Image image)
    {
        var stride = image.Width * 4;
        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (var y = 0; y < image.Height; y++)
            {
                deflate.WriteByte(0);
                deflate.Write(image.Rgba, y * stride, stride);
            }
        }

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), image.Height);
        header[8] = 8;
        header[9] = 6;

        using var png = new MemoryStream();
        png.Write(Signature);
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, string type, ReadOnlySpan<byte> body)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, body.Length);
        png.Write(number);
        var typed = new byte[4 + body.Length];
        System.Text.Encoding.ASCII.GetBytes(type, typed);
        body.CopyTo(typed.AsSpan(4));
        png.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc(typed));
        png.Write(number);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
        {
            c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }

        return c;
    }).ToArray();

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        return c ^ 0xFFFFFFFFu;
    }
}
