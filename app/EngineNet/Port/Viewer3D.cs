using System.Buffers.Binary;
using PortIT.Engine.Build;
using PortIT.Engine.Formats;
using PortIT.Engine.Imaging;
using PortIT.Engine.Readers;

namespace PortIT.Engine.Port;

/// <summary>
/// The interactive 3D view of a built ch.pac (the user's request 2026-11-12: "like in Blender - rotate, look closer"):
/// the model is read once (Load), then drawn from any angle (Draw) by a software z-buffer, the screen cut into bands
/// that the CPU cores fill at once. The colour comes from the HD texture pack's .dds when the mod has one (sharper than
/// the copy inside ch.pac). Pixels come back as BGRA, ready for a bitmap.
/// </summary>
public static class Viewer3D
{
    public sealed class Part
    {
        public float[] V = [], N = [], UV = [];
        public int[] T = [];
        public bool Cut;          // (hair cards: drawn only where the colour map's alpha is over 90)
        public int Verts => V.Length / 3;
        public int Model;         // (its ch.pac child: 0 body, 0x2710 hair, 0x4e20 lashes)
        public int[] Inf = [];    // (4 per vertex: node index, -1 = none)
        public float[] Wt = [];
        public float[]? PV, PN;   // (posed by the hair test; null: the bind pose)
    }

    public sealed class Scene
    {
        public List<Part> Parts = [];
        public byte[]? Tex; public int TW, TH;            // (RGBA)
        public float[] Lo = new float[3], Hi = new float[3], Centre = new float[3];
        public int Verts, Tris;
        public float Height => Hi[1] - Lo[1];
        public Dictionary<int, List<Jboy.Node>> Nodes = [];
        public string? HairSettings, Capsules;
    }

    /// <summary>where the camera is: turned (yaw) round the up axis, tipped (pitch), Zoom x the fit-to-window size, and
    /// moved (PanX / PanY, in pixels)</summary>
    public sealed class Camera
    {
        public double Yaw, Pitch, Zoom = 1, PanX, PanY;
        public bool Lit = true, Textured = true, Wire;
        public Camera Clone() => (Camera)MemberwiseClone();
    }

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

    /// <summary>a built ch.pac (attire 0: the ring attire, 1: the entrance outfit) and, when given, the HD pack's colour .dds</summary>
    public static Scene Load(string pac, int attire = 0, string? colorDds = null)
    {
        var arc = Epk8.Read(File.ReadAllBytes(pac));
        var ents = arc.Groups[0].Entries;
        var blob = ents[Math.Min(attire, ents.Count - 1)].Data;
        var models = new List<Jboy>();
        var ids = new List<int>();
        string? hairText = null, capText = null;
        Img? color = null;
        foreach (var e in Pach.Read(blob))
        {
            var raw = Bpe.Unpack(e.Data);
            if (Jboy.IsJboy(raw) && e.Id is 0 or 0x2710 or 0x4e20) { models.Add(Jboy.Read(raw)); ids.Add((int)e.Id); }
            else if (e.Id == 0x3c) hairText = System.Text.Encoding.Latin1.GetString(raw);
            else if (e.Id == 0x3d) capText = System.Text.Encoding.Latin1.GetString(raw);
            else if (color == null && colorDds == null && Bundle.IsBundle(raw))
                foreach (var (name, dds) in SvrBuilder.TexBundleRead(raw))
                    if (name == "color" && dds.Length >= 4 && dds.AsSpan(0, 4).SequenceEqual("DDS "u8))
                        try { color = ImageIO.Decode(dds).ToRgba(); } catch (Exception) { }
        }
        if (colorDds != null && File.Exists(colorDds))
            try { color = ImageIO.Decode(File.ReadAllBytes(colorDds)).ToRgba(); } catch (Exception) { }
        var s = new Scene { Tex = color?.Px, TW = color?.W ?? 0, TH = color?.H ?? 0, HairSettings = hairText, Capsules = capText };
        for (int mi = 0; mi < models.Count; mi++) s.Nodes[ids[mi]] = models[mi].Nodes;
        for (int c = 0; c < 3; c++) { s.Lo[c] = float.PositiveInfinity; s.Hi[c] = float.NegativeInfinity; }
        for (int mi = 0; mi < models.Count; mi++)
            foreach (var me in models[mi].Meshes)
            {
                var T = TrisOf(me);
                if (T.Length == 0) continue;
                var p = new Part { T = T, Cut = (me.U88 & 0x110) != 0, Model = ids[mi] };
                int nw = Math.Max(1, me.Weights.Count), nv = me.Verts.Count;
                var flat = me.Weights.SelectMany(b => b).ToList();
                p.Inf = new int[4 * nv]; p.Wt = new float[4 * nv];
                Array.Fill(p.Inf, -1);
                for (int v = 0; v < nv; v++)
                    for (int k = 0; k < Math.Min(4, nw); k++)
                    {
                        if (v * nw + k >= flat.Count) break;
                        var (bs, w) = flat[v * nw + k];
                        if (bs[0] == 255 || bs[0] >= me.Palette.Count) continue;
                        p.Inf[4 * v + k] = me.Palette[bs[0]] - 1; p.Wt[4 * v + k] = w;
                    }
                p.V = new float[3 * me.Verts.Count]; p.N = new float[3 * me.Verts.Count]; p.UV = new float[2 * me.Uvs.Count];
                for (int i = 0; i < me.Verts.Count; i++)
                    for (int c = 0; c < 3; c++)
                    {
                        p.V[3 * i + c] = me.Verts[i].PosNrm[c]; p.N[3 * i + c] = me.Verts[i].PosNrm[3 + c];
                        s.Lo[c] = Math.Min(s.Lo[c], p.V[3 * i + c]); s.Hi[c] = Math.Max(s.Hi[c], p.V[3 * i + c]);
                    }
                for (int i = 0; i < me.Uvs.Count; i++) { p.UV[2 * i] = me.Uvs[i].U; p.UV[2 * i + 1] = me.Uvs[i].V; }
                if (p.UV.Length / 2 < p.Verts) Array.Resize(ref p.UV, 2 * p.Verts);
                s.Parts.Add(p);
                s.Verts += p.Verts; s.Tris += T.Length / 3;
            }
        for (int c = 0; c < 3; c++) s.Centre[c] = (s.Lo[c] + s.Hi[c]) / 2;
        return s;
    }

    /// <summary>the HD pack colour .dds of a mod folder (ring: the one without "entrance" in its name), or null</summary>
    public static string? HdColor(string modDir, bool entrance)
    {
        string tp = Path.Combine(modDir, "texture_pack");
        if (!Directory.Exists(tp)) return null;
        return Directory.EnumerateFiles(tp, "*.dds").FirstOrDefault(f =>
        {
            string n = Path.GetFileName(f).ToLowerInvariant();
            return n.Contains("_color_") && n.Contains("_entrance_") == entrance;
        });
    }

    /// <summary>the model drawn at W x H from the camera -&gt; BGRA bytes (W*H*4); smooth: bilinear texture (else nearest)</summary>
    /// <summary>(checks: the cut-out level - 90 as the preview; another to see what the game's would show)</summary>
    public static int AlphaRef = 90;

    public static byte[] Draw(Scene s, int W, int H, Camera cam, bool smooth = true)
    {
        var px = new byte[W * H * 4];
        // background: a soft vertical fade (the app's dark panel colours)
        for (int y = 0; y < H; y++)
        {
            double t = (double)y / Math.Max(H - 1, 1);
            byte r = (byte)(16 - 6 * t), g = (byte)(21 - 6 * t), b = (byte)(30 - 7 * t);
            for (int x = 0; x < W; x++) { int o = 4 * (y * W + x); px[o] = b; px[o + 1] = g; px[o + 2] = r; px[o + 3] = 255; }
        }
        if (s.Parts.Count == 0) return px;
        double cy = Math.Cos(cam.Yaw), sy = Math.Sin(cam.Yaw), cp = Math.Cos(cam.Pitch), sp = Math.Sin(cam.Pitch);
        // (model axes: x right, y down, z away from the front; turn round y, then tip round x)
        double scale = (H * 0.92) / Math.Max(s.Height, 1e-6) * cam.Zoom;
        double ox = W / 2.0 + cam.PanX, oy = H / 2.0 + cam.PanY;
        // light: from the viewer, a little up and left
        double lx = -0.35, ly = -0.45, lz = -0.82, ll = Math.Sqrt(lx * lx + ly * ly + lz * lz);
        lx /= ll; ly /= ll; lz /= ll;
        var proj = s.Parts.Select(p =>
        {
            int n = p.Verts;
            var sx = new float[n]; var syy = new float[n]; var sz = new float[n]; var sh = new float[n];
            for (int i = 0; i < n; i++)
            {
                var PVv = p.PV ?? p.V; var PNn = p.PN ?? p.N;
                double x = PVv[3 * i] - s.Centre[0], y = PVv[3 * i + 1] - s.Centre[1], z = PVv[3 * i + 2] - s.Centre[2];
                double x1 = x * cy - z * sy, z1 = x * sy + z * cy;
                double y2 = y * cp - z1 * sp, z2 = y * sp + z1 * cp;
                sx[i] = (float)(ox + x1 * scale); syy[i] = (float)(oy + y2 * scale); sz[i] = (float)z2;
                double nx = PNn[3 * i], ny = PNn[3 * i + 1], nz = PNn[3 * i + 2];
                double nx1 = nx * cy - nz * sy, nz1 = nx * sy + nz * cy;
                double ny2 = ny * cp - nz1 * sp, nz2 = ny * sp + nz1 * cp;
                double nn = Math.Max(Math.Sqrt(nx1 * nx1 + ny2 * ny2 + nz2 * nz2), 1e-9);
                double d = Math.Abs((nx1 * lx + ny2 * ly + nz2 * lz) / nn);
                sh[i] = cam.Lit ? (float)(0.32 + 0.68 * d) : 1f;
            }
            return (sx, syy, sz, sh);
        }).ToList();
        var zb = new float[W * H];
        Array.Fill(zb, float.PositiveInfinity);
        byte[]? tex = cam.Textured ? s.Tex : null;
        int tw = s.TW, th = s.TH;
        int bands = Math.Max(1, Math.Min(Environment.ProcessorCount * 2, H / 8));
        Parallel.For(0, bands, band =>
        {
            int r0 = band * H / bands, r1 = (band + 1) * H / bands - 1;
            for (int pi = 0; pi < s.Parts.Count; pi++)
            {
                var p = s.Parts[pi];
                var (sx, syy, sz, sh) = proj[pi];
                var T = p.T; var UV = p.UV;
                for (int t = 0; t < T.Length; t += 3)
                {
                    int i0 = T[t], i1 = T[t + 1], i2 = T[t + 2];
                    float ya = syy[i0], yb = syy[i1], yc = syy[i2];
                    int y0 = Math.Max(r0, (int)Math.Floor(Math.Min(ya, Math.Min(yb, yc))));
                    int y1 = Math.Min(r1, (int)Math.Ceiling(Math.Max(ya, Math.Max(yb, yc))));
                    if (y1 < y0) continue;
                    float xa = sx[i0], xb = sx[i1], xc = sx[i2];
                    int x0 = Math.Max(0, (int)Math.Floor(Math.Min(xa, Math.Min(xb, xc))));
                    int x1 = Math.Min(W - 1, (int)Math.Ceiling(Math.Max(xa, Math.Max(xb, xc))));
                    if (x1 < x0) continue;
                    float d = (xb - xa) * (yc - ya) - (xc - xa) * (yb - ya);
                    if (Math.Abs(d) < 1e-9f) continue;
                    float inv = 1f / d;
                    for (int py = y0; py <= y1; py++)
                    {
                        float gy = py + 0.5f;
                        for (int pxx = x0; pxx <= x1; pxx++)
                        {
                            float gx = pxx + 0.5f;
                            float b1 = ((gx - xa) * (yc - ya) - (xc - xa) * (gy - ya)) * inv;
                            float b2 = ((xb - xa) * (gy - ya) - (gx - xa) * (yb - ya)) * inv;
                            float b0 = 1 - b1 - b2;
                            if (b0 < 0 || b1 < 0 || b2 < 0) continue;
                            float z = b0 * sz[i0] + b1 * sz[i1] + b2 * sz[i2];
                            int at = py * W + pxx;
                            if (!(z < zb[at])) continue;
                            double r = 225, g = 225, bl = 230;
                            if (tex != null)
                            {
                                double u = b0 * UV[2 * i0] + b1 * UV[2 * i1] + b2 * UV[2 * i2];
                                double v = b0 * UV[2 * i0 + 1] + b1 * UV[2 * i1 + 1] + b2 * UV[2 * i2 + 1];
                                u -= Math.Floor(u); v -= Math.Floor(v);
                                double fx = u * (tw - 1), fy = v * (th - 1);
                                if (smooth)
                                {
                                    int ix = (int)fx, iy = (int)fy; double ax = fx - ix, ay = fy - iy;
                                    int ix1 = Math.Min(ix + 1, tw - 1), iy1 = Math.Min(iy + 1, th - 1);
                                    int o00 = 4 * (iy * tw + ix), o10 = 4 * (iy * tw + ix1), o01 = 4 * (iy1 * tw + ix), o11 = 4 * (iy1 * tw + ix1);
                                    double w00 = (1 - ax) * (1 - ay), w10 = ax * (1 - ay), w01 = (1 - ax) * ay, w11 = ax * ay;
                                    if (p.Cut && tex[o00 + 3] * w00 + tex[o10 + 3] * w10 + tex[o01 + 3] * w01 + tex[o11 + 3] * w11 <= AlphaRef) continue;
                                    r = tex[o00] * w00 + tex[o10] * w10 + tex[o01] * w01 + tex[o11] * w11;
                                    g = tex[o00 + 1] * w00 + tex[o10 + 1] * w10 + tex[o01 + 1] * w01 + tex[o11 + 1] * w11;
                                    bl = tex[o00 + 2] * w00 + tex[o10 + 2] * w10 + tex[o01 + 2] * w01 + tex[o11 + 2] * w11;
                                }
                                else
                                {
                                    int o = 4 * ((int)fy * tw + (int)fx);
                                    if (p.Cut && tex[o + 3] <= AlphaRef) continue;
                                    r = tex[o]; g = tex[o + 1]; bl = tex[o + 2];
                                }
                            }
                            double shd = b0 * sh[i0] + b1 * sh[i1] + b2 * sh[i2];
                            zb[at] = z;
                            int q = 4 * at;
                            px[q] = (byte)Math.Clamp(bl * shd, 0, 255);
                            px[q + 1] = (byte)Math.Clamp(g * shd, 0, 255);
                            px[q + 2] = (byte)Math.Clamp(r * shd, 0, 255);
                        }
                    }
                    if (cam.Wire)
                    {
                        // (the triangle's edges over it: Blender's wireframe overlay)
                        Line(px, W, r0, r1, xa, ya, xb, yb); Line(px, W, r0, r1, xb, yb, xc, yc); Line(px, W, r0, r1, xc, yc, xa, ya);
                    }
                }
            }
        });
        return px;
    }

    /// <summary>the hair test's pose put on the scene's meshes (linear blend skinning by the sim's skin matrices)</summary>
    public static void Pose(Scene s, HairSim sim)
    {
        var cache = new Dictionary<(int, int), double[]?>();
        double[]? Sk(int model, int b) { if (!cache.TryGetValue((model, b), out var m)) cache[(model, b)] = m = sim.Skin(model, b); return m; }
        foreach (var p in s.Parts)
        {
            if (!sim.Has(p.Model)) continue;
            int n = p.Verts;
            p.PV ??= new float[3 * n]; p.PN ??= new float[3 * n];
            for (int v = 0; v < n; v++)
            {
                double x = 0, y = 0, z = 0, nx = 0, ny = 0, nz = 0, tw = 0;
                for (int k = 0; k < 4; k++)
                {
                    int b = p.Inf[4 * v + k]; double w = p.Wt[4 * v + k];
                    if (b < 0 || w <= 0) continue;
                    var M = Sk(p.Model, b);
                    if (M == null) continue;
                    double px = p.V[3 * v], py = p.V[3 * v + 1], pz = p.V[3 * v + 2];
                    x += w * (M[0] * px + M[1] * py + M[2] * pz + M[3]); y += w * (M[4] * px + M[5] * py + M[6] * pz + M[7]); z += w * (M[8] * px + M[9] * py + M[10] * pz + M[11]);
                    double qx = p.N[3 * v], qy = p.N[3 * v + 1], qz = p.N[3 * v + 2];
                    nx += w * (M[0] * qx + M[1] * qy + M[2] * qz); ny += w * (M[4] * qx + M[5] * qy + M[6] * qz); nz += w * (M[8] * qx + M[9] * qy + M[10] * qz);
                    tw += w;
                }
                if (tw <= 0) { for (int c = 0; c < 3; c++) { p.PV[3 * v + c] = p.V[3 * v + c]; p.PN[3 * v + c] = p.N[3 * v + c]; } continue; }
                p.PV[3 * v] = (float)(x / tw); p.PV[3 * v + 1] = (float)(y / tw); p.PV[3 * v + 2] = (float)(z / tw);
                p.PN[3 * v] = (float)nx; p.PN[3 * v + 1] = (float)ny; p.PN[3 * v + 2] = (float)nz;
            }
        }
    }

    /// <summary>the hair test's simulation for a scene (null when the model has no moving hair)</summary>
    public static HairSim? Sim(Scene s)
    {
        var sim = new HairSim(s.Nodes, s.HairSettings, s.Capsules, s.Hi[1]);
        if (sim.Chains == 0 && s.HairSettings == null) { }
        sim.Reset();
        return sim;
    }

    static void Line(byte[] px, int W, int r0, int r1, float x0, float y0, float x1, float y1)
    {
        int n = (int)Math.Ceiling(Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0)));
        for (int k = 0; k <= n; k++)
        {
            float t = n == 0 ? 0 : (float)k / n;
            int x = (int)(x0 + (x1 - x0) * t), y = (int)(y0 + (y1 - y0) * t);
            if (y < r0 || y > r1 || x < 0 || x >= W) continue;
            int q = 4 * (y * W + x);
            px[q] = (byte)((px[q] + 255) / 2); px[q + 1] = (byte)((px[q + 1] + 190) / 2); px[q + 2] = (byte)((px[q + 2] + 90) / 2);
        }
    }
}
