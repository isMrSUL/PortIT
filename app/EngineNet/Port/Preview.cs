using System.Buffers.Binary;
using PortIT.Engine.Build;
using PortIT.Engine.Formats;
using PortIT.Engine.Imaging;
using PortIT.Engine.Readers;
using PortIT.Engine.Util;
using SkiaSharp;

namespace PortIT.Engine.Port;

/// <summary>
/// portit/preview.py: a z-buffer software render, front and side - of a built ch.pac (render) and of a source
/// wrestler before porting (render_source). The NumPy arithmetic as it runs there (float64 maths, a float32 depth
/// buffer and image, NumPy's float remainder for the texture wrap). The caption is drawn with SkiaSharp's default
/// font (Pillow's built-in font is not copied: the caption's pixels differ).
/// </summary>
public static class Preview
{
    const int Width = 1000, Height = 760;

    sealed class Part
    {
        public double[] V = [], N = [], UV = [];
        public int[] T = [];
        public float[]? Col; public int Cw, Ch;            // (colour texture, RGBA float32)
        public float[]? Cut; public int Aw, Ah, ACh; public double CutAt;   // (cut-out texture, channel, threshold)
        public float[] Solid = [200, 200, 205];
        public bool Textured, Highlight;
    }

    static double Mod1(double x)
    {
        double m = x % 1.0;
        if (m != 0) { if (m < 0) m += 1.0; }
        else m = 0.0;
        return m;
    }

    static float[] Floats(Img im)
    {
        var r = im.ToRgba();
        return r.Px.Select(b => (float)b).ToArray();
    }

    static (float[] Img, int W, int H) Draw(List<Part> parts, bool negateSide, Func<Part, int, double, double, (bool Keep, double R, double G, double B)> shade)
    {
        int W = Width, H = Height;
        var img = new float[H * W * 3];
        for (int i = 0; i < H * W; i++) { img[3 * i] = 13; img[3 * i + 1] = 17; img[3 * i + 2] = 24; }
        if (parts.Count == 0) return (img, W, H);
        var lo = new double[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var hi = new double[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        foreach (var p in parts)
            for (int i = 0; i < p.V.Length / 3; i++)
                for (int c = 0; c < 3; c++) { lo[c] = Math.Min(lo[c], p.V[3 * i + c]); hi[c] = Math.Max(hi[c], p.V[3 * i + c]); }
        double sc = (H - 50) / Math.Max(hi[1] - lo[1], 1e-6);
        double[] light = [0.35, -0.55, -0.75];
        double ln = Blas.Norm(light);
        for (int c = 0; c < 3; c++) light[c] /= ln;
        foreach (var (view, cx) in new[] { (0, W * 0.3), (1, W * 0.76) })
        {
            var zb = new float[H * W];
            Array.Fill(zb, float.PositiveInfinity);
            foreach (var p in parts)
            {
                int n = p.V.Length / 3;
                var sx = new double[n]; var sy = new double[n]; var depth = new double[n];
                for (int i = 0; i < n; i++)
                {
                    double x = view == 0 ? p.V[3 * i] - (lo[0] + hi[0]) / 2 : -(p.V[3 * i + 2] - (lo[2] + hi[2]) / 2);
                    sx[i] = cx + x * sc;
                    sy[i] = 25 + (p.V[3 * i + 1] - lo[1]) * sc;
                    depth[i] = view == 0 ? p.V[3 * i + 2] : p.V[3 * i];
                }
                for (int t = 0; t < p.T.Length; t += 3)
                {
                    int i0 = p.T[t], i1 = p.T[t + 1], i2 = p.T[t + 2];
                    double xs0 = sx[i0], xs1 = sx[i1], xs2 = sx[i2], ys0 = sy[i0], ys1 = sy[i1], ys2 = sy[i2];
                    int x0 = (int)Math.Max(0, Math.Floor(Math.Min(xs0, Math.Min(xs1, xs2)))), x1 = (int)Math.Min(W - 1, Math.Ceiling(Math.Max(xs0, Math.Max(xs1, xs2))));
                    int y0 = (int)Math.Max(0, Math.Floor(Math.Min(ys0, Math.Min(ys1, ys2)))), y1 = (int)Math.Min(H - 1, Math.Ceiling(Math.Max(ys0, Math.Max(ys1, ys2))));
                    if (x1 < x0 || y1 < y0) continue;
                    double d = (xs1 - xs0) * (ys2 - ys0) - (xs2 - xs0) * (ys1 - ys0);
                    if (Math.Abs(d) < 1e-9) continue;
                    for (int py = y0; py <= y1; py++)
                        for (int px = x0; px <= x1; px++)
                        {
                            double gx = px + 0.5, gy = py + 0.5;
                            double b1 = ((gx - xs0) * (ys2 - ys0) - (xs2 - xs0) * (gy - ys0)) / d;
                            double b2 = ((xs1 - xs0) * (gy - ys0) - (gx - xs0) * (ys1 - ys0)) / d;
                            double b0 = 1 - b1 - b2;
                            if (!(b0 >= 0 && b1 >= 0 && b2 >= 0)) continue;
                            double z = b0 * depth[i0] + b1 * depth[i1] + b2 * depth[i2];
                            if (view == 1 && negateSide) z = -z;
                            int at = py * W + px;
                            if (!(z < zb[at])) continue;
                            double u = Mod1(b0 * p.UV[2 * i0] + b1 * p.UV[2 * i1] + b2 * p.UV[2 * i2]);
                            double v = Mod1(b0 * p.UV[2 * i0 + 1] + b1 * p.UV[2 * i1 + 1] + b2 * p.UV[2 * i2 + 1]);
                            var (keep, r, g, bl) = shade(p, 0, u, v);
                            if (!keep) continue;
                            double nx = b0 * p.N[3 * i0] + b1 * p.N[3 * i1] + b2 * p.N[3 * i2];
                            double ny = b0 * p.N[3 * i0 + 1] + b1 * p.N[3 * i1 + 1] + b2 * p.N[3 * i2 + 1];
                            double nz = b0 * p.N[3 * i0 + 2] + b1 * p.N[3 * i1 + 2] + b2 * p.N[3 * i2 + 2];
                            double nn = Math.Max(Math.Sqrt(0.0 + nx * nx + ny * ny + nz * nz), 1e-9);
                            nx /= nn; ny /= nn; nz /= nn;
                            double dot = Math.FusedMultiplyAdd(nz, light[2], Math.FusedMultiplyAdd(nx, light[0], Math.FusedMultiplyAdd(ny, light[1], 0.0)));
                            double sh = 0.38 + 0.62 * Math.Abs(dot);
                            zb[at] = (float)z;
                            img[3 * at] = (float)Math.Clamp(r * sh, 0, 255);
                            img[3 * at + 1] = (float)Math.Clamp(g * sh, 0, 255);
                            img[3 * at + 2] = (float)Math.Clamp(bl * sh, 0, 255);
                        }
                }
            }
        }
        return (img, W, H);
    }

    static void Save(float[] img, int W, int H, string title, string outPath)
    {
        var im = new Img(W, H, 3);
        for (int i = 0; i < img.Length; i++) im.Px[i] = (byte)img[i];
        if (!string.IsNullOrEmpty(title))
        {
            var rgba = im.ToRgba();
            var info = new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using var bmp = new SKBitmap(info);
            System.Runtime.InteropServices.Marshal.Copy(rgba.Px, 0, bmp.GetPixels(), rgba.Px.Length);
            using (var canvas = new SKCanvas(bmp))
            using (var paint = new SKPaint { Color = new SKColor(150, 165, 190), IsAntialias = true, TextSize = 11, Typeface = SKTypeface.Default })
                canvas.DrawText(title, 10, 6 + 10, paint);
            var px = new byte[W * H * 4];
            System.Runtime.InteropServices.Marshal.Copy(bmp.GetPixels(), px, 0, px.Length);
            im = new Img(W, H, 4, px).ToRgb();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        ImageIO.SavePng(im, outPath);
    }

    // ---------------------------------------------------------------- the built ch.pac
    static int[] TrisOf(Jboy.Mesh m)
    {
        var o = new List<int>();
        foreach (var (_, idx) in m.Strips)
            for (int k = 0; k + 2 < idx.Length; k++)
            {
                int a = idx[k], b = idx[k + 1], c = idx[k + 2];
                if (a == b || b == c || a == c) continue;
                if (k % 2 == 0) o.AddRange([a, b, c]); else o.AddRange([b, a, c]);
            }
        return [.. o];
    }

    public static string Render(string pac, string outPath, int attire = 0)
    {
        var arc = Epk8.Read(File.ReadAllBytes(pac));
        var ents = arc.Groups[0].Entries;
        var blob = ents[Math.Min(attire, ents.Count - 1)].Data;
        var models = new List<Jboy>();
        Img? color = null;
        var textures = new Dictionary<string, Img>();
        foreach (var e in Pach.Read(blob))
        {
            var raw = Bpe.Unpack(e.Data);
            if (Jboy.IsJboy(raw) && e.Id is 0 or 0x2710 or 0x4e20) models.Add(Jboy.Read(raw));
            else if (Bundle.IsBundle(raw))
                foreach (var (name, dds) in SvrBuilder.TexBundleRead(raw))
                    if (dds.Length >= 4 && dds.AsSpan(0, 4).SequenceEqual("DDS "u8))
                        try { textures[name] = ImageIO.Decode(dds).ToRgba(); } catch (Exception) { }
        }
        textures.TryGetValue("color", out color);
        float[]? col = color != null ? Floats(color) : null;
        var parts = new List<Part>();
        foreach (var m in models)
            foreach (var me in m.Meshes)
            {
                var T = TrisOf(me);
                if (T.Length == 0) continue;
                var p = new Part { T = T, Textured = col != null, Col = col, Cw = color?.W ?? 1, Ch = color?.H ?? 1 };
                p.V = me.Verts.SelectMany(v => new double[] { v.PosNrm[0], v.PosNrm[1], v.PosNrm[2] }).ToArray();
                p.N = me.Verts.SelectMany(v => new double[] { v.PosNrm[3], v.PosNrm[4], v.PosNrm[5] }).ToArray();
                p.UV = me.Uvs.SelectMany(uv => new double[] { uv.U, uv.V }).ToArray();
                p.CutAt = (me.U88 & 0x110) != 0 ? 90 : -1;
                parts.Add(p);
            }
        var (img, W, H) = Draw(parts, true, (p, _, u, v) =>
        {
            if (!p.Textured) return (true, 235.0, 235.0, 240.0);
            int at = 4 * ((int)(v * (p.Ch - 1)) * p.Cw + (int)(u * (p.Cw - 1)));
            if (p.CutAt >= 0 && !(p.Col![at + 3] > p.CutAt)) return (false, 0, 0, 0);
            return (true, p.Col![at], p.Col[at + 1], p.Col[at + 2]);
        });
        Save(img, W, H, "the built ch.pac  -  front / side", outPath);
        return outPath;
    }

    // ---------------------------------------------------------------- the source, before porting
    /// <summary>(PortIT's "Show": the asked-for mesh tinted orange, so people see which piece a name is)</summary>
    static (bool, double, double, double) Hi(Part p, double r, double g, double b) =>
        p.Highlight ? (true, 0.35 * r + 0.65 * 255, 0.35 * g + 0.65 * 150, 0.35 * b + 0.65 * 40) : (true, r, g, b);

    public static string RenderSource(SourceAew.Model src, string outPath, int lod = 1, int texSide = 512, string title = "the 2K model, before porting", string? highlight = null)
    {
        var lods = new Dictionary<string, List<List<Mcd.LodMesh>>>();
        int[] TrisFor(SourceAew.SourceMesh me)
        {
            if (me.SourcePath == null) return me.Tris;
            if (!lods.TryGetValue(me.SourcePath, out var L))
            {
                try { L = Mcd.Load(me.SourcePath).Lods; } catch (Exception) { L = []; }
                lods[me.SourcePath] = L;
            }
            if (L.Count > lod)
                foreach (var row in L[lod]) if (row.Mesh == me.Index) return row.Triangles;
            return me.Tris;
        }
        var cache = new Dictionary<string, (float[] Px, int W, int H)?>();
        (float[] Px, int W, int H)? Image(string path)
        {
            if (!cache.TryGetValue(path, out var c))
            {
                try
                {
                    var im = ImageIO.Load(path).ToRgba();
                    if (Math.Max(im.W, im.H) > texSide) im = Resample.ResizeRgbaPremultiplied(im, texSide, Math.Max(1, texSide * im.H / im.W), Filter.Bilinear);
                    c = (Floats(im), im.W, im.H);
                }
                catch (Exception) { c = null; }
                cache[path] = c;
            }
            return c;
        }
        var parts = new List<Part>();
        foreach (var me in src.Meshes)
        {
            if (me.Variant || me.Kind == "teeth" || me.Material.ToLowerInvariant() is "m_tongue" or "m_teeth") continue;
            var T = TrisFor(me);
            if (T.Length == 0) continue;
            var p = new Part { V = me.Positions, N = me.Normals, UV = me.Uvs, T = T, Highlight = highlight != null && string.Equals(me.Material, highlight, StringComparison.OrdinalIgnoreCase) };
            var col = me.Textures.TryGetValue("color", out var cp) ? Image(cp) : null;
            (float[] Px, int W, int H)? cut = null; int cch = 0;
            if (me.Kind == "hair" && col != null && !me.Textures.ContainsKey("rai")) { cut = col; cch = 3; }
            else if (me.Kind == "hair" || (me.Cutout && col == null))
            {
                var rai = me.Textures.TryGetValue("rai", out var rp) ? Image(rp) : null;
                if (rai != null) { cut = rai; cch = 1; }
                var c = new double[] { .05, .04, .03 };
                if (me.Colors != null && me.Colors.Length > 0)
                    for (int k = 0; k < 3; k++)
                    {
                        var colv = Enumerable.Range(0, me.Colors.Length / me.ColorC).Select(i => me.Colors[me.ColorC * i + k]).Order().ToArray();
                        int n = colv.Length;
                        c[k] = n % 2 == 1 ? colv[n / 2] : (colv[n / 2 - 1] + colv[n / 2]) / 2;
                    }
                for (int k = 0; k < 3; k++) c[k] = c[k] <= 0.0031308 ? 12.92 * c[k] : 1.055 * Math.Pow(Math.Clamp(c[k], 0, 1), 1 / 2.4) - 0.055;
                p.Solid = c.Select(x => (float)(x * 255)).ToArray();
                col = null;
            }
            else if (me.Cutout && col != null) { cut = col; cch = 3; }
            if (col != null) { p.Col = col.Value.Px; p.Cw = col.Value.W; p.Ch = col.Value.H; }
            if (cut != null) { p.Cut = cut.Value.Px; p.Aw = cut.Value.W; p.Ah = cut.Value.H; p.ACh = cch; p.CutAt = 110; }
            parts.Add(p);
        }
        var (img, W, H) = Draw(parts, false, (p, _, u, v) =>
        {
            if (p.Cut != null && !(p.Cut[4 * ((int)(v * (p.Ah - 1)) * p.Aw + (int)(u * (p.Aw - 1))) + p.ACh] > 110)) return (false, 0, 0, 0);
            if (p.Col != null)
            {
                int at = 4 * ((int)(v * (p.Ch - 1)) * p.Cw + (int)(u * (p.Cw - 1)));
                return Hi(p, p.Col[at], p.Col[at + 1], p.Col[at + 2]);
            }
            return Hi(p, p.Solid[0], p.Solid[1], p.Solid[2]);
        });
        Save(img, W, H, string.IsNullOrEmpty(title) ? "" : title + "  -  front / side", outPath);
        return outPath;
    }
}
