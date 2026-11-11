namespace PortIT.Engine.Imaging;

public enum Filter { Box, Bilinear, Lanczos }

/// <summary>
/// Pillow's resize (libImaging/Resample.c), 8 bits a channel: per output pixel a normalised filter kernel over the
/// input, turned to 22-bit fixed point; the horizontal pass first, then the vertical. Same pixels as Pillow's
/// Image.resize(size, filter) for L / RGB images, and for RGBA too (Pillow premultiplies RGBA first:
/// <see cref="ResizeRgbaPremultiplied"/>; the engine's resize_rgba resizes RGB and A apart: <see cref="ResizeApart"/>).
/// </summary>
public static class Resample
{
    const int PrecisionBits = 32 - 8 - 2;

    static double FilterValue(Filter f, double x)
    {
        switch (f)
        {
            case Filter.Box:
                return x > -0.5 && x <= 0.5 ? 1.0 : 0.0;
            case Filter.Bilinear:
                if (x < 0.0) x = -x;
                return x < 1.0 ? 1.0 - x : 0.0;
            default:
                return -3.0 <= x && x < 3.0 ? Sinc(x) * Sinc(x / 3) : 0.0;
        }
    }

    static double Sinc(double x)
    {
        if (x == 0.0) return 1.0;
        x *= Math.PI;
        return Math.Sin(x) / x;
    }

    static double Support(Filter f) => f switch { Filter.Box => 0.5, Filter.Bilinear => 1.0, _ => 3.0 };

    /// <summary>-&gt; (kernel size, bounds [xmin, count] per output pixel, fixed-point weights)</summary>
    static (int Ksize, int[] Bounds, int[] Kk) Coeffs(int inSize, float in0, float in1, int outSize, Filter f)
    {
        double scale = (double)(in1 - in0) / outSize, filterscale = scale < 1.0 ? 1.0 : scale;
        double support = Support(f) * filterscale;
        int ksize = (int)Math.Ceiling(support) * 2 + 1;
        var kkd = new double[outSize * ksize];
        var bounds = new int[outSize * 2];
        for (int xx = 0; xx < outSize; xx++)
        {
            double center = in0 + (xx + 0.5) * scale, ww = 0.0, ss = 1.0 / filterscale;
            int xmin = (int)(center - support + 0.5);
            if (xmin < 0) xmin = 0;
            int xmax = (int)(center + support + 0.5);
            if (xmax > inSize) xmax = inSize;
            xmax -= xmin;
            int k = xx * ksize, x;
            for (x = 0; x < xmax; x++)
            {
                double w = FilterValue(f, (x + xmin - center + 0.5) * ss);
                kkd[k + x] = w;
                ww += w;
            }
            for (x = 0; x < xmax; x++) if (ww != 0.0) kkd[k + x] /= ww;
            for (; x < ksize; x++) kkd[k + x] = 0;
            bounds[xx * 2] = xmin;
            bounds[xx * 2 + 1] = xmax;
        }
        var kk = new int[kkd.Length];
        for (int i = 0; i < kkd.Length; i++)
            kk[i] = kkd[i] < 0 ? (int)(-0.5 + kkd[i] * (1 << PrecisionBits)) : (int)(0.5 + kkd[i] * (1 << PrecisionBits));
        return (ksize, bounds, kk);
    }

    static byte Clip8(int v)
    {
        v >>= PrecisionBits;
        return v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;
    }

    /// <summary>Pillow's Image.resize for an L / RGB image (an RGBA one is resized as it is, without premultiplying)</summary>
    public static Img Resize(Img im, int w, int h, Filter f)
    {
        if (w == im.W && h == im.H) return im.Copy();
        int ch = im.Ch;
        var (kh, bh, wh) = Coeffs(im.W, 0, im.W, w, f);
        var (kv, bv, wv) = Coeffs(im.H, 0, im.H, h, f);
        bool needH = w != im.W, needV = h != im.H;
        var src = im;
        int yFirst = 0;
        if (needH)
        {
            // (only the rows the vertical pass reads)
            yFirst = bv[0];
            int yLast = bv[h * 2 - 2] + bv[h * 2 - 1];
            if (!needV) { yFirst = 0; yLast = im.H; }
            var tmp = new Img(w, yLast - yFirst, ch);
            Parallel.For(0, yLast - yFirst, yy =>
            {
                int row = (yy + yFirst) * im.W * ch, orow = yy * w * ch;
                for (int xx = 0; xx < w; xx++)
                {
                    int xmin = bh[xx * 2], xmax = bh[xx * 2 + 1], k = xx * kh;
                    for (int c = 0; c < ch; c++)
                    {
                        int ss = 1 << (PrecisionBits - 1);
                        for (int x = 0; x < xmax; x++) ss += im.Px[row + (x + xmin) * ch + c] * wh[k + x];
                        tmp.Px[orow + xx * ch + c] = Clip8(ss);
                    }
                }
            });
            src = tmp;
        }
        if (!needV) return src;
        var o = new Img(w, h, ch);
        int sw = src.W;
        Parallel.For(0, h, yy =>
        {
            int ymin = bv[yy * 2] - (needH ? yFirst : 0), ymax = bv[yy * 2 + 1], k = yy * kv, orow = yy * w * ch;
            for (int xx = 0; xx < w; xx++)
                for (int c = 0; c < ch; c++)
                {
                    int ss = 1 << (PrecisionBits - 1);
                    for (int y = 0; y < ymax; y++) ss += src.Px[((y + ymin) * sw + xx) * ch + c] * wv[k + y];
                    o.Px[orow + xx * ch + c] = Clip8(ss);
                }
        });
        return o;
    }

    /// <summary>Pillow's Image.resize on an RGBA image: premultiplied (RGBa), resized, back to RGBA</summary>
    public static Img ResizeRgbaPremultiplied(Img im, int w, int h, Filter f)
    {
        if (im.Ch != 4) return Resize(im, w, h, f);
        if (w == im.W && h == im.H) return im.Copy();
        return Resize(im.Premultiply(), w, h, f).Unpremultiply();
    }

    /// <summary>w13_char.resize_rgba: RGB and A each resized on their own (no premultiplying); others as Resize</summary>
    public static Img ResizeApart(Img im, int w, int h, Filter f = Filter.Box)
    {
        if (im.Ch != 4) return Resize(im, w, h, f);
        return Resize(im.ToRgb(), w, h, f).WithAlpha(Resize(im.Channel(3), w, h, f));
    }
}
