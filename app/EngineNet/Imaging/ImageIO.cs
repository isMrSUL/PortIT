using System.Buffers.Binary;
using SkiaSharp;

namespace PortIT.Engine.Imaging;

/// <summary>
/// image files in and out - the engine's Image.open / Image.save: DDS (<see cref="Dds"/>), PNG / JPEG / BMP / WEBP
/// through SkiaSharp (unpremultiplied, so the pixels are the file's), TGA (uncompressed and RLE). Modes as Pillow
/// opens them: a grey PNG is L, an RGB one RGB, anything with alpha (or a palette) RGBA.
/// </summary>
public static class ImageIO
{
    public static Img Load(string path) => Decode(File.ReadAllBytes(path), Path.GetExtension(path));

    public static Img Decode(byte[] data, string? ext = null)
    {
        if (Dds.IsDds(data)) return Dds.Decode(data);
        if ((ext ?? "").Equals(".tga", StringComparison.OrdinalIgnoreCase) && !IsPng(data)) return Tga(data);
        using var codec = SKCodec.Create(new SKMemoryStream(data)) ?? throw new InvalidDataException("unreadable image");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        var r = codec.GetPixels(info, bmp.GetPixels());
        if (r is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) throw new InvalidDataException("image: " + r);
        var rgba = new Img(info.Width, info.Height, 4, bmp.Bytes);
        int ch = IsPng(data) ? PngChannels(data) : 3;
        return ch switch { 1 => rgba.ToRgb().Channel(0), 3 => rgba.ToRgb(), _ => rgba };
    }

    static bool IsPng(ReadOnlySpan<byte> d) => d.Length > 26 && d[..8].SequenceEqual((byte[])[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

    /// <summary>a PNG's channels as Pillow opens it: grey -&gt; L, RGB -&gt; RGB (both 4 with a tRNS chunk), else RGBA</summary>
    static int PngChannels(ReadOnlySpan<byte> d)
    {
        int type = d[25];
        bool trns = false;
        int p = 8;
        while (p + 8 <= d.Length)
        {
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(d[p..]);
            var t = d.Slice(p + 4, 4);
            if (t.SequenceEqual("tRNS"u8)) trns = true;
            if (t.SequenceEqual("IDAT"u8)) break;
            p += 12 + len;
        }
        return type switch { 0 => trns ? 4 : 1, 2 => trns ? 4 : 3, _ => 4 };
    }

    /// <summary>a TGA file (types 2 / 3 / 10 / 11: true colour or grey, plain or RLE), rows top to bottom</summary>
    static Img Tga(byte[] d)
    {
        int idLen = d[0], cmapType = d[1], type = d[2];
        int cmapLen = d[5] | (d[6] << 8), cmapBits = d[7];
        int w = d[12] | (d[13] << 8), h = d[14] | (d[15] << 8), bits = d[16], desc = d[17];
        int bpp = bits / 8, p = 18 + idLen + (cmapType == 1 ? cmapLen * ((cmapBits + 7) / 8) : 0);
        bool grey = type is 3 or 11, rle = type is 10 or 11;
        int ch = grey ? 1 : bpp == 4 ? 4 : 3;
        var raw = new byte[w * h * bpp];
        if (!rle) Array.Copy(d, p, raw, 0, raw.Length);
        else
        {
            int o = 0;
            while (o < raw.Length)
            {
                int hdr = d[p++], n = (hdr & 0x7F) + 1;
                if ((hdr & 0x80) != 0) { for (int k = 0; k < n; k++) Array.Copy(d, p, raw, o + k * bpp, bpp); p += bpp; }
                else { Array.Copy(d, p, raw, o, n * bpp); p += n * bpp; }
                o += n * bpp;
            }
        }
        var im = new Img(w, h, ch);
        bool top = (desc & 0x20) != 0;
        for (int y = 0; y < h; y++)
        {
            int sy = top ? y : h - 1 - y;
            for (int x = 0; x < w; x++)
            {
                int s = (sy * w + x) * bpp, t = (y * w + x) * ch;
                if (grey) im.Px[t] = raw[s];
                else { im.Px[t] = raw[s + 2]; im.Px[t + 1] = raw[s + 1]; im.Px[t + 2] = raw[s]; if (ch == 4) im.Px[t + 3] = raw[s + 3]; }
            }
        }
        return im;
    }

    /// <summary>a PNG file (lossless; compression level 1 as the engine's work PNGs - the pixels are what count)</summary>
    public static void SavePng(Img im, string path)
    {
        var rgba = im.ToRgba();
        var info = new SKImageInfo(im.W, im.H, SKColorType.Rgba8888, im.Ch == 4 ? SKAlphaType.Unpremul : SKAlphaType.Opaque);
        using var bmp = new SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(rgba.Px, 0, bmp.GetPixels(), rgba.Px.Length);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        using var f = File.Create(path);
        data.SaveTo(f);
    }
}
