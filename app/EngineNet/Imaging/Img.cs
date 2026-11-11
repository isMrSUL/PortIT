namespace PortIT.Engine.Imaging;

/// <summary>
/// an 8-bit image, 1 (L), 3 (RGB) or 4 (RGBA) channels, rows top to bottom - the engine's stand-in for a Pillow image.
/// Conversions follow Pillow's (Convert.c): L -&gt; RGB(A) copies the level, RGB -&gt; RGBA adds alpha 255, RGBA -&gt; RGB drops it,
/// RGB(A) -&gt; L is ITU-R 601-2 luma (L = R * 299/1000 + G * 587/1000 + B * 114/1000, Pillow's rounding).
/// </summary>
public sealed class Img
{
    public readonly int W, H, Ch;
    public readonly byte[] Px;

    public Img(int w, int h, int ch, byte[]? px = null)
    {
        if (ch is not (1 or 3 or 4)) throw new ArgumentException("channels must be 1, 3 or 4");
        W = w; H = h; Ch = ch;
        Px = px ?? new byte[w * h * ch];
        if (Px.Length != w * h * ch) throw new ArgumentException("pixel buffer size");
    }

    public static Img Filled(int w, int h, params byte[] value)
    {
        var im = new Img(w, h, value.Length);
        for (int i = 0; i < w * h; i++) value.CopyTo(im.Px, i * value.Length);
        return im;
    }

    public string Mode => Ch switch { 1 => "L", 3 => "RGB", _ => "RGBA" };

    public Img Copy() => new(W, H, Ch, (byte[])Px.Clone());

    public Img ToRgba()
    {
        if (Ch == 4) return this;
        var o = new Img(W, H, 4);
        for (int i = 0, n = W * H; i < n; i++)
        {
            if (Ch == 1) { byte l = Px[i]; o.Px[4 * i] = o.Px[4 * i + 1] = o.Px[4 * i + 2] = l; }
            else { o.Px[4 * i] = Px[3 * i]; o.Px[4 * i + 1] = Px[3 * i + 1]; o.Px[4 * i + 2] = Px[3 * i + 2]; }
            o.Px[4 * i + 3] = 255;
        }
        return o;
    }

    public Img ToRgb()
    {
        if (Ch == 3) return this;
        var o = new Img(W, H, 3);
        for (int i = 0, n = W * H; i < n; i++)
        {
            if (Ch == 1) { byte l = Px[i]; o.Px[3 * i] = o.Px[3 * i + 1] = o.Px[3 * i + 2] = l; }
            else { o.Px[3 * i] = Px[4 * i]; o.Px[3 * i + 1] = Px[4 * i + 1]; o.Px[3 * i + 2] = Px[4 * i + 2]; }
        }
        return o;
    }

    /// <summary>Pillow's L conversion: L24 fixed point, (R * 19595 + G * 38470 + B * 7471 + 0x8000) &gt;&gt; 16</summary>
    public Img ToL()
    {
        if (Ch == 1) return this;
        var o = new Img(W, H, 1);
        for (int i = 0, n = W * H; i < n; i++)
        {
            int r = Px[Ch * i], g = Px[Ch * i + 1], b = Px[Ch * i + 2];
            o.Px[i] = (byte)((r * 19595 + g * 38470 + b * 7471 + 0x8000) >> 16);
        }
        return o;
    }

    /// <summary>one channel as an L image (Pillow getchannel)</summary>
    public Img Channel(int c)
    {
        var o = new Img(W, H, 1);
        for (int i = 0, n = W * H; i < n; i++) o.Px[i] = Px[Ch * i + c];
        return o;
    }

    /// <summary>this RGB(A) image with the given L image as its alpha (Pillow putalpha)</summary>
    public Img WithAlpha(Img a)
    {
        if (a.W != W || a.H != H || a.Ch != 1) throw new ArgumentException("alpha size");
        var o = ToRgba().Copy();
        for (int i = 0, n = W * H; i < n; i++) o.Px[4 * i + 3] = a.Px[i];
        return o;
    }

    /// <summary>Pillow's RGBA -&gt; RGBa (premultiplied): MULDIV255(c, a) = (t + (t &gt;&gt; 8)) &gt;&gt; 8, t = c * a + 128</summary>
    public Img Premultiply()
    {
        var o = Copy();
        for (int i = 0, n = W * H; i < n; i++)
        {
            int a = Px[4 * i + 3];
            for (int c = 0; c < 3; c++)
            {
                int t = Px[4 * i + c] * a + 128;
                o.Px[4 * i + c] = (byte)(((t >> 8) + t) >> 8);
            }
        }
        return o;
    }

    /// <summary>Pillow's RGBa -&gt; RGBA: alpha 0 or 255 as it is, else clip8(255 * c / a)</summary>
    public Img Unpremultiply()
    {
        var o = Copy();
        for (int i = 0, n = W * H; i < n; i++)
        {
            int a = Px[4 * i + 3];
            if (a == 0 || a == 255) continue;
            for (int c = 0; c < 3; c++) o.Px[4 * i + c] = (byte)Math.Min(255, 255 * Px[4 * i + c] / a);
        }
        return o;
    }

    public void Paste(Img src, int x0, int y0)
    {
        if (src.Ch != Ch) src = Ch switch { 1 => src.ToL(), 3 => src.ToRgb(), _ => src.ToRgba() };
        for (int y = 0; y < src.H; y++)
        {
            int ty = y0 + y;
            if (ty < 0 || ty >= H) continue;
            for (int x = 0; x < src.W; x++)
            {
                int tx = x0 + x;
                if (tx < 0 || tx >= W) continue;
                Buffer.BlockCopy(src.Px, (y * src.W + x) * Ch, Px, (ty * W + tx) * Ch, Ch);
            }
        }
    }
}
