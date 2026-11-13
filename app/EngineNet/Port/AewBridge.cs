using System.Text.RegularExpressions;
using PortIT.Engine.Imaging;
using PortIT.Engine.Readers;
using PortIT.Engine.Util;

namespace PortIT.Engine.Port;

/// <summary>
/// k26/aew_bridge.py: an AEW Fight Forever wrestler into the 2K pipeline - its meshes / bones in rock_fbx's space and
/// names (the skin split to M_Body / M_Head by J_Head's weights, the head's texture cut from the shared skin sheet,
/// AEW's own mouth with its occlusion baked, eyes, lashes, hair / beard cards, gear), the inside of the lips as
/// M_InnerMouth shaded as a mouth, a folder laid out like a 2K mod for rock_model's find_tex. Build stands in for
/// rock_fbx.build.
/// </summary>
public sealed class AewBridge
{
    readonly double[] AX = [1, 0, 0, 0, 0, 1, 0, 1, 0];
    /// <summary>(the head's lip sides: bones on the jaw's side / the upper lip's - AEW's names; an FBX model sets its own)</summary>
    readonly Regex JawRx = new(@"(^J_Jaw$|Lip_[LRC]_b_|Jaw_)"), UpperRx = new(@"Lip_[LRC]_u_");
    /// <summary>an FBX model (FbxBridge): the label used in the log</summary>
    readonly string Tag = "AEW";
    static readonly Dictionary<string, string> Rename = new()
    {
        ["H_Neck_00"] = "H_Neck_tw", ["H_Forearm1_L"] = "H_Elbow_L_tw01", ["H_Forearm2_L"] = "H_Elbow_L_tw02",
        ["H_Forearm1_R"] = "H_Elbow_R_tw01", ["H_Forearm2_R"] = "H_Elbow_R_tw02",
    };
    const int CropPad = 16;
    const double BeardAheadCm = 4.0, InnerDarkCm = 1.5, InnerFloor = 0.15, AoFloor = 0.12;
    static readonly Regex BeardBones = new("(Jaw|Cheek|Lip|Chin|Mouth)", RegexOptions.IgnoreCase);

    sealed class BMesh
    {
        public RockFbx.Mesh M = new();
        public string Slot = "";
        public BMesh Clone() => new()
        {
            Slot = Slot,
            M = new RockFbx.Mesh
            {
                Name = M.Name, Material = M.Material, Texture = M.Texture, Verts = (double[])M.Verts.Clone(), Normals = (double[])M.Normals.Clone(),
                Uv = (double[])M.Uv.Clone(), Tris = (int[])M.Tris.Clone(), Weights = M.Weights.Select(w => (w.Bone, (double[])w.W.Clone())).ToList(),
                LipSrc = (int[]?)M.LipSrc?.Clone(), JawSrc = (double[]?)M.JawSrc?.Clone(),
            },
        };
    }

    readonly Action<string> log;
    readonly SourceAew.Model src;
    public readonly string Folder, Char, Attire = "Attire", TexDir;
    public readonly bool Female;
    List<RockFbx.Bone> bones = [];
    List<BMesh> meshes = [];
    (int X, int Y, int W, int H, int SW, int SH)? crop;
    SourceAew.SourceMesh? skinMe;
    (double[] V, List<(int Bone, double[] W)> W)? skin;
    readonly HashSet<string> names = [];

    public AewBridge(string paks, string folder, string work, Action<string>? log = null)
    {
        this.log = log ?? (_ => { });
        src = SourceAew.Load(paks, folder);
        Folder = folder;
        Char = Path.Combine(work, "aew_char", folder);
        TexDir = Path.Combine(Char, Attire, "Textures");
        Directory.CreateDirectory(TexDir);
        Directory.CreateDirectory(Path.Combine(Char, "BaseModel", "Textures"));
        Female = src.Female;
        Make();
    }

    /// <summary>a model already in the 2K pipeline's axes (an FBX character, FbxBridge): its bones named as 2K's,
    /// uv v down (as AEW's), mesh kinds set; jaw / upper lip bones by the regexes given</summary>
    public AewBridge(SourceAew.Model model, string work, Regex jaw, Regex upper, string tag, Action<string>? log = null)
    {
        this.log = log ?? (_ => { });
        src = model;
        AX = [1, 0, 0, 0, 1, 0, 0, 0, 1];
        JawRx = jaw; UpperRx = upper; Tag = tag;
        Folder = model.Name;
        Char = Path.Combine(work, "fbx_char", model.Name);
        TexDir = Path.Combine(Char, Attire, "Textures");
        Directory.CreateDirectory(TexDir);
        Directory.CreateDirectory(Path.Combine(Char, "BaseModel", "Textures"));
        Female = model.Female;
        Make();
    }

    // ---------------------------------------------------------------- small helpers
    static Img BakeAo(Img col, Img orms)
    {
        col = col.ToRgba();
        var ao = Resample.Resize(orms.ToRgba().Channel(0), col.W, col.H, Filter.Bilinear);
        var o = new Img(col.W, col.H, 4);
        for (int i = 0; i < col.W * col.H; i++)
        {
            double f = AoFloor + (1 - AoFloor) * (ao.Px[i] / 255.0);
            for (int k = 0; k < 3; k++) o.Px[4 * i + k] = (byte)Math.Clamp(col.Px[4 * i + k] * f, 0, 255);
            o.Px[4 * i + 3] = col.Px[4 * i + 3];
        }
        return o;
    }

    static double[] Mean0(double[] V, IEnumerable<int> rows)
    {
        var m = new double[3]; int n = 0;
        foreach (int i in rows) { for (int c = 0; c < 3; c++) m[c] += V[3 * i + c]; n++; }
        for (int c = 0; c < 3; c++) m[c] /= n;
        return m;
    }

    /// <summary>per triangle: true on the head's own inside-of-the-mouth piece, or null</summary>
    static bool[]? MouthPiece(RockFbx.Mesh m, double[] eye, double iod)
    {
        int n = m.N;
        var comp = FullPort.Cards(n, m.Tris);
        int? best = null; int bestN = 0;
        foreach (int r in comp.Distinct().Order())
        {
            var sel = Enumerable.Range(0, n).Where(i => comp[i] == r).ToList();
            if (sel.Count > 0.25 * n) continue;
            var c = Mean0(m.Verts, sel);
            for (int k = 0; k < 3; k++) c[k] -= eye[k];
            if (0.6 * iod < -c[1] && -c[1] < 2.2 * iod && Math.Abs(c[0]) < 0.5 * iod)
                if (best == null || sel.Count > bestN) { best = r; bestN = sel.Count; }
        }
        if (best == null) return null;
        return Enumerable.Range(0, m.Tris.Length / 3).Select(t => comp[m.Tris[3 * t]] == best).ToArray();
    }

    /// <summary>a part of a mesh: the triangles sel and their vertices (weights that are 0 there dropped)</summary>
    static BMesh Sub(BMesh b, bool[] sel)
    {
        var m = b.M;
        var T = new List<int>();
        for (int t = 0; t < sel.Length; t++) if (sel[t]) T.AddRange([m.Tris[3 * t], m.Tris[3 * t + 1], m.Tris[3 * t + 2]]);
        var used = T.Distinct().Order().ToArray();
        var remap = new int[m.N]; Array.Fill(remap, -1);
        for (int i = 0; i < used.Length; i++) remap[used[i]] = i;
        double[] Take(double[] a, int k) { var o = new double[used.Length * k]; for (int i = 0; i < used.Length; i++) for (int c = 0; c < k; c++) o[k * i + c] = a[k * used[i] + c]; return o; }
        var w = new List<(int, double[])>();
        foreach (var (bn, wv) in m.Weights)
        {
            var x = used.Select(i => wv[i]).ToArray();
            if (x.Length > 0 && x.Max() > 0) w.Add((bn, x));
        }
        return new BMesh
        {
            Slot = b.Slot,
            M = new RockFbx.Mesh
            {
                Name = m.Name, Material = m.Material, Texture = m.Texture, Tris = T.Select(i => remap[i]).ToArray(), Verts = Take(m.Verts, 3),
                Normals = Take(m.Normals, 3), Uv = Take(m.Uv, 2), Weights = w, LipSrc = m.LipSrc == null ? null : used.Select(i => m.LipSrc[i]).ToArray(),
            },
        };
    }

    string? Tex(string tname, SourceAew.SourceMesh me, bool ao = false)
    {
        string? o = null;
        foreach (var (role, suffix) in new[] { ("color", "_color"), ("normal", "_nrm") })
        {
            if (!me.Images.TryGetValue(role, out var im)) continue;
            string path = Path.Combine(TexDir, tname + suffix + ".png");
            if (!File.Exists(path))
            {
                im = im.ToRgba();
                if (ao && role == "color" && me.Images.TryGetValue("orms", out var orms)) im = BakeAo(im, orms);
                ImageIO.SavePng(im, path);
            }
            if (role == "color") o = path;
        }
        return o;
    }

    static Img Crop(Img im, int x0, int y0, int x1, int y1)
    {
        var o = new Img(x1 - x0, y1 - y0, im.Ch);
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                if (x >= 0 && y >= 0 && x < im.W && y < im.H)
                    for (int c = 0; c < im.Ch; c++) o.Px[im.Ch * ((y - y0) * o.W + x - x0) + c] = im.Px[im.Ch * (y * im.W + x) + c];
        return o;
    }

    /// <summary>the head's part of AEW's skin sheet -&gt; head_color / _nrm.png cut to its texture box (+ CROP_PAD), uv moved
    /// onto it in place -&gt; the colour path</summary>
    string? TexCrop(string tname, SourceAew.SourceMesh me, double[] uv)
    {
        if (!me.Images.TryGetValue("color", out var col)) return null;
        int W = col.W, H = col.H, n = uv.Length / 2;
        var rows = Enumerable.Range(0, n).Select(i => 1.0 - uv[2 * i + 1]).ToArray();
        var us = Enumerable.Range(0, n).Select(i => uv[2 * i]).ToArray();
        int x0 = Math.Max(0, (int)Math.Floor(us.Min() * W) - CropPad), x1 = Math.Min(W, (int)Math.Ceiling(us.Max() * W) + CropPad);
        int y0 = Math.Max(0, (int)Math.Floor(rows.Min() * H) - CropPad), y1 = Math.Min(H, (int)Math.Ceiling(rows.Max() * H) + CropPad);
        string? o = null;
        foreach (var (role, suffix) in new[] { ("color", "_color"), ("normal", "_nrm") })
        {
            if (!me.Images.TryGetValue(role, out var im)) continue;
            double k0 = (double)im.W / W, k1 = (double)im.H / H;
            string path = Path.Combine(TexDir, tname + suffix + ".png");
            ImageIO.SavePng(Crop(im.ToRgba(), (int)(x0 * k0), (int)(y0 * k1), (int)(x1 * k0), (int)(y1 * k1)), path);
            if (role == "color") o = path;
        }
        crop = (x0, y0, x1 - x0, y1 - y0, W, H);
        for (int i = 0; i < n; i++)
        {
            uv[2 * i] = (uv[2 * i] * W - x0) / (x1 - x0);
            uv[2 * i + 1] = 1.0 - (rows[i] * H - y0) / (y1 - y0);
        }
        return o;
    }

    /// <summary>the inner lips' part of the head texture darkened as a mouth is: by AEW's occlusion where the skin has
    /// it, and by depth behind the lips' edge everywhere - the darker of the two</summary>
    void ShadeInner(RockFbx.Mesh inner)
    {
        if (string.IsNullOrEmpty(inner.Texture)) return;
        var im = ImageIO.Load(inner.Texture).ToRgba();
        int W = im.W, H = im.H, n = inner.N;
        double zmax = Enumerable.Range(0, n).Max(i => inner.Verts[3 * i + 2]);
        var f = Enumerable.Range(0, n).Select(i => Math.Clamp(1.0 - (zmax - inner.Verts[3 * i + 2]) / InnerDarkCm, InnerFloor, 1.0)).ToArray();
        var mask = new byte[W * H]; Array.Fill(mask, (byte)255);
        for (int t = 0; t < inner.Tris.Length; t += 3)
        {
            var pts = new List<(double, double)>();
            for (int k = 0; k < 3; k++) { int v = inner.Tris[t + k]; pts.Add((inner.Uv[2 * v] * W, (1 - inner.Uv[2 * v + 1]) * H)); }
            double mean = ((0.0 + f[inner.Tris[t]]) + f[inner.Tris[t + 1]] + f[inner.Tris[t + 2]]) / 3;
            Draw.Polygon(mask, W, H, pts, (byte)(int)(255 * mean));
        }
        var fac = mask.Select(x => x / 255.0).ToArray();
        if (skinMe != null && skinMe.Images.TryGetValue("orms", out var orms0) && crop != null)
        {
            var (cx, cy, cw, ch, SW, SH) = crop.Value;
            var orms = orms0.ToRgba();
            double k0 = (double)orms.W / SW, k1 = (double)orms.H / SH;
            var o = Crop(orms, (int)(cx * k0), (int)(cy * k1), (int)((cx + cw) * k0), (int)((cy + ch) * k1));
            var ao = Resample.Resize(o.Channel(0), W, H, Filter.Bilinear);
            for (int i = 0; i < W * H; i++)
                if (mask[i] < 255) fac[i] = Math.Min(fac[i], AoFloor + (1 - AoFloor) * (ao.Px[i] / 255.0));
        }
        for (int i = 0; i < W * H; i++)
            for (int k = 0; k < 3; k++) im.Px[4 * i + k] = (byte)Math.Clamp(im.Px[4 * i + k] * fac[i], 0, 255);
        ImageIO.SavePng(im, inner.Texture);
    }

    // ---------------------------------------------------------------- the pieces, once
    void Make()
    {
        var bnames = src.Bones.Select(b => b.Name).ToList();
        var P = src.Bones.Select(b => new[] { b.World[12], b.World[13], b.World[14] }).ToList();
        foreach (var b in src.Bones)
        {
            var w = RockModel.T4(t: Blas.MatVec3(AX, 3, P[bones.Count]));
            string nm = Rename.TryGetValue(b.Name, out var rn) ? rn : Regex.Replace(b.Name, @"^(J_\w+F3_[LR])_end$", "$1_stub");
            bones.Add(new RockFbx.Bone { Name = nm, Src = nm, Parent = b.Parent >= 0 ? b.Parent : null, World = w });
        }
        var kids = new Dictionary<int, List<int>>();
        for (int i = 0; i < src.Bones.Count; i++)
            if (src.Bones[i].Parent >= 0) { if (!kids.TryGetValue(src.Bones[i].Parent, out var l)) kids[src.Bones[i].Parent] = l = []; l.Add(i); }
        var head = new HashSet<int>();
        var stack = new Stack<int>();
        if (bnames.Contains("J_Head")) stack.Push(bnames.IndexOf("J_Head"));
        while (stack.Count > 0) { int i = stack.Pop(); head.Add(i); foreach (var k in kids.GetValueOrDefault(i, [])) stack.Push(k); }
        var beardb = Enumerable.Range(0, bnames.Count).Where(i => BeardBones.IsMatch(bnames[i]) && head.Contains(i)).ToHashSet();
        foreach (var me in src.Meshes)
        {
            int n = me.N;
            var V = Rows.TimesMatT(me.Positions, AX);
            var N = Rows.TimesMatT(me.Normals, AX);
            var uv = (double[])me.Uvs.Clone();
            for (int i = 0; i < n; i++) uv[2 * i + 1] = 1.0 - uv[2 * i + 1];
            var w = new List<(int Bone, double[] W)>();
            for (int c = 0; c < me.MaxInf; c++)
            {
                var bs = Enumerable.Range(0, n).Where(i => me.BoneW[me.MaxInf * i + c] > 0).Select(i => (int)me.BoneIdx[me.MaxInf * i + c]).Distinct().Order();
                foreach (int b in bs)
                {
                    int at = w.FindIndex(x => x.Bone == b);
                    var add = new double[n];
                    for (int i = 0; i < n; i++) add[i] = (at >= 0 ? w[at].W[i] : 0) + (me.BoneIdx[me.MaxInf * i + c] == b ? me.BoneW[me.MaxInf * i + c] : 0.0);
                    if (at >= 0) w[at] = (b, add); else w.Add((b, add));
                }
            }
            string key = Regex.Replace(me.Material, "^(mt|mi|s)_", "", RegexOptions.IgnoreCase);
            key = Regex.Replace(key, @"_\d+$", "");
            var bas = new BMesh { Slot = me.Material, M = new RockFbx.Mesh { Verts = V, Normals = N, Uv = uv, Tris = (int[])me.Tris.Clone(), Weights = w } };
            double[] SumOf(IEnumerable<int> bonesIn)
            {
                var s = new double[n];
                foreach (int b in bonesIn) { var e = w.FirstOrDefault(x => x.Bone == b); if (e.W != null) for (int i = 0; i < n; i++) s[i] += e.W[i]; }
                return s;
            }
            if (me.Kind is "head" or "headonly")
            {
                var inW = Enumerable.Range(0, bnames.Count).Where(b => w.Any(x => x.Bone == b));
                var jw = SumOf(inW.Where(b => JawRx.IsMatch(bnames[b])));
                var uw = SumOf(inW.Where(b => UpperRx.IsMatch(bnames[b])));
                bas.M.LipSrc = Enumerable.Range(0, n).Select(i => jw[i] > uw[i] && jw[i] > 0.05 ? 1 : uw[i] >= jw[i] && uw[i] > 0.05 ? -1 : 0).ToArray();
                var hw = SumOf(head.Order());
                int nt = bas.M.Tris.Length / 3;
                var th = Enumerable.Range(0, nt).Select(t => me.Kind == "headonly" || ((hw[bas.M.Tris[3 * t]] + hw[bas.M.Tris[3 * t + 1]]) + hw[bas.M.Tris[3 * t + 2]]) / 3 > 0.5).ToArray();
                foreach (var (nm, mat, s) in new[] { ("M_Body", "body", th.Select(x => !x).ToArray()), ("M_Head", "head", th) })
                {
                    if (!s.Any(x => x)) continue;
                    var part = Sub(bas, s);
                    part.M.Name = nm; part.M.Material = mat;
                    part.M.Texture = nm == "M_Head" ? TexCrop(mat, me, part.M.Uv) : Tex(mat, me);
                    meshes.Add(part);
                }
                skin = (V, w);
                skinMe = me;
            }
            else if (me.Kind == "body") { bas.M.Name = "M_Body"; bas.M.Material = "body"; bas.M.Texture = Tex("body", me); meshes.Add(bas); }
            else if (me.Kind == "teeth") { bas.M.Name = "M_AEWMouth"; bas.M.Material = "aewmouth"; bas.M.Texture = Tex("aewmouth", me, ao: true); meshes.Add(bas); }
            else if (me.Kind == "eye")
            {
                string side = Regex.IsMatch(me.Material, "_L(_|$)", RegexOptions.IgnoreCase) ? "l" : "r";
                bas.M.Name = "M_EYE"; bas.M.Material = "eye_" + side; bas.M.Texture = Tex("eye", me); meshes.Add(bas);
            }
            else if (me.Kind == "lash") { bas.M.Name = "M_TransB_eyelash"; bas.M.Material = "eyelash"; bas.M.Texture = Tex("eyelash", me); meshes.Add(bas); }
            else if (me.Kind == "hair") meshes.AddRange(Hair(bas, me, beardb));
            else
            {
                string nm = "M_" + key[..Math.Min(1, key.Length)].ToUpperInvariant() + key[Math.Min(1, key.Length)..], mat = key.ToLowerInvariant();
                int k = 1;
                while (names.Contains(nm + (k > 1 ? k.ToString() : ""))) k++;
                nm += k > 1 ? k.ToString() : ""; mat += k > 1 ? k.ToString() : "";
                names.Add(nm);
                if (me.Cutout) mat += "_ALPHA";
                bas.M.Name = nm; bas.M.Material = mat; bas.M.Texture = Tex(mat, me);
                meshes.Add(bas);
            }
        }
        // the inside of the lips -> M_InnerMouth (the face's texture, shaded as a mouth)
        int hi = meshes.FindIndex(m => m.M.Name == "M_Head");
        if (hi >= 0 && bnames.Contains("J_Eye_L") && bnames.Contains("J_Eye_R"))
        {
            var hm = meshes[hi];
            var eL = Blas.MatVec3(AX, 3, P[bnames.IndexOf("J_Eye_L")]); var eR = Blas.MatVec3(AX, 3, P[bnames.IndexOf("J_Eye_R")]);
            var bag = MouthPiece(hm.M, [(eL[0] + eR[0]) / 2, (eL[1] + eR[1]) / 2, (eL[2] + eR[2]) / 2], Blas.Norm(new[] { eL[0] - eR[0], eL[1] - eR[1], eL[2] - eR[2] }));
            if (bag != null)
            {
                var inner = Sub(hm, bag);
                inner.M.Name = "M_InnerMouth"; inner.M.Material = "head"; inner.M.Texture = hm.M.Texture;
                if (bnames.Contains("J_Jaw"))
                {
                    int jaw = bnames.IndexOf("J_Jaw");
                    var wi = inner.M.Weights;
                    foreach (int k in wi.Select(x => x.Bone).Where(k => Regex.IsMatch(bnames[k], "Lip_[LRC]_b_")).ToList())
                    {
                        int pk = wi.FindIndex(x => x.Bone == k);
                        var popped = wi[pk].W; wi.RemoveAt(pk);
                        int pj = wi.FindIndex(x => x.Bone == jaw);
                        if (pj >= 0) { var s = new double[popped.Length]; for (int i = 0; i < s.Length; i++) s[i] = wi[pj].W[i] + popped[i]; wi[pj] = (jaw, s); }
                        else wi.Add((jaw, popped.Select(x => 0 + x).ToArray()));
                    }
                }
                var rest = Sub(hm, bag.Select(x => !x).ToArray());
                meshes[hi] = rest;
                meshes.Add(inner);
                ShadeInner(inner.M);
                log($"{Tag}: the inside of the lips ({inner.M.N} vertices) -> M_InnerMouth");
            }
        }
        // winding: each mesh's triangles agree with its own normals again (AEW's swap of Y and Z mirrored them)
        foreach (var b in meshes)
        {
            var m = b.M;
            int nt = m.Tris.Length / 3;
            if (nt == 0) continue;
            int neg = 0, pos = 0;
            for (int t = 0; t < nt; t++)
            {
                int i0 = 3 * m.Tris[3 * t], i1 = 3 * m.Tris[3 * t + 1], i2 = 3 * m.Tris[3 * t + 2];
                double[] e1 = [m.Verts[i1] - m.Verts[i0], m.Verts[i1 + 1] - m.Verts[i0 + 1], m.Verts[i1 + 2] - m.Verts[i0 + 2]];
                double[] e2 = [m.Verts[i2] - m.Verts[i0], m.Verts[i2 + 1] - m.Verts[i0 + 1], m.Verts[i2 + 2] - m.Verts[i0 + 2]];
                double[] fn = [e1[1] * e2[2] - e1[2] * e2[1], e1[2] * e2[0] - e1[0] * e2[2], e1[0] * e2[1] - e1[1] * e2[0]];
                var nm = new double[3];
                for (int c = 0; c < 3; c++) nm[c] = ((m.Normals[i0 + c] + m.Normals[i1 + c]) + m.Normals[i2 + c]) / 3;
                double agree = (fn[0] * nm[0] + fn[2] * nm[2]) + fn[1] * nm[1];
                if (agree < 0) neg++; else if (agree > 0) pos++;
            }
            if (neg > pos)
                for (int t = 0; t < nt; t++) (m.Tris[3 * t + 1], m.Tris[3 * t + 2]) = (m.Tris[3 * t + 2], m.Tris[3 * t + 1]);
        }
        log($"{Tag}: {meshes.Count} meshes for the 2K pipeline - {string.Join(", ", meshes.Select(m => m.M.Name))}");
    }

    List<BMesh> Hair(BMesh bas, SourceAew.SourceMesh me, HashSet<int> beardb)
    {
        var V = bas.M.Verts; int n = bas.M.N;
        var comp = FullPort.Cards(n, bas.M.Tris);
        var P = new Dictionary<string, double[]>();
        foreach (var b in bones) P[b.Name] = [b.World[3], b.World[7], b.World[11]];
        double? eyeY = P.ContainsKey("J_Eye_L") && P.ContainsKey("J_Eye_R") ? (P["J_Eye_L"][1] + P["J_Eye_R"][1]) / 2 : null;
        double? hz = P.TryGetValue("J_Head", out var jh) ? jh[2] : null;
        var isBeard = new Dictionary<int, bool>();
        if (skin != null && eyeY != null && !Female)
        {
            var (SV, SW) = skin.Value;
            int ns = SV.Length / 3;
            foreach (int r in comp.Distinct().Order())
            {
                var vi = Enumerable.Range(0, n).Where(i => comp[i] == r).ToArray();
                var c = Mean0(V, vi);
                if (c[1] > eyeY - 1.0 || c[2] < hz + BeardAheadCm) continue;
                double best = 1e18; int sv = 0;
                for (int i0 = 0; i0 < ns; i0 += 4096)
                {
                    double dm = double.PositiveInfinity; int km = 0;
                    foreach (int v in vi)
                        for (int q = i0; q < Math.Min(ns, i0 + 4096); q++)
                        {
                            double dx = V[3 * v] - SV[3 * q], dy = V[3 * v + 1] - SV[3 * q + 1], dz = V[3 * v + 2] - SV[3 * q + 2];
                            double d = dx * dx + dy * dy + dz * dz;
                            if (d < dm) { dm = d; km = q; }
                        }
                    if (dm < best) { best = dm; sv = km; }
                }
                double bw = 0;
                foreach (int b in beardb.Order()) { var e = SW.FirstOrDefault(x => x.Bone == b); if (e.W != null) bw += e.W[sv]; }
                isBeard[r] = bw > 0.3;
            }
        }
        int nt = bas.M.Tris.Length / 3;
        var triBeard = Enumerable.Range(0, nt).Select(t => isBeard.GetValueOrDefault(comp[bas.M.Tris[3 * t]])).ToArray();
        log($"{Tag} hair: {comp.Distinct().Count()} cards, {isBeard.Values.Count(x => x)} of them beard");
        var o = new List<BMesh>();
        foreach (var (nm, mat, sel) in new[] { ("M_Hair", "hair", triBeard.Select(x => !x).ToArray()), ("M_Beard", "beard", triBeard) })
        {
            if (!sel.Any(x => x)) continue;
            int k = 1;
            while (names.Contains(nm + (k > 1 ? k.ToString() : ""))) k++;
            string sfx = k > 1 ? k.ToString() : "";
            names.Add(nm + sfx);
            var part = Sub(bas, sel);
            part.M.Name = nm + sfx; part.M.Material = mat + sfx + "_ALPHA"; part.M.Texture = Tex(mat + sfx + "_ALPHA", me);
            o.Add(part);
        }
        return o;
    }

    /// <summary>rock_fbx.build's place: copies of the meshes not dropped (by name or AEW slot), the report, the body
    /// under the gear hidden (rf.HideBody), the bones</summary>
    public (List<RockFbx.Mesh> Meshes, List<RockFbx.Bone> Bones) Build(RockFbx rf)
    {
        var ms = meshes.Where(m => !rf.DropMeshes.Contains(m.M.Name) && !rf.DropMeshes.Contains(m.Slot)).Select(m => m.Clone().M).ToList();
        rf.Report.Clear();
        foreach (var m in ms)
            rf.Report.Add($"{m.Name,-18} {m.N,6} verts {m.Tris.Length / 3,6} tris  {m.Material,-12} {m.Texture ?? "None"}  {m.Weights.Count} bones");
        rf.HideBody(ms);
        var bs = bones.Select(b => new RockFbx.Bone { Name = b.Name, Src = b.Src, Parent = b.Parent, World = (double[])b.World.Clone() }).ToList();
        return (ms, bs);
    }

    /// <summary>port_2k.hair_rgb for AEW: the hair's own colour map - the median of its opaque texels, made linear</summary>
    public (int R, int G, int B) HairRgb()
    {
        string p = Path.Combine(TexDir, "hair_ALPHA_color.png");
        if (File.Exists(p))
        {
            var a = ImageIO.Load(p).ToRgba();
            var sel = Enumerable.Range(0, a.W * a.H).Where(i => a.Px[4 * i + 3] > 128).ToList();
            if (sel.Count > 0)
            {
                var o = new int[3];
                for (int c = 0; c < 3; c++)
                {
                    var col = sel.Select(i => a.Px[4 * i + c] / 255.0).Order().ToArray();
                    int n = col.Length;
                    double med = n % 2 == 1 ? col[n / 2] : (col[n / 2 - 1] + col[n / 2]) / 2;
                    double lin = med <= 0.04045 ? med / 12.92 : Math.Pow((med + 0.055) / 1.055, 2.4);
                    o[c] = (int)Math.Round(255 * lin, MidpointRounding.ToEven);
                }
                return (o[0], o[1], o[2]);
            }
        }
        return (90, 70, 50);
    }
}
