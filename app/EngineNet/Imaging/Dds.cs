using System.Buffers.Binary;
using System.Numerics;

namespace PortIT.Engine.Imaging;

/// <summary>
/// DDS reading - the top level, as Pillow opens it (DdsImagePlugin + BcnDecode.c), so every later step sees the same
/// pixels: BC1 / BC2 / BC3 -&gt; RGBA, BC4 -&gt; L, BC5 -&gt; RGB, BC7 -&gt; RGBA (through Pfim's DirectXTex port, checked equal to
/// Pillow on 2K26 maps), plain 32 / 24 / 8-bit layouts. sRGB DXGI formats read as their plain twins (dds_compat.py).
/// </summary>
public static class Dds
{
    static readonly Dictionary<uint, uint> SrgbToPlain = new() { [72] = 71, [75] = 74, [78] = 77, [29] = 28, [91] = 87, [93] = 88, [99] = 98 };

    public static bool IsDds(ReadOnlySpan<byte> d) => d.Length >= 128 && d[..4].SequenceEqual("DDS "u8);

    public sealed record Info(int Width, int Height, int Mips, string Format);

    public static Info ReadInfo(ReadOnlySpan<byte> d)
    {
        if (!IsDds(d)) throw new InvalidDataException("not a DDS file");
        int h = (int)U32(d, 12), w = (int)U32(d, 16), mips = Math.Max(1, (int)U32(d, 28));
        return new Info(w, h, mips, FormatOf(d));
    }

    static uint U32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d[o..]);

    static string FormatOf(ReadOnlySpan<byte> d)
    {
        uint flags = U32(d, 80);
        string four = System.Text.Encoding.ASCII.GetString(d.Slice(84, 4));
        if ((flags & 0x4) != 0)
        {
            if (four == "DX10")
            {
                uint f = U32(d, 128);
                if (SrgbToPlain.TryGetValue(f, out var plain)) f = plain;
                return f switch
                {
                    71 or 70 => "BC1", 74 or 73 => "BC2", 77 or 76 => "BC3", 80 or 79 => "BC4", 81 => "BC4S",
                    83 or 82 => "BC5", 84 => "BC5S", 98 or 97 => "BC7", 28 or 27 => "RGBA8", 87 => "BGRA8", 88 => "BGRX8",
                    _ => "DXGI" + f,
                };
            }
            return four switch
            {
                "DXT1" => "BC1", "DXT2" or "DXT3" => "BC2", "DXT4" or "DXT5" => "BC3", "ATI1" or "BC4U" => "BC4",
                "BC4S" => "BC4S", "ATI2" or "BC5U" => "BC5", "BC5S" => "BC5S", _ => "FOURCC " + four,
            };
        }
        int bits = (int)U32(d, 88);
        return $"RAW{bits}";
    }

    /// <summary>the top level as Pillow's image (mode as Pillow opens it)</summary>
    public static Img Decode(byte[] data)
    {
        var d = data.AsSpan();
        var info = ReadInfo(d);
        int w = info.Width, h = info.Height;
        int off = System.Text.Encoding.ASCII.GetString(d.Slice(84, 4)) == "DX10" && (U32(d, 80) & 0x4) != 0 ? 148 : 128;
        var src = d[off..];
        switch (info.Format)
        {
            case "BC1": return Blocks(src, w, h, 4, 8, Bc1Block);
            case "BC2": return Blocks(src, w, h, 4, 16, Bc2Block);
            case "BC3": return Blocks(src, w, h, 4, 16, Bc3Block);
            case "BC4": return Blocks(src, w, h, 1, 8, (s, o) => Alpha(s, o, 0, 1));
            case "BC5": return Blocks(src, w, h, 3, 16, (s, o) => { Alpha(s, o, 0, 3); Alpha(s[8..], o, 1, 3); });
            case "BC7": return Bc7(data, w, h);
            case "RGBA8": return Plain(src, w, h, [0, 1, 2, 3], 4, 4);
            case "BGRA8": return Plain(src, w, h, [2, 1, 0, 3], 4, 4);
            case "BGRX8": return Plain(src, w, h, [2, 1, 0], 4, 3);
        }
        if (info.Format.StartsWith("RAW")) return Masked(d, src, w, h);
        throw new NotSupportedException("DDS format " + info.Format + " is not supported");
    }

    delegate void BlockFn(ReadOnlySpan<byte> src, Span<byte> out16px);

    /// <summary>block formats: each 4x4 block decoded to 16 pixels of `ch` channels, cropped at the edges</summary>
    static Img Blocks(ReadOnlySpan<byte> src, int w, int h, int ch, int blockBytes, BlockFn fn)
    {
        var im = new Img(w, h, ch);
        int bw = (w + 3) / 4, bh = (h + 3) / 4;
        Span<byte> px = stackalloc byte[16 * 4];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                px.Clear();
                fn(src.Slice((by * bw + bx) * blockBytes, blockBytes), px);
                for (int y = 0; y < 4; y++)
                {
                    int iy = by * 4 + y;
                    if (iy >= h) break;
                    for (int x = 0; x < 4; x++)
                    {
                        int ix = bx * 4 + x;
                        if (ix >= w) break;
                        px.Slice((y * 4 + x) * ch, ch).CopyTo(im.Px.AsSpan((iy * w + ix) * ch, ch));
                    }
                }
            }
        return im;
    }

    // ---- Pillow's BcnDecode.c arithmetic
    static void Decode565(int c, out int r, out int g, out int b)
    {
        r = (c & 0xF800) >> 8; r |= r >> 5;
        g = (c & 0x7E0) >> 3; g |= g >> 6;
        b = (c & 0x1F) << 3; b |= b >> 5;
    }

    /// <summary>the colour half of a BC1/2/3 block into RGBA pixels; separateAlpha: always the 4-colour mode (BC2/3)</summary>
    static void Color(ReadOnlySpan<byte> s, Span<byte> o, bool separateAlpha)
    {
        int c0 = s[0] | (s[1] << 8), c1 = s[2] | (s[3] << 8);
        uint lut = BinaryPrimitives.ReadUInt32LittleEndian(s[4..]);
        Span<int> p = stackalloc int[16];
        Decode565(c0, out p[0], out p[1], out p[2]); p[3] = 255;
        Decode565(c1, out p[4], out p[5], out p[6]); p[7] = 255;
        if (c0 > c1 || separateAlpha)
        {
            for (int k = 0; k < 3; k++) { p[8 + k] = (2 * p[k] + p[4 + k]) / 3; p[12 + k] = (p[k] + 2 * p[4 + k]) / 3; }
            p[11] = 255; p[15] = 255;
        }
        else
        {
            for (int k = 0; k < 3; k++) { p[8 + k] = (p[k] + p[4 + k]) / 2; p[12 + k] = 0; }
            p[11] = 255; p[15] = 0;
        }
        for (int n = 0; n < 16; n++)
        {
            int cw = (int)(3 & (lut >> (2 * n)));
            o[4 * n] = (byte)p[cw * 4]; o[4 * n + 1] = (byte)p[cw * 4 + 1]; o[4 * n + 2] = (byte)p[cw * 4 + 2];
            if (!separateAlpha) o[4 * n + 3] = (byte)p[cw * 4 + 3];
        }
    }

    /// <summary>a BC3 / BC4 / BC5 alpha half (8 bytes) into channel `chan` of pixels `stride` bytes apart</summary>
    static void Alpha(ReadOnlySpan<byte> s, Span<byte> o, int chan, int stride)
    {
        Span<int> a = stackalloc int[8];
        a[0] = s[0]; a[1] = s[1];
        if (a[0] > a[1])
            for (int i = 2; i < 8; i++) a[i] = ((8 - i) * a[0] + (i - 1) * a[1]) / 7;
        else
        {
            for (int i = 2; i < 6; i++) a[i] = ((6 - i) * a[0] + (i - 1) * a[1]) / 5;
            a[6] = 0; a[7] = 255;
        }
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)s[2 + i] << (8 * i);
        for (int n = 0; n < 16; n++) o[n * stride + chan] = (byte)a[(int)((bits >> (3 * n)) & 7)];
    }

    static void Bc1Block(ReadOnlySpan<byte> s, Span<byte> o) => Color(s, o, false);

    static void Bc2Block(ReadOnlySpan<byte> s, Span<byte> o)
    {
        Color(s[8..], o, true);
        for (int n = 0; n < 16; n++)
        {
            int v = (s[n / 2] >> (4 * (n & 1))) & 0xF;
            o[4 * n + 3] = (byte)((v << 4) | v);
        }
    }

    static void Bc3Block(ReadOnlySpan<byte> s, Span<byte> o)
    {
        Color(s[8..], o, true);
        Alpha(s, o, 3, 4);
    }

    static Img Bc7(byte[] data, int w, int h)
    {
        // (Pfim decodes the whole file; only the top level is kept - an sRGB tag read as its plain twin first)
        var bytes = data;
        uint f = U32(data, 128);
        if (System.Text.Encoding.ASCII.GetString(data, 84, 4) == "DX10" && SrgbToPlain.TryGetValue(f, out var plain))
        {
            bytes = (byte[])data.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(128), plain);
        }
        using var ms = new MemoryStream(bytes);
        using var img = Pfim.Pfimage.FromStream(ms);
        var im = new Img(w, h, 4);
        int bpp = img.BitsPerPixel / 8;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int si = y * img.Stride + x * bpp, di = (y * w + x) * 4;
                im.Px[di] = img.Data[si + 2]; im.Px[di + 1] = img.Data[si + 1]; im.Px[di + 2] = img.Data[si];
                im.Px[di + 3] = bpp == 4 ? img.Data[si + 3] : (byte)255;
            }
        return im;
    }

    static Img Plain(ReadOnlySpan<byte> src, int w, int h, int[] order, int bpp, int ch)
    {
        var im = new Img(w, h, ch);
        for (int i = 0; i < w * h; i++)
            for (int c = 0; c < ch; c++) im.Px[i * ch + c] = src[i * bpp + order[c]];
        return im;
    }

    /// <summary>plain layouts by their bit masks (32-bit with / without alpha, 24-bit, 8-bit luminance)</summary>
    static Img Masked(ReadOnlySpan<byte> d, ReadOnlySpan<byte> src, int w, int h)
    {
        uint flags = U32(d, 80);
        int bits = (int)U32(d, 88);
        uint[] masks = [U32(d, 92), U32(d, 96), U32(d, 100), U32(d, 104)];
        bool alpha = (flags & 0x1) != 0 && masks[3] != 0;
        int bpp = bits / 8;
        if (bpp == 1) return new Img(w, h, 1, src[..(w * h)].ToArray());
        int ch = alpha ? 4 : 3;
        var im = new Img(w, h, ch);
        for (int i = 0; i < w * h; i++)
        {
            uint v = 0;
            for (int k = 0; k < bpp; k++) v |= (uint)src[i * bpp + k] << (8 * k);
            for (int c = 0; c < ch; c++)
            {
                uint m = masks[c];
                if (m == 0) continue;
                int shift = BitOperations.TrailingZeroCount(m);
                uint max = m >> shift;
                uint val = (v & m) >> shift;
                im.Px[i * ch + c] = (byte)(max == 255 ? val : val * 255 / max);
            }
        }
        return im;
    }
}
