using System.Text.RegularExpressions;
using PortIT.Engine.Imaging;
using PortIT.Engine.Readers;
using PortIT.Engine.Util;

namespace PortIT.Engine.Port;

/// <summary>
/// A 2K wrestler (base model + attire) -&gt; meshes and bones for the Rock / Brock pipeline - port of k26/rock_fbx.py as
/// PortIT runs it (rock_fbx.covered wrapped by full_port.install: skin under gear kept KEEP_EDGE_CM in from the gear's
/// edges; QUICK_READ skips the test). Space: +Y up (TURN), cm. Arithmetic in NumPy's order (Util/Rows, Blas).
/// </summary>
public sealed class RockFbx
{
    public sealed class Mesh
    {
        public string Name = "", Material = "";
        public string? Texture;
        public double[] Verts = [], Normals = [], Uv = [];   // n x 3, n x 3, n x 2
        public int[] Tris = [];                                // n x 3
        /// <summary>bone index -&gt; per-vertex weight (insertion ordered, as the Python dict)</summary>
        public List<(int Bone, double[] W)> Weights = [];
        public int[]? LipSrc;
        public double[]? JawSrc;
        public int N => Verts.Length / 3;
    }

    public sealed class Bone { public string Name = "", Src = ""; public int? Parent; public double[] World = new double[16]; }

    // (rock_names.py)
    public static readonly HashSet<string> Locators = ["vector", "J_Dock", "J_Opponent", "J_Cam_Offset", "J_Cam1", "J_Cam1_fov", "J_Cam2", "J_Cam2_fov", "J_FootIK_R", "J_FootIK_L", "J_anim_origin"];

    public static string Rename(string n)
    {
        var centre = new Dictionary<string, string> { ["J_Root"] = "root", ["J_Hips"] = "pelvis", ["J_Spine1"] = "spine_01", ["J_Spine2"] = "spine_02", ["J_Chest"] = "spine_03", ["J_Neck"] = "neck_01", ["J_Head"] = "head", ["J_Jaw"] = "jaw" };
        if (centre.TryGetValue(n, out var c)) return c;
        foreach (var (s, t) in new[] { ("_L", "_l"), ("_R", "_r") })
        {
            if (!n.EndsWith(s)) continue;
            var b = n[..^2];
            var limb = new Dictionary<string, string?> { ["J_Clavicle"] = "clavicle", ["J_Shoulder"] = "upperarm", ["J_Elbow"] = "lowerarm", ["J_Wrist"] = "hand", ["J_Leg"] = "thigh", ["J_Knee"] = "calf", ["J_Foot"] = "foot", ["J_Toe"] = "ball", ["J_Eye"] = "eye", [$"H_Elbow{s}_tw01"] = null };
            if (limb.TryGetValue(b, out var l)) return l + t;
            foreach (var (f, u) in new[] { ("Index", "index"), ("Middle", "middle"), ("Ring", "ring"), ("Pinky", "pinky"), ("Thumb", "thumb") })
            {
                for (int k = 1; k <= 3; k++) if (b == $"J_{f}F{k}") return $"{u}_{k:00}{t}";
                if (b == $"J_{f}F0") return $"{u}_metacarpal{t}";
            }
        }
        foreach (var (s, t) in new[] { ("_L", "_l"), ("_R", "_r") })
            foreach (var k in new[] { "01", "02" })
                if (n == $"H_Elbow{s}_tw{k}") return $"lowerarm_twist_{k}{t}";
        return n;
    }

    // settings (module globals in Python; port_2k sets them)
    public HashSet<string> DropMeshes = ["M_Teeth", "M_Tongue", "M_MouthBag"];
    public HashSet<string> NotGear = ["M_Body", "M_Head", "M_EYE", "M_TransB_eyelash", "M_Teeth", "M_Tongue", "M_MouthBag"];
    public double HideOutCm = 4.0, HideInCm = 8.0, KeepEdgeCm = 3.0;
    public bool SkipVariants = true, QuickRead = false;
    /// <summary>alternate looks used all the same (PortIT 2026-11-11: an entrance outfit's M_Hair_Entrance, M_HoodUp...)</summary>
    public HashSet<string> UseVariants = new(StringComparer.OrdinalIgnoreCase);
    public const int TexMax = 2048;
    public const double Au27MinCm = 0.5;
    public readonly Dictionary<string, Dictionary<string, string>> TexAlias = [];
    Source2kTextures? aliasTex;
    public readonly List<string> Report = [];

    static readonly double[] Turn = [1, 0, 0, 0, -1, 0, 0, 0, -1];

    // ---------------------------------------------------------------- textures
    public Dictionary<string, string> MaterialAlias(string chr, IList<string> folders, string mat)
    {
        string key = Regex.Replace(mat, "(?i):skinned$", "").ToLowerInvariant();
        if (TexAlias.TryGetValue(key, out var hit)) return hit;
        aliasTex ??= new Source2kTextures(chr);
        var o = new Dictionary<string, string>();
        var others = Directory.Exists(chr)
            ? Directory.EnumerateDirectories(chr).Where(d => !Path.GetFileName(d).Equals("basemodel", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(d).StartsWith('.') && !Path.GetFileName(d).StartsWith('_'))
                .OrderBy(d => !Path.GetFileName(d).ToLowerInvariant().Contains("default")).ThenBy(d => d.ToLowerInvariant(), StringComparer.Ordinal).ToList()
            : [];
        var shared = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(chr)))!, "_Shared");
        var seen = new HashSet<string>();
        foreach (var f in folders.Concat(others).Append(shared))
        {
            var norm = Path.GetFullPath(f).ToLowerInvariant().TrimEnd('\\');
            if (!seen.Add(norm)) continue;
            var md = Path.Combine(f, "Materials");
            if (!Directory.Exists(md)) continue;
            var j = Directory.EnumerateFiles(md).FirstOrDefault(x => Path.GetFileName(x).ToLowerInvariant() == key + ".jmtl");
            if (j != null)
            {
                var t = aliasTex.MaterialTextures(j);
                if (t.TryGetValue("color", out var cc)) o["_color"] = cc;
                if (t.TryGetValue("nrm", out var nn)) o["_nrm"] = nn;
                break;
            }
        }
        TexAlias[key] = o;
        return o;
    }

    static void SaveTex(string src, string dst)
    {
        var im = ImageIO.Load(src).ToRgba();
        if (Math.Max(im.W, im.H) > TexMax)
        {
            double k = (double)TexMax / Math.Max(im.W, im.H);
            im = Resample.ResizeRgbaPremultiplied(im, (int)(im.W * k), (int)(im.H * k), Filter.Lanczos);
        }
        ImageIO.SavePng(im, dst);
    }

    /// <summary>rock_fbx.texture_for: the colour texture by the material's name (tried with _l / _r cut, then the .mtls
    /// name), else its own hashes' -&gt; 'textures/x_color.png' (written, at most 2048) or null</summary>
    public static string? TextureFor(string material, IList<string> folders, string outDir, string? extra, string? alias)
    {
        var bas = material.Split(':')[0];
        var cands = new List<string> { bas, bas.EndsWith("_l") || bas.EndsWith("_r") ? bas[..bas.LastIndexOf('_')] : bas };
        if (extra != null && extra.ToLowerInvariant().EndsWith("_color")) cands.Add(extra[..^"_color".Length]);
        foreach (var folder in folders)
            foreach (var c in cands)
            {
                var td = Path.Combine(folder, "Textures");
                var hits = Directory.Exists(td) ? Directory.EnumerateFiles(td).Where(p => Path.GetFileName(p).ToLowerInvariant().Split('.')[0] == (c + "_color").ToLowerInvariant()).ToList() : [];
                if (hits.Count > 0)
                {
                    var name = c + "_color.png";
                    var dst = Path.Combine(outDir, "textures", name);
                    if (!File.Exists(dst)) SaveTex(hits[0], dst);
                    return "textures/" + name;
                }
            }
        if (alias != null && File.Exists(alias))
        {
            var name = Regex.Replace(bas, "(?i):skinned$", "") + "_color.png";
            var dst = Path.Combine(outDir, "textures", name);
            if (!File.Exists(dst)) SaveTex(alias, dst);
            return "textures/" + name;
        }
        return null;
    }

    // ---------------------------------------------------------------- the hide-under-gear test
    /// <summary>rock_fbx.covered: per point, an attire triangle along its normal within out_cm outside or in_cm inside</summary>
    public static bool[] Covered(double[] verts, double[] normals, double[] tri, double outCm = 6.0, double inCm = 1.5, double cell = 8.0)
    {
        int nt = tri.Length / 9, n = verts.Length / 3;
        var A = new double[3 * nt]; var e1 = new double[3 * nt]; var e2 = new double[3 * nt];
        var grid = new Dictionary<(long, long, long), List<int>>();
        for (int i = 0; i < nt; i++)
        {
            double[] cen = new double[3];
            for (int c = 0; c < 3; c++)
            {
                double a = tri[9 * i + c], b = tri[9 * i + 3 + c], cc = tri[9 * i + 6 + c];
                A[3 * i + c] = a; e1[3 * i + c] = b - a; e2[3 * i + c] = cc - a;
                cen[c] = (a + b + cc) / 3;
            }
            var key = ((long)Math.Floor(cen[0] / cell), (long)Math.Floor(cen[1] / cell), (long)Math.Floor(cen[2] / cell));
            if (!grid.TryGetValue(key, out var l)) grid[key] = l = [];
            l.Add(i);
        }
        var res = new bool[n];
        for (int vi = 0; vi < n; vi++)
        {
            double[] p = [verts[3 * vi], verts[3 * vi + 1], verts[3 * vi + 2]], nv = [normals[3 * vi], normals[3 * vi + 1], normals[3 * vi + 2]];
            double nn = Blas.Norm(nv);
            if (nn < 1e-6) continue;
            double[] d = [nv[0] / nn, nv[1] / nn, nv[2] / nn];
            long k0 = (long)Math.Floor(p[0] / cell), k1 = (long)Math.Floor(p[1] / cell), k2 = (long)Math.Floor(p[2] / cell);
            bool hit = false;
            for (int dx = -1; dx <= 1 && !hit; dx++)
                for (int dy = -1; dy <= 1 && !hit; dy++)
                    for (int dz = -1; dz <= 1 && !hit; dz++)
                    {
                        if (!grid.TryGetValue((k0 + dx, k1 + dy, k2 + dz), out var cand)) continue;
                        foreach (int c in cand)
                        {
                            double ex = e2[3 * c], ey = e2[3 * c + 1], ez = e2[3 * c + 2];
                            double hx = d[1] * ez - d[2] * ey, hy = d[2] * ex - d[0] * ez, hz = d[0] * ey - d[1] * ex;
                            double fx = e1[3 * c], fy = e1[3 * c + 1], fz = e1[3 * c + 2];
                            double a = (fx * hx + fz * hz) + fy * hy;
                            bool ok = Math.Abs(a) > 1e-9;
                            double f = ok ? 1.0 / a : 0;
                            double sx = p[0] - A[3 * c], sy = p[1] - A[3 * c + 1], sz = p[2] - A[3 * c + 2];
                            double u = f * ((sx * hx + sz * hz) + sy * hy);
                            double qx = sy * fz - sz * fy, qy = sz * fx - sx * fz, qz = sx * fy - sy * fx;
                            double v = f * Math.FusedMultiplyAdd(qz, d[2], Math.FusedMultiplyAdd(qx, d[0], Math.FusedMultiplyAdd(qy, d[1], 0.0)));
                            double t = f * ((ex * qx + ez * qz) + ey * qy);
                            if (ok && u >= 0 && v >= 0 && u + v <= 1 && t <= outCm && t >= -inCm) { hit = true; break; }
                        }
                    }
            res[vi] = hit;
        }
        return res;
    }

    /// <summary>full_port.install's wrapper: the plain test, then points within KeepEdgeCm of an uncovered point kept</summary>
    bool[] CoveredInner(double[] verts, double[] normals, double[] tri, double outCm, double inCm)
    {
        int n = verts.Length / 3;
        if (QuickRead) return new bool[n];
        var hid = Covered(verts, normals, tri, outCm, inCm);
        var keep = (bool[])hid.Clone();
        var open = Enumerable.Range(0, n).Where(i => !hid[i]).ToArray();
        double lim = KeepEdgeCm * KeepEdgeCm;
        for (int i = 0; i < n; i++)
        {
            if (!hid[i]) continue;
            double best = 1e9;
            if (open.Length > 0)
            {
                best = double.MaxValue;
                foreach (int o in open)
                {
                    double d0 = verts[3 * i] - verts[3 * o], d1 = verts[3 * i + 1] - verts[3 * o + 1], d2 = verts[3 * i + 2] - verts[3 * o + 2];
                    best = Math.Min(best, 0.0 + d0 * d0 + d1 * d1 + d2 * d2);
                }
            }
            if (best < lim) keep[i] = false;
        }
        return keep;
    }

    /// <summary>the attire's own hidden-polygon list (2K's .hpl), when it has one: mesh -&gt; vertex indices</summary>
    public Dictionary<string, int[]>? Hidden;
    /// <summary>true when the last Build hid M_Body by the attire's own list (then the rest of the skin is meant to show)</summary>
    public bool UsedHpl;

    /// <summary>the body skin under the gear removed (rock_fbx.hide_body). When the attire has a 2K hidden-polygon list for
    /// M_Body, exactly the triangles 2K hides (all three corners in the list) go: the guess below hid the back of the calves
    /// under open-backed boots and the skin under knee / elbow pads, which 2K shows (Rock 1999, 2K25, 2026-11-12). The guess
    /// stays for models without a list (AEW, mods that ship empty ones).</summary>
    public void HideBody(List<Mesh> meshes)
    {
        UsedHpl = false;
        var body = meshes.FirstOrDefault(m => m.Name == "M_Body");
        if (!QuickRead && body != null && Hidden != null && Hidden.TryGetValue("M_Body", out var list) && list.Length > 0 && list.Max() < body.N)
        {
            var h = new bool[body.N];
            foreach (int i in list) h[i] = true;
            int nt0 = body.Tris.Length / 3;
            var keepL = Enumerable.Range(0, nt0).Where(t => !(h[body.Tris[3 * t]] && h[body.Tris[3 * t + 1]] && h[body.Tris[3 * t + 2]])).ToList();
            Cut(body, keepL, "the attire's hidden-polygon list");
            UsedHpl = true;
            return;
        }
        var gear = meshes.Where(m => !NotGear.Contains(m.Name) && !DropMeshes.Contains(m.Name)).ToList();
        if (gear.Count == 0) return;
        var att = gear.SelectMany(m => m.Tris.SelectMany(v => new[] { m.Verts[3 * v], m.Verts[3 * v + 1], m.Verts[3 * v + 2] })).ToArray();
        foreach (var m in meshes)
        {
            if (m.Name != "M_Body") continue;
            var hid = CoveredInner(m.Verts, m.Normals, att, HideOutCm, HideInCm);
            int nt = m.Tris.Length / 3;
            var cen = new double[3 * nt]; var cn = new double[3 * nt];
            for (int t = 0; t < nt; t++)
                for (int c = 0; c < 3; c++)
                {
                    int a = m.Tris[3 * t], b = m.Tris[3 * t + 1], cc = m.Tris[3 * t + 2];
                    cen[3 * t + c] = (m.Verts[3 * a + c] + m.Verts[3 * b + c] + m.Verts[3 * cc + c]) / 3;
                    cn[3 * t + c] = (m.Normals[3 * a + c] + m.Normals[3 * b + c] + m.Normals[3 * cc + c]) / 3;
                }
            var mid = CoveredInner(cen, cn, att, HideOutCm, HideInCm);
            var keepT = Enumerable.Range(0, nt).Where(t => !(hid[m.Tris[3 * t]] && hid[m.Tris[3 * t + 1]] && hid[m.Tris[3 * t + 2]] && mid[t])).ToList();
            Cut(m, keepT, null);
        }
    }

    /// <summary>only the kept triangles of m and the vertices they use</summary>
    void Cut(Mesh m, List<int> keepT, string? why)
    {
        {
            int n0 = m.N, t0 = m.Tris.Length / 3;
            var used = new bool[n0];
            foreach (int t in keepT) for (int k = 0; k < 3; k++) used[m.Tris[3 * t + k]] = true;
            var remap = new int[n0];
            int cnt = 0;
            for (int i = 0; i < n0; i++) remap[i] = used[i] ? cnt++ : -1;
            m.Tris = keepT.SelectMany(t => new[] { remap[m.Tris[3 * t]], remap[m.Tris[3 * t + 1]], remap[m.Tris[3 * t + 2]] }).ToArray();
            m.Verts = Pick(m.Verts, used, 3); m.Normals = Pick(m.Normals, used, 3); m.Uv = Pick(m.Uv, used, 2);
            m.Weights = m.Weights.Select(w => (w.Bone, Pick(w.W, used, 1))).Where(w => w.Item2.Length > 0 && w.Item2.Max() > 0).ToList();
            Report.Add($"M_Body under the gear removed{(why != null ? $" ({why})" : "")}: {n0 - m.N} of {n0} vertices, {t0 - m.Tris.Length / 3} of {t0} triangles");
        }
    }

    static double[] Pick(double[] a, bool[] used, int k)
    {
        var o = new List<double>();
        for (int i = 0; i < used.Length; i++) if (used[i]) for (int c = 0; c < k; c++) o.Add(a[k * i + c]);
        return o.ToArray();
    }

    // ---------------------------------------------------------------- names, files
    /// <summary>per mesh: its object / material file name (rock_fbx.object_names)</summary>
    public static List<string> ObjectNames(Mcd m, string mdir)
    {
        var names = new Dictionary<string, string>();
        foreach (var o in m.Objects)
        {
            var v = Regex.Replace(o.Name, "(:skinned)?$", "", RegexOptions.IgnoreCase);
            names[v.ToLowerInvariant()] = v;
        }
        var md = Path.Combine(mdir, "Materials");
        if (Directory.Exists(md))
            foreach (var p in Directory.EnumerateFiles(md, "*.jmtl"))
            {
                var stem = Path.GetFileNameWithoutExtension(p);
                names[stem.ToLowerInvariant()] = stem;
            }
        var outl = new List<string>();
        static string NoAlpha(string s) => Regex.Replace(s, "_alpha$", "");
        foreach (var mb in m.Meshes)
        {
            var key = Regex.Replace(mb.Material.ToLowerInvariant(), "^m_", "");
            var cand = names.Keys.Where(s => NoAlpha(s) == key).ToList();
            if (cand.Count == 0) cand = names.Keys.Where(s => s.StartsWith(key) || key.EndsWith(NoAlpha(s))).ToList();
            if (cand.Count == 0) cand = names.Keys.Where(s => key.Contains(NoAlpha(s))).ToList();
            outl.Add(cand.Count > 0 ? names[cand.OrderBy(s => s.Length).First()] : mb.Object.Length > 0 ? mb.Object : key);
        }
        return outl;
    }

    public static string AttireMcd(string chr, string attire)
    {
        var d = Path.Combine(chr, attire);
        if (File.Exists(d)) return d;
        var c = Directory.Exists(d) ? Directory.EnumerateFiles(d).Where(p => p.EndsWith(".mcd", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)).ToList() : [];
        if (c.Count == 0) throw new InvalidDataException($"no .mcd / .mdl in {d}");
        return c.OrderBy(p => Path.GetFileName(p).ToLowerInvariant() is not ("attire.mcd" or "attire.mdl")).ThenBy(p => p, StringComparer.Ordinal).First();
    }

    public static string BaseMcd(string chr)
    {
        var d = Path.Combine(chr, "BaseModel");
        if (Directory.Exists(d))
            foreach (var f in Directory.EnumerateFiles(d).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal))
                if (f!.ToLowerInvariant() is "basemodel.mcd" or "basemodel.mdl") return Path.Combine(d, f);
        return Path.Combine(d, "baseModel.mcd");
    }

    // ---------------------------------------------------------------- build
    public (List<Mesh> Meshes, List<Bone> Bones) Build(string chr, string attire, string outDir)
    {
        Report.Clear();     // (the lines of this read, as the Python prints them on each call)
        Directory.CreateDirectory(Path.Combine(outDir, "textures"));
        var bas = Mcd.Load(BaseMcd(chr));
        var apath = AttireMcd(chr, attire);
        var att = Mcd.Load(apath);
        var adir = Path.GetDirectoryName(apath)!;
        var src = bas.Bones;
        var keep = Enumerable.Range(0, src.Count).Where(i => !Locators.Contains(src[i].Name)).ToList();
        var nw = new Dictionary<int, int>();
        for (int k = 0; k < keep.Count; k++) nw[keep[k]] = k;
        double[] C4 = [1, 0, 0, 0, 0, -1, 0, 0, 0, 0, -1, 0, 0, 0, 0, 1];
        var bones = new List<Bone>();
        foreach (int i in keep)
        {
            var b = src[i];
            int? p = b.Parent >= 0 ? b.Parent : null;
            while (p != null && !nw.ContainsKey(p.Value)) p = src[p.Value].Parent >= 0 ? src[p.Value].Parent : null;
            var wt = new double[16];
            for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) wt[4 * r + c] = b.World[c, r];      // (row-vector bind -> column convention)
            bones.Add(new Bone { Name = Rename(b.Name), Src = b.Name, Parent = p != null ? nw[p.Value] : null, World = Blas.MatMul(C4, wt, 4) });
        }
        var byName = keep.ToDictionary(i => src[i].Name, i => nw[i]);
        var meshes = new List<Mesh>();
        var colors = Mtls.ColorMaps(chr);
        TexAlias.Clear();
        aliasTex = null;
        foreach (var (model, folder) in new[] { (bas, Path.Combine(chr, "BaseModel")), (att, adir) })
        {
            var objs = ObjectNames(model, folder);
            var mats = model.Meshes.Select(mb => mb.Material).ToList();
            var matsLow = mats.Select(x => x.ToLowerInvariant()).ToHashSet();
            for (int k = 0; k < model.Meshes.Count; k++)
            {
                var mb = model.Meshes[k];
                string name = mb.Material;
                if (DropMeshes.Contains(name) || model.Lods.Count == 0) continue;
                if (SkipVariants && !UseVariants.Contains(name) && mats.Any(o => !name.Equals(o, StringComparison.OrdinalIgnoreCase) && name.ToLowerInvariant().StartsWith(o.ToLowerInvariant() + "_")))
                { Report.Add($"{name,-18} left out (an alternate look)"); continue; }
                if (name.Length >= 2 && name[..2].ToLowerInvariant() == "s_" && matsLow.Contains("m_" + name[2..].ToLowerInvariant()))
                { Report.Add($"{name,-18} left out (2K cloth-physics copy of M_{name[2..]})"); continue; }
                var sp = model.StreamOf(k, "POSITION"); var sn = model.StreamOf(k, "NORMAL"); var su = model.Uvs(k);
                if (sp == null || sn == null || su == null)
                { Report.Add($"{name,-18} left out (no {(sp == null ? "positions" : sn == null ? "normals" : "texture coordinates")} in the 2K model)"); continue; }
                var verts = Three(sp); var nrm = Three(sn);
                var uv = su.Select(x => (double)x).ToArray();
                var tris = model.Lods[0][k].Triangles;
                // weights by the base skeleton's bones; a bone only the attire has folds into its nearest base ancestor
                var w = new List<(int Bone, double[] W)>();
                var names = model.Bones.Select(b => b.Name).ToList();
                int n = mb.Vertices, inf = mb.Influences;
                for (int col = 0; col < inf; col++)
                {
                    var uniq = Enumerable.Range(0, n).Where(v => mb.SkinWeights[v * inf + col] > 0).Select(v => (int)mb.SkinBones[v * inf + col]).Distinct().OrderBy(x => x).ToList();
                    foreach (int b in uniq)
                    {
                        int j = b;
                        while (j >= 0 && !byName.ContainsKey(names[j])) j = model.Bones[j].Parent;
                        if (j < 0) continue;
                        int t = byName[names[j]];
                        var add = new double[n];
                        for (int v = 0; v < n; v++) add[v] = mb.SkinBones[v * inf + col] == b ? mb.SkinWeights[v * inf + col] : 0.0;
                        int at = w.FindIndex(x => x.Bone == t);
                        if (at < 0) w.Add((t, add.Select(x => 0 + x).ToArray()));
                        else { var cur = w[at].W; for (int v = 0; v < n; v++) cur[v] += add[v]; }
                    }
                }
                var mat = objs[k];
                var dirs = new List<string> { folder, Path.Combine(chr, "BaseModel"), adir };
                var tex = TextureFor(mat, dirs, outDir, colors.GetValueOrDefault(Regex.Replace(mat, "(?i):skinned$", "").ToLowerInvariant()),
                                     MaterialAlias(chr, dirs, mat).GetValueOrDefault("_color"));
                var me = new Mesh { Name = name, Verts = Rows.TimesMatT(verts, Turn), Normals = Rows.TimesMatT(nrm, Turn), Uv = uv, Tris = tris, Material = mat, Texture = tex, Weights = w };
                meshes.Add(me);
                if (name == "M_Head")
                {
                    var side = LipSideAu27(mb, n, false);
                    if (side != null) { me.LipSrc = side.Select(x => (int)x).ToArray(); me.JawSrc = LipSideAu27(mb, n, true); }
                    else
                    {
                        var eyes = bones.Where(b => b.Src is "J_Eye_L" or "J_Eye_R").Select(b => new[] { b.World[3], b.World[7], b.World[11] }).ToList();
                        if (eyes.Count == 2) me.LipSrc = LipSideGeometric(me.Verts, tris, eyes[0], eyes[1]);
                    }
                }
                Report.Add($"{name,-18} {n,6} verts {tris.Length / 3,6} tris  {mat,-12} {tex ?? "None"}  {w.Count} bones");
            }
        }
        Hidden = Hpl.Read(Path.ChangeExtension(apath, ".hpl"));
        HideBody(meshes);
        return (meshes, bones);
    }

    static double[] Three(Mcd.Stream s)
    {
        var o = new double[s.N * 3];
        for (int i = 0; i < s.N; i++) for (int c = 0; c < 3; c++) o[3 * i + c] = s.V[i * s.C + c];
        return o;
    }

    /// <summary>per head vertex +1 on the jaw / lower lip, -1 off it, from 2K's own jaw-open shape (or how far it moves:
    /// amount) - null without a usable one</summary>
    public static double[]? LipSideAu27(Mcd.MeshBuf mb, int n, bool amount)
    {
        var s = mb.Shapes.FirstOrDefault(x => x.Name.ToLowerInvariant() is "au027_u" or "au026_u" or "au027" or "jaw_dn");
        if (s == null) return null;
        var d = new double[n];
        for (int i = 0; i < s.Index.Length; i++) d[s.Index[i]] = Math.Max(s.Delta[3 * i + 1], 0);
        double mx = d.Max();
        if (mx < Au27MinCm) return null;
        var j = d.Select(x => x / mx).ToArray();
        return amount ? j : j.Select(x => x > 0.3 ? 1.0 : -1.0).ToArray();
    }

    /// <summary>touching lips without a jaw-open shape: where the mesh has two points at one place below the eyes, the
    /// one whose neighbours lie above is the upper lip's - those seed the lips, the rest by surface distance (radius)</summary>
    public static int[]? LipSideGeometric(double[] V, int[] tris, double[] eyeL, double[] eyeR, double radius = 4.0)
    {
        int n = V.Length / 3;
        double[] mid = [(eyeL[0] + eyeR[0]) / 2, (eyeL[1] + eyeR[1]) / 2, (eyeL[2] + eyeR[2]) / 2];
        double iod = Blas.Norm([eyeL[0] - eyeR[0], eyeL[1] - eyeR[1], eyeL[2] - eyeR[2]]);
        var nb = new Dictionary<int, SortedSet<int>>();
        for (int t = 0; t < tris.Length; t += 3)
            for (int a = 0; a < 3; a++)
                for (int b = 0; b < 3; b++)
                    if (tris[t + a] != tris[t + b]) { if (!nb.TryGetValue(tris[t + a], out var s)) nb[tris[t + a]] = s = []; s.Add(tris[t + b]); }
        var groups = new List<(long, long, long)>();
        var gl = new Dictionary<(long, long, long), List<int>>();
        for (int i = 0; i < n; i++)
        {
            var key = ((long)Math.Round(V[3 * i] / 1e-3, MidpointRounding.ToEven), (long)Math.Round(V[3 * i + 1] / 1e-3, MidpointRounding.ToEven), (long)Math.Round(V[3 * i + 2] / 1e-3, MidpointRounding.ToEven));
            if (!gl.TryGetValue(key, out var l)) { gl[key] = l = []; groups.Add(key); }
            l.Add(i);
        }
        var seeds = new List<(int I, int S)>();
        foreach (var key in groups)
        {
            var g = gl[key];
            if (g.Count != 2) continue;
            int p = g[0];
            if (!(0.6 * iod < mid[1] - V[3 * p + 1] && mid[1] - V[3 * p + 1] < 2.2 * iod && Math.Abs(V[3 * p] - mid[0]) < 0.8 * iod && V[3 * p + 2] > mid[2] - 0.3 * iod)) continue;
            var dy = g.Select(i => nb.TryGetValue(i, out var s) && s.Count > 0 ? Np.Sum(s.Select(x => V[3 * x + 1]).ToArray()) / s.Count - V[3 * i + 1] : 0.0).ToArray();
            if (dy[0] * dy[1] < 0)
                for (int k = 0; k < 2; k++)
                {
                    int at = seeds.FindIndex(x => x.I == g[k]);
                    var e = (g[k], dy[k] > 0 ? -1 : 1);
                    if (at < 0) seeds.Add(e); else seeds[at] = e;
                }
        }
        if (seeds.Count(s => s.S < 0) < 3 || seeds.Count(s => s.S > 0) < 3) return null;
        var dist = new Dictionary<int, double>(); var lab = new Dictionary<int, int>();
        var heap = new PriorityQueue<int, (double, int)>();
        foreach (var (i, s) in seeds) { dist[i] = 0.0; lab[i] = s; heap.Enqueue(i, (0.0, i)); }
        while (heap.TryDequeue(out int a, out var pr))
        {
            double d = pr.Item1;
            if (d > dist.GetValueOrDefault(a, 1e18) || d > radius) continue;
            if (!nb.TryGetValue(a, out var ns)) continue;
            foreach (int b in ns)
            {
                double nd = d + Blas.Norm([V[3 * a] - V[3 * b], V[3 * a + 1] - V[3 * b + 1], V[3 * a + 2] - V[3 * b + 2]]);
                if (nd < dist.GetValueOrDefault(b, 1e18) && nd <= radius) { dist[b] = nd; lab[b] = lab[a]; heap.Enqueue(b, (nd, b)); }
            }
        }
        double y0 = Np.Sum(seeds.Select(s => V[3 * s.I + 1]).ToArray()) / seeds.Count;
        return Enumerable.Range(0, n).Select(i => lab.TryGetValue(i, out var l) ? l : V[3 * i + 1] > y0 ? -1 : 1).ToArray();
    }
}
