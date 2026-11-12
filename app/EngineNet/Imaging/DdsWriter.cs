using System.Buffers.Binary;

namespace PortIT.Engine.Imaging;

/// <summary>
/// DXT1 / DXT5 DDS files with full mips - the engine's w13_char.dds_encode and full_port._dds_levels (which saved each
/// level through Pillow's DXT encoder). The header is Pillow's, with the engine's fixes: the mip-count flag, the mip
/// count, the top level's byte size as the linear size (the game steps through the mips by it) and the mipmap caps.
/// The block encoder is our own (principal-axis endpoints, two least-squares refinements, the decoder's exact palette
/// for matching); the check holds its error to Pillow's.
/// </summary>
public static class DdsWriter
{
    /// <summary>w13_char.dds_encode: every level from the full image (box filter, RGB and A apart)</summary>
    public static byte[] Encode(Img img, string fourcc) => Levels(img, fourcc, null);

    /// <summary>full_port._dds_levels (DXT5): as Encode, and in each smaller level the cut-out cells' alpha set to
    /// 0 / 255 at 96 (hair cards stay sharp in the distance)</summary>
    /// <summary>a test hook: every picture handed to the encoder (the parity check compares them with Python's)</summary>
    public static Action<Img, string>? OnEncode;
    /// <summary>(checks: the picture and the cut cells handed to <see cref="Levels"/>)</summary>
    public static Action<Img, IReadOnlyList<(int X, int Y, int W, int H)>?>? OnLevels;

    /// <summary>a hair card's pixel at or over this alpha is solid, under it clear (96 left Seth Rollins' beard thin and
    /// see-through in game; 48 keeps more of each strand)</summary>
    public const int CutAt = 48;

    public static byte[] Levels(Img img, string fourcc, IReadOnlyList<(int X, int Y, int W, int H)>? cutCells)
    {
        OnEncode?.Invoke(img, fourcc);
        OnLevels?.Invoke(img, cutCells);
        var top = img.ToRgba();
        int w = top.W, h = top.H;
        int mips = (int)Math.Log2(Math.Max(w, h)) + 1;
        bool dxt1 = fourcc == "DXT1";
        var body = new MemoryStream();
        for (int k = 0; k < mips; k++)
        {
            var lv = k == 0 ? top : Resample.ResizeApart(top, Math.Max(1, w >> k), Math.Max(1, h >> k));
            // (every level, the top too: the game keeps faint alpha, so a soft strand edge or a Lanczos ring showed as a
            //  solid block of card - Seth Rollins' beard, 2026-11-12)
            if (cutCells != null)
            {
                lv = lv.Copy();
                foreach (var (x, y, cw, ch) in cutCells)
                {
                    int x0 = x >> k, y0 = y >> k;
                    int x1 = Math.Max((x + cw) >> k, x0 + 1), y1 = Math.Max((y + ch) >> k, y0 + 1);
                    for (int yy = y0; yy < Math.Min(y1, lv.H); yy++)
                        for (int xx = x0; xx < Math.Min(x1, lv.W); xx++)
                        {
                            int i = (yy * lv.W + xx) * 4 + 3;
                            lv.Px[i] = lv.Px[i] >= CutAt ? (byte)255 : (byte)0;
                        }
                }
            }
            body.Write(Bcn.Encode(lv, dxt1));
        }
        var hdr = new byte[128];
        uint[] f = [0x20534444, 0x7C, 0x81007 | 0x20000, (uint)h, (uint)w,
                    (uint)(Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * (dxt1 ? 8 : 16)), 0, (uint)mips];
        for (int i = 0; i < f.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(4 * i), f[i]);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(76), 0x20);          // pixel format size
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(80), 0x4);           // FOURCC
        "DXT1"u8.CopyTo(hdr.AsSpan(84));
        if (!dxt1) hdr[87] = (byte)'5';
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(88), 0x20);          // (Pillow writes 32 here)
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(108), 0x1000 | 0x400008);
        var outb = new byte[128 + body.Length];
        hdr.CopyTo(outb, 0);
        body.Position = 0;
        body.ReadExactly(outb, 128, (int)body.Length);
        return outb;
    }
}

/// <summary>BC1 (DXT1, opaque) / BC3 (DXT5) block encoding</summary>
public static class Bcn
{
    public static byte[] Encode(Img rgba, bool dxt1)
    {
        int w = rgba.W, h = rgba.H, bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
        int bs = dxt1 ? 8 : 16;
        var o = new byte[bw * bh * bs];
        Parallel.For(0, bh, by =>
        {
            Span<byte> blk = stackalloc byte[64];
            for (int bx = 0; bx < bw; bx++)
            {
                // (pixels past the edge repeat the last row / column)
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                    {
                        int sx = Math.Min(bx * 4 + x, w - 1), sy = Math.Min(by * 4 + y, h - 1);
                        rgba.Px.AsSpan((sy * w + sx) * 4, 4).CopyTo(blk.Slice((y * 4 + x) * 4, 4));
                    }
                var dst = o.AsSpan((by * bw + bx) * bs, bs);
                if (dxt1) ColorBlock(blk, dst);
                else { AlphaBlock(blk, dst); ColorBlock(blk, dst[8..]); }
            }
        });
        return o;
    }

    static int To565(int r, int g, int b) => ((r * 31 + 127) / 255 << 11) | ((g * 63 + 127) / 255 << 5) | ((b * 31 + 127) / 255);

    static void From565(int c, out int r, out int g, out int b)
    {
        r = (c & 0xF800) >> 8; r |= r >> 5;
        g = (c & 0x7E0) >> 3; g |= g >> 6;
        b = (c & 0x1F) << 3; b |= b >> 5;
    }

    /// <summary>the 4-colour palette exactly as the decoders build it (Pillow's integer thirds)</summary>
    static void Palette(int c0, int c1, Span<int> p)
    {
        From565(c0, out p[0], out p[1], out p[2]);
        From565(c1, out p[3], out p[4], out p[5]);
        for (int k = 0; k < 3; k++) { p[6 + k] = (2 * p[k] + p[3 + k]) / 3; p[9 + k] = (p[k] + 2 * p[3 + k]) / 3; }
    }

    static int Match(ReadOnlySpan<byte> blk, int c0, int c1, out uint idx)
    {
        Span<int> p = stackalloc int[12];
        Palette(c0, c1, p);
        int err = 0;
        idx = 0;
        for (int n = 0; n < 16; n++)
        {
            int r = blk[4 * n], g = blk[4 * n + 1], b = blk[4 * n + 2], best = int.MaxValue, bi = 0;
            for (int k = 0; k < 4; k++)
            {
                int dr = r - p[3 * k], dg = g - p[3 * k + 1], db = b - p[3 * k + 2];
                int e = dr * dr + dg * dg + db * db;
                if (e < best) { best = e; bi = k; }
            }
            err += best;
            idx |= (uint)bi << (2 * n);
        }
        return err;
    }

    static void ColorBlock(ReadOnlySpan<byte> blk, Span<byte> dst)
    {
        // principal axis of the block's colours (power iteration on the covariance)
        double mr = 0, mg = 0, mb = 0;
        for (int n = 0; n < 16; n++) { mr += blk[4 * n]; mg += blk[4 * n + 1]; mb += blk[4 * n + 2]; }
        mr /= 16; mg /= 16; mb /= 16;
        double crr = 0, crg = 0, crb = 0, cgg = 0, cgb = 0, cbb = 0;
        for (int n = 0; n < 16; n++)
        {
            double r = blk[4 * n] - mr, g = blk[4 * n + 1] - mg, b = blk[4 * n + 2] - mb;
            crr += r * r; crg += r * g; crb += r * b; cgg += g * g; cgb += g * b; cbb += b * b;
        }
        double vr = 1, vg = 1, vb = 1;
        for (int it = 0; it < 8; it++)
        {
            double r = crr * vr + crg * vg + crb * vb, g = crg * vr + cgg * vg + cgb * vb, b = crb * vr + cgb * vg + cbb * vb;
            double m = Math.Max(Math.Abs(r), Math.Max(Math.Abs(g), Math.Abs(b)));
            if (m < 1e-9) break;
            vr = r / m; vg = g / m; vb = b / m;
        }
        double lo = double.MaxValue, hi = double.MinValue;
        int ilo = 0, ihi = 0;
        for (int n = 0; n < 16; n++)
        {
            double d = blk[4 * n] * vr + blk[4 * n + 1] * vg + blk[4 * n + 2] * vb;
            if (d < lo) { lo = d; ilo = n; }
            if (d > hi) { hi = d; ihi = n; }
        }
        int c0 = To565(blk[4 * ihi], blk[4 * ihi + 1], blk[4 * ihi + 2]);
        int c1 = To565(blk[4 * ilo], blk[4 * ilo + 1], blk[4 * ilo + 2]);
        int bestErr = Best(blk, ref c0, ref c1, out uint bestIdx);
        // least-squares refinement of the endpoints for the chosen indices, twice
        for (int pass = 0; pass < 2; pass++)
        {
            if (!Refine(blk, bestIdx, out int r0, out int r1)) break;
            int e = Best(blk, ref r0, ref r1, out uint idx);
            if (e < bestErr) { bestErr = e; c0 = r0; c1 = r1; bestIdx = idx; }
            else break;
        }
        // single-colour blocks and refinement collapsing to one 565 value: 4-colour mode needs c0 > c1
        Write(dst, c0, c1, bestIdx);
    }

    /// <summary>the endpoints ordered for the 4-colour mode (c0 &gt; c1, indices remapped) and matched</summary>
    static int Best(ReadOnlySpan<byte> blk, ref int c0, ref int c1, out uint idx)
    {
        if (c0 < c1) (c0, c1) = (c1, c0);
        if (c0 == c1)
        {
            // (equal endpoints decode in the 3-colour mode; nudge one so the 4-colour palette applies)
            if (c1 > 0) c1--; else c0++;
        }
        return Match(blk, c0, c1, out idx);
    }

    static bool Refine(ReadOnlySpan<byte> blk, uint idx, out int c0, out int c1)
    {
        // w0 = weight of endpoint 0 per index (0: 1, 1: 0, 2: 2/3, 3: 1/3)
        ReadOnlySpan<double> w0 = [1.0, 0.0, 2.0 / 3, 1.0 / 3];
        double aa = 0, bb = 0, ab = 0;
        Span<double> ax = stackalloc double[3], bx = stackalloc double[3];
        for (int n = 0; n < 16; n++)
        {
            double a = w0[(int)((idx >> (2 * n)) & 3)], b = 1 - a;
            aa += a * a; bb += b * b; ab += a * b;
            for (int c = 0; c < 3; c++) { ax[c] += a * blk[4 * n + c]; bx[c] += b * blk[4 * n + c]; }
        }
        double det = aa * bb - ab * ab;
        if (Math.Abs(det) < 1e-9) { c0 = c1 = 0; return false; }
        Span<int> e0 = stackalloc int[3], e1 = stackalloc int[3];
        for (int c = 0; c < 3; c++)
        {
            e0[c] = Math.Clamp((int)Math.Round((ax[c] * bb - bx[c] * ab) / det), 0, 255);
            e1[c] = Math.Clamp((int)Math.Round((bx[c] * aa - ax[c] * ab) / det), 0, 255);
        }
        c0 = To565(e0[0], e0[1], e0[2]);
        c1 = To565(e1[0], e1[1], e1[2]);
        return true;
    }

    static void Write(Span<byte> dst, int c0, int c1, uint idx)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(dst, (ushort)c0);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[2..], (ushort)c1);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[4..], idx);
    }

    /// <summary>BC3 alpha: the block's max / min in the 8-value mode, each pixel the nearest value</summary>
    static void AlphaBlock(ReadOnlySpan<byte> blk, Span<byte> dst)
    {
        int lo = 255, hi = 0;
        for (int n = 0; n < 16; n++) { int a = blk[4 * n + 3]; lo = Math.Min(lo, a); hi = Math.Max(hi, a); }
        Span<int> pal = stackalloc int[8];
        ulong bits = 0;
        if (hi == lo)
        {
            // (one value: a0 = a1, the 6-value mode's first entry)
            dst[0] = dst[1] = (byte)hi;
            for (int i = 2; i < 8; i++) dst[i] = 0;
            return;
        }
        int a0 = hi, a1 = lo;
        pal[0] = a0; pal[1] = a1;
        for (int i = 2; i < 8; i++) pal[i] = ((8 - i) * a0 + (i - 1) * a1) / 7;
        for (int n = 0; n < 16; n++)
        {
            int a = blk[4 * n + 3], best = int.MaxValue, bi = 0;
            for (int k = 0; k < 8; k++)
            {
                int e = Math.Abs(a - pal[k]);
                if (e < best) { best = e; bi = k; }
            }
            bits |= (ulong)bi << (3 * n);
        }
        dst[0] = (byte)a0; dst[1] = (byte)a1;
        for (int i = 0; i < 6; i++) dst[2 + i] = (byte)(bits >> (8 * i));
    }
}
