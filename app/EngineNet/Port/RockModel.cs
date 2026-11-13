using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PortIT.Engine.Build;
using PortIT.Engine.Formats;
using PortIT.Engine.Imaging;
using PortIT.Engine.Util;

namespace PortIT.Engine.Port;

/// <summary>
/// A 2K wrestler (base model + attire) -&gt; model.json for the SvR builder - port of k26/rock_model.py: pose (legs onto
/// the template's, arms / hands / fingers turned), linear blend skinning, the face rig (Face / FaceTransfer), weights to
/// 2011 bones, decimation (Qem), the texture sheets, the template's mouth inside. Its module settings are fields
/// (port_2k / brock_model / full_port set them in Python).
/// </summary>
public sealed class RockModel
{
    // ---------------------------------------------------------------- settings (rock_model module globals)
    public (int W, int H) Sheet = (2048, 1024);
    public const int Gutter = 6;
    public Dictionary<string, (int X, int Y, int W, int H)> Cells = new()
    {
        ["head"] = (0, 0, 1024, 1024), ["body"] = (1024, 0, 1024, 512), ["shoes"] = (1024, 512, 512, 512), ["underwear"] = (1536, 512, 512, 256),
        ["l_kneepad"] = (1536, 768, 64, 64), ["r_kneepad"] = (1600, 768, 64, 64), ["l_elbowpad"] = (1664, 768, 64, 64), ["r_elbowpad"] = (1728, 768, 64, 64),
        ["eye"] = (1792, 768, 256, 128), ["eyelash"] = (1792, 896, 256, 128), ["mouthbag"] = (1536 + 128 + 16, 832 + 16, 96, 96),
    };
    public Dictionary<string, (double Base, double K)> Shine = new()
    {
        ["head"] = (10, 1 / 16.0), ["body"] = (10, 1 / 16.0), ["shoes"] = (80, 1 / 3.0), ["underwear"] = (25, 1 / 2.0), ["l_kneepad"] = (60, 1 / 3.0),
        ["r_kneepad"] = (60, 1 / 3.0), ["l_elbowpad"] = (60, 1 / 3.0), ["r_elbowpad"] = (60, 1 / 3.0), ["eye"] = (52, 0), ["eyelash"] = (0, 0),
    };
    public Dictionary<string, int> Sweat = new() { ["head"] = 77, ["body"] = 77, ["eye"] = 103 };
    public static readonly (int X, int Y, int W, int H) TmplMouthSrc = (128, 0, 256, 128);
    public (int X, int Y) TmplMouthCell = (1536, 832);
    public double FaceGain = 0.7;
    public Dictionary<string, (int R, int G, int B)> Solid = new() { ["teeth"] = (226, 220, 204), ["mouthbag"] = (112, 38, 42) };
    public double CornerRadius = 0.25;
    public int CornerSmooth = 0;
    public bool NormalFlipG = true, TemplateMouth = true, StockWinding = true;
    public Dictionary<string, string> Kind = new() { ["M_EYE"] = "eye", ["M_TransB_eyelash"] = "lash" };
    public Dictionary<string, int> Target = new()
    {
        ["M_Body"] = 3300, ["M_Head"] = 2900, ["M_Shoes"] = 1850, ["M_Underwear"] = 850, ["M_R_kneepad"] = 300, ["M_L_kneepad"] = 300,
        ["M_R_elbowpad"] = 220, ["M_L_elbowpad"] = 220, ["M_Teeth"] = 380, ["M_Tongue"] = 150, ["M_MouthBag"] = 220,
    };
    public double MinWeight = 0.02;
    public HashSet<string> DropMeshes = ["M_Teeth", "M_Tongue"];
    public HashSet<string> Cutout = [];
    public double SeamWeld = 0.03;
    public Func<List<RockFbx.Mesh>, List<RockFbx.Bone>, List<RockFbx.Mesh>>? Prepare;
    public List<string> TexFirst = [];
    public RockFbx Fbx = new();
    /// <summary>a reader in rock_fbx.build's place (an AEW wrestler: AewBridge.Build)</summary>
    public Func<RockFbx, (List<RockFbx.Mesh>, List<RockFbx.Bone>)>? Source;
    public static readonly Dictionary<string, int> Fingers = new() { ["Thumb"] = 0, ["Index"] = 1, ["Middle"] = 2, ["Ring"] = 3, ["Pinky"] = 4 };
    public static readonly string[] JawBones = ["d_kuchi", "d_kuchi_00", "d_kuchi_01", "l_kuchi", "r_kuchi"];
    public readonly List<string> Log = [];
    /// <summary>(checks: per output mesh, its vertices whose source weights hold two equal ones - where NumPy's
    /// unstable argsort may list / keep them otherwise)</summary>
    public Dictionary<string, HashSet<int>>? TieDebug;

    // ---------------------------------------------------------------- small maths (row-major 3x3 / 4x4)
    public static double[] T4(double[]? R = null, double[]? t = null)
    {
        var M = new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        if (R != null) for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) M[4 * i + j] = R[3 * i + j];
        if (t != null) { M[3] = t[0]; M[7] = t[1]; M[11] = t[2]; }
        return M;
    }

    static double[] Mul(params double[][] ms) { var r = ms[0]; for (int i = 1; i < ms.Length; i++) r = Blas.MatMul(r, ms[i], 4); return r; }
    static double[] Neg(double[] v) => v.Select(x => -x).ToArray();
    static double[] Sub(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
    static double[] Cross(double[] a, double[] b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
    static double[] Div(double[] a, double s) => [a[0] / s, a[1] / s, a[2] / s];

    /// <summary>M @ [p 1] for a 4x4 M: NumPy's 4 x 4 matrix-vector order, (a0 v0 + a2 v2) + (a1 v1 + a3 v3)</summary>
    public static double[] Apply4(double[] M, double[] p)
    {
        var o = new double[3];
        for (int i = 0; i < 3; i++) o[i] = (M[4 * i] * p[0] + M[4 * i + 2] * p[2]) + (M[4 * i + 1] * p[1] + M[4 * i + 3] * 1.0);
        return o;
    }

    static double[] Sub3(double[] M4) => [M4[0], M4[1], M4[2], M4[4], M4[5], M4[6], M4[8], M4[9], M4[10]];

    /// <summary>the smallest rotation taking direction a onto b</summary>
    public static double[] Align(double[] a, double[] b)
    {
        a = Div(a, Blas.Norm(a)); b = Div(b, Blas.Norm(b));
        var v = Cross(a, b);
        double c = Blas.Dot(a, b);
        if (c < -0.999999)
        {
            var ax = Cross(a, Math.Abs(a[0]) < 0.9 ? [1.0, 0, 0] : [0, 1.0, 0]);
            ax = Div(ax, Blas.Norm(ax));
            var o = new double[9];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) o[3 * i + j] = 2 * (ax[i] * ax[j]) - (i == j ? 1 : 0);
            return o;
        }
        double[] K = [0, -v[2], v[1], v[2], 0, -v[0], -v[1], v[0], 0];
        var KK = Blas.MatMul(K, K, 3);
        var R = new double[9];
        for (int i = 0; i < 9; i++) R[i] = ((i % 4 == 0 ? 1.0 : 0.0) + K[i]) + KK[i] / (1 + c);
        return R;
    }

    /// <summary>rotation with R a1 along b1 and R a2 in the (b1, b2) plane</summary>
    public static double[] Triad(double[] a1, double[] a2, double[] b1, double[] b2)
    {
        static double[] Frame(double[] x, double[] y)
        {
            x = Div(x, Blas.Norm(x));
            var z = Cross(x, y);
            z = Div(z, Blas.Norm(z));
            var m = Cross(z, x);
            return [x[0], m[0], z[0], x[1], m[1], z[1], x[2], m[2], z[2]];
        }
        var F = Frame(b1, b2); var G = Frame(a1, a2);
        double[] Gt = [G[0], G[3], G[6], G[1], G[4], G[7], G[2], G[5], G[8]];
        return Blas.MatMul(F, Gt, 3);
    }

    // ---------------------------------------------------------------- 2K bone -> 2011 bone(s)
    public static List<(string, double)>? To2011(string name)
    {
        if (name is "J_Root" or "J_Hips") return [("root", 1.0)];
        if (name is "J_Spine1" or "J_Spine2" || name.StartsWith("H_Gut_Jig")) return [("koshi", 1.0)];
        if (name == "J_Chest" || name.StartsWith("H_Pec") || name.StartsWith("H_Lat") || name.StartsWith("J_Collar") || name.StartsWith("H_Collar")) return [("mune", 1.0)];
        if (name == "J_Neck" || Regex.IsMatch(name, @"^H_Neck_[LR]_1$")) return [("kubi", 1.0)];
        if (name == "H_Neck_tw" || Regex.IsMatch(name, @"^H_Neck_[LR]_2$")) return [("kubi_x", 1.0)];
        if (Regex.IsMatch(name, @"^H_Trap_[LR]$")) return [("mune", 0.5), ("kubi", 0.5)];
        if (name is "J_Head" or "J_upperTeeth") return [("atama", 1.0)];
        if (name is "J_Jaw" or "J_lowerTeeth" || name.StartsWith("J_Tongue")) return [("d_kuchi", 1.0)];
        var m = Regex.Match(name, "_([LR])(?:_|$)");
        if (!m.Success) return null;
        string s = m.Groups[1].Value.ToLowerInvariant() + "_";
        if (name.StartsWith("J_Eye_")) return [(s + "eye", 1.0)];
        if (name.StartsWith("J_Clavicle") || name.StartsWith("H_Shld_Pad")) return [(s + "sakotsu", 1.0)];
        if (Regex.IsMatch(name, @"^H_Delt_[LR]_1$")) return [(s + "sakotsu", 0.5), (s + "ninoude", 0.5)];
        if (name.StartsWith("J_Shoulder") || name.StartsWith("H_Delt") || name.StartsWith("H_Bicep") || name.StartsWith("H_Ebw_Out")) return [(s + "ninoude", 1.0)];
        if (Regex.IsMatch(name, @"^H_Elbow_[LR]_tw01")) return [(s + "kote_x", 1.0)];
        if (Regex.IsMatch(name, @"^H_Elbow_[LR]_tw02")) return [(s + "kote_x2", 1.0)];
        if (name.StartsWith("J_Elbow")) return [(s + "kote", 1.0)];
        if (name.StartsWith("J_Wrist")) return [(s + "te", 1.0)];
        var fm = Regex.Match(name, @"^J_(Thumb|Index|Middle|Ring|Pinky)F(\d)_[LR]");
        if (fm.Success)
        {
            int k = int.Parse(fm.Groups[2].Value);
            if (k == 0) return [(s + "te", 1.0)];
            return [(s + $"yubi{Fingers[fm.Groups[1].Value]}{Math.Min(k, 3) - 1}", 1.0)];
        }
        if (name.StartsWith("H_Leg_Vol")) return [("root", 0.5), (s + "momo", 0.5)];
        if (name.StartsWith("J_Leg") || name.StartsWith("H_Hip") || name.StartsWith("H_Kn_") || name.StartsWith("H_Quad") || name.StartsWith("H_Knee_Upper")) return [(s + "momo", 1.0)];
        if (name.StartsWith("J_Knee") || name.StartsWith("H_Knee_Lower")) return [(s + "sune", 1.0)];
        if (name.StartsWith("J_Foot") || name.StartsWith("H_Foot")) return [(s + "ashi", 1.0)];
        if (name.StartsWith("J_Toe") || name.StartsWith("H_Toe")) return [(s + "tsumasaki", 1.0)];
        return null;
    }

    // ---------------------------------------------------------------- pose / skin
    public static List<double[]> Pose(List<RockFbx.Bone> bones, Dictionary<string, double[]> tmplWorld, double[] origin)
    {
        var P = bones.Select(b => new[] { b.World[3], b.World[7], b.World[11] }).ToList();
        var idx = new Dictionary<string, int>();
        for (int i = 0; i < bones.Count; i++) idx[bones[i].Src] = i;
        var tw = tmplWorld.ToDictionary(kv => kv.Key, kv => new[] { origin[0] + 10.0 * kv.Value[0], origin[1] + 10.0 * kv.Value[1], origin[2] + 10.0 * kv.Value[2] });
        var M = new double[bones.Count][];
        for (int i = 0; i < bones.Count; i++)
        {
            var b = bones[i];
            int? p = b.Parent;
            if (p != null && p >= i) throw new InvalidDataException("parents first");
            var Mp = p != null ? M[p.Value] : T4();
            M[i] = Mp;
            string name = b.Src;
            var mm = Regex.Match(name, "_([LR])(?:_|$)");
            string s = mm.Success ? mm.Groups[1].Value : "";
            string x = s.ToLowerInvariant() + "_";
            double[] TurnTo(int child, double[] target)
            {
                var pn = Apply4(Mp, P[i]);
                var dcur = Blas.MatVec3(Mp, 4, Sub(P[child], P[i]));
                var R = Align(dcur, target);
                return Mul(T4(t: pn), T4(R), T4(t: Neg(pn)), Mp);
            }
            if (s.Length > 0 && (name == "J_Leg_" + s || name == "J_Knee_" + s))
            {
                var (a0, a1, c0, c1) = name.StartsWith("J_Leg") ? ("J_Leg_", "J_Knee_", "momo", "sune") : ("J_Knee_", "J_Foot_", "sune", "ashi");
                var H = P[i]; var K = P[idx[a1 + s]];
                var tH = tw[x + c0]; var tK = tw[x + c1];
                var d = Sub(K, H); var dt = Sub(tK, tH);
                var dn = Div(d, Blas.Norm(d));
                double sc = Blas.Norm(dt) / Blas.Norm(d);
                var S = new double[9];
                for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) S[3 * r + c] = (r == c ? 1.0 : 0.0) + (sc - 1) * (dn[r] * dn[c]);
                M[i] = Mul(T4(t: tH), T4(Align(d, dt)), T4(S), T4(t: Neg(H)));
            }
            else if (s.Length > 0 && (name == "J_Foot_" + s || name == "H_Foot_" + s))
            {
                var F = P[idx["J_Foot_" + s]]; var Tq = P[idx["J_Toe_" + s]];
                var tF = tw[x + "ashi"]; var tT = tw[x + "tsumasaki"];
                double[] h1 = [(Tq[0] - F[0]) * 1, (Tq[1] - F[1]) * 0, (Tq[2] - F[2]) * 1], h2 = [(tT[0] - tF[0]) * 1, (tT[1] - tF[1]) * 0, (tT[2] - tF[2]) * 1];
                M[i] = Mul(T4(t: tF), T4(Align(h1, h2)), T4(t: Neg(F)));
            }
            else if (s.Length > 0 && name == "J_Shoulder_" + s) M[i] = TurnTo(idx["J_Elbow_" + s], Sub(tw[x + "kote"], tw[x + "ninoude"]));
            else if (s.Length > 0 && name == "J_Elbow_" + s) M[i] = TurnTo(idx["J_Wrist_" + s], Sub(tw[x + "te"], tw[x + "kote"]));
            else if (s.Length > 0 && name == "J_Wrist_" + s)
            {
                var pn = Apply4(Mp, P[i]);
                var a1v = Blas.MatVec3(Mp, 4, Sub(P[idx["J_MiddleF1_" + s]], P[i]));
                var a2v = Blas.MatVec3(Mp, 4, Sub(P[idx["J_IndexF1_" + s]], P[idx["J_PinkyF1_" + s]]));
                var b1 = Sub(tw[x + "yubi20"], tw[x + "te"]);
                var b2 = Sub(tw[x + "yubi10"], tw[x + "yubi40"]);
                M[i] = Mul(T4(t: pn), T4(Triad(a1v, a2v, b1, b2)), T4(t: Neg(pn)), Mp);
            }
            else
            {
                var fm = Regex.Match(name, @"^J_(Thumb|Index|Middle|Ring|Pinky)F([123])_[LR]$");
                if (fm.Success)
                {
                    int f = Fingers[fm.Groups[1].Value], k = int.Parse(fm.Groups[2].Value);
                    string nxt = k < 3 ? $"J_{fm.Groups[1].Value}F{k + 1}_{s}" : $"J_{fm.Groups[1].Value}F3_{s}_stub";
                    int j = k - 1;
                    var a = tw[x + $"yubi{f}{Math.Min(j, 1)}"];
                    var c = tw[x + $"yubi{f}{Math.Min(j, 1) + 1}"];
                    M[i] = TurnTo(idx[nxt], Sub(c, a));
                }
            }
        }
        return M.ToList();
    }

    /// <summary>linear blend skinning: per bone (the weights' order) w (V @ L.T + t) and the normals through inv(L)</summary>
    public static (double[] V, double[] N) Skin(double[] verts, double[] normals, List<(int Bone, double[] W)> weights, List<double[]> M)
    {
        int n = verts.Length / 3;
        var o = new double[3 * n]; var no = new double[3 * n]; var tot = new double[n];
        foreach (var (b, w) in weights)
        {
            var A = M[b];
            var L = Sub3(A);
            var pv = Rows.TimesMatT(verts, L);
            var inv = Inv3(L);
            var pn = Rows.TimesMat(normals, inv);
            for (int i = 0; i < n; i++)
            {
                for (int c = 0; c < 3; c++)
                {
                    o[3 * i + c] += w[i] * (pv[3 * i + c] + A[4 * c + 3]);
                    no[3 * i + c] += w[i] * pn[3 * i + c];
                }
                tot[i] += w[i];
            }
        }
        for (int i = 0; i < n; i++)
        {
            double t = tot[i] == 0 ? 1 : tot[i];
            for (int c = 0; c < 3; c++) o[3 * i + c] /= t;
            double nn = Math.Sqrt(0.0 + no[3 * i] * no[3 * i] + no[3 * i + 1] * no[3 * i + 1] + no[3 * i + 2] * no[3 * i + 2]);
            if (nn == 0) nn = 1;
            for (int c = 0; c < 3; c++) no[3 * i + c] /= nn;
        }
        return (o, no);
    }

    /// <summary>a 3x3 inverse (np.linalg.inv stand-in: LU with partial pivoting - agrees with LAPACK to the last bits)</summary>
    public static double[] Inv3(double[] m)
    {
        var a = new double[3, 6];
        for (int r = 0; r < 3; r++) { for (int c = 0; c < 3; c++) a[r, c] = m[3 * r + c]; a[r, 3 + r] = 1; }
        for (int c = 0; c < 3; c++)
        {
            int piv = c;
            for (int r = c + 1; r < 3; r++) if (Math.Abs(a[r, c]) > Math.Abs(a[piv, c])) piv = r;
            for (int k = 0; k < 6; k++) (a[c, k], a[piv, k]) = (a[piv, k], a[c, k]);
            for (int r = c + 1; r < 3; r++) { double f = a[r, c] / a[c, c]; for (int k = c; k < 6; k++) a[r, k] -= f * a[c, k]; }
        }
        var x = new double[9];
        for (int col = 0; col < 3; col++)
            for (int r = 2; r >= 0; r--)
            {
                double s = a[r, 3 + col];
                for (int q = r + 1; q < 3; q++) s -= a[r, q] * x[3 * q + col];
                x[3 * r + col] = s / a[r, r];
            }
        return x;
    }

    // ---------------------------------------------------------------- weights helpers
    public static List<(int B, double W)> Tidy(IEnumerable<(int B, double W)> e, double minWeight)
    {
        var acc = new List<(int B, double W)>();
        foreach (var (b, w) in e)
        {
            int at = acc.FindIndex(x => x.B == b);
            if (at < 0) acc.Add((b, 0.0 + w)); else acc[at] = (b, acc[at].W + w);
        }
        var top = acc.OrderBy(x => -x.W).Take(4).ToList();
        double t = 0; foreach (var (_, w) in top) t += w;
        if (t == 0) t = 1.0;
        var e2 = top.Where(x => x.W / t >= minWeight).Select(x => (x.B, x.W / t)).ToList();
        if (e2.Count == 0) e2 = [(top[0].B, 1.0)];
        double t2 = 0; foreach (var (_, w) in e2) t2 += w;
        return e2.Select(x => (x.B, x.Item2 / t2)).ToList();
    }

    public List<List<(int, double)>> SmoothCorners(double[] U, int[] tris, List<List<(int B, double W)>> wl, Dictionary<string, double[]> L, Func<IEnumerable<(int, double)>, List<(int, double)>> tidy, double radius, int rounds)
    {
        int n = U.Length / 3;
        var rep = Face.WeldRep(U);
        var nb = new Dictionary<int, HashSet<int>>();
        for (int t = 0; t < tris.Length; t += 3)
            for (int a = 0; a < 3; a++) for (int b = 0; b < 3; b++)
                if (tris[t + a] != tris[t + b]) { int ra = rep[tris[t + a]]; if (!nb.TryGetValue(ra, out var s)) nb[ra] = s = []; s.Add(rep[tris[t + b]]); }
        var k = new double[n];
        for (int i = 0; i < n; i++)
        {
            double d1 = Math.Sqrt(0.0 + Sq(U[3 * i] - L["mouth_l"][0]) + Sq(U[3 * i + 1] - L["mouth_l"][1]) + Sq(U[3 * i + 2] - L["mouth_l"][2]));
            double d2 = Math.Sqrt(0.0 + Sq(U[3 * i] - L["mouth_r"][0]) + Sq(U[3 * i + 1] - L["mouth_r"][1]) + Sq(U[3 * i + 2] - L["mouth_r"][2]));
            k[i] = Math.Clamp(1 - Math.Min(d1, d2) / radius, 0, 1);
        }
        var region = Enumerable.Range(0, n).Where(i => k[i] > 0).ToList();
        var cur = Enumerable.Range(0, n).Select(i => wl[i].GroupBy(e => e.B).ToDictionary(g => g.Key, g => g.Last().W)).ToList();
        for (int r = 0; r < rounds; r++)
        {
            var nxt = new Dictionary<int, Dictionary<int, double>>();
            foreach (int v in region)
            {
                var ns = nb.GetValueOrDefault(rep[v], []).Where(x => x < n).ToList();
                if (ns.Count == 0) continue;
                var avg = new Dictionary<int, double>();
                foreach (int x in ns) foreach (var (b, w) in cur[x]) avg[b] = avg.GetValueOrDefault(b, 0.0) + w / ns.Count;
                var mix = new Dictionary<int, double>();
                foreach (int b in avg.Keys.Union(cur[v].Keys)) mix[b] = (1 - k[v]) * cur[v].GetValueOrDefault(b, 0.0) + k[v] * avg.GetValueOrDefault(b, 0.0);
                nxt[v] = mix;
            }
            foreach (var (v, e) in nxt) cur[v] = e;
            for (int i = 0; i < n; i++) if (rep[i] != i && nxt.ContainsKey(rep[i])) cur[i] = new Dictionary<int, double>(cur[rep[i]]);
        }
        var regSet = region.ToHashSet();
        var touched = regSet.Union(Enumerable.Range(0, n).Where(i => regSet.Contains(rep[i]))).ToHashSet();
        return Enumerable.Range(0, n).Select(i => touched.Contains(i) ? tidy(cur[i].Select(kv => (kv.Key, kv.Value))) : wl[i].Select(e => (e.B, e.W)).ToList()).ToList();
    }

    static double Sq(double x) => x * x;

    public List<List<(int, double)>> JawFromSource(List<List<(int B, double W)>> wl, double[] jaw, Func<string, int> b11, List<string> names11, Func<IEnumerable<(int, double)>, List<(int, double)>> tidy)
    {
        var o = new List<List<(int, double)>>();
        int dk = b11("d_kuchi");
        for (int i = 0; i < wl.Count; i++)
        {
            var e = wl[i];
            double t = FaceGain * jaw[i];
            var jw = e.Where(x => JawBones.Contains(names11[x.B])).ToList();
            var rest = e.Where(x => !JawBones.Contains(names11[x.B])).ToList();
            double sj = 0, sr = 0;
            foreach (var (_, w) in jw) sj += w;
            foreach (var (_, w) in rest) sr += w;
            if (t < 0.01 && sj < 0.01) { o.Add(e.Select(x => (x.B, x.W)).ToList()); continue; }
            var nw = sj > 1e-6 ? jw.Select(x => (x.B, x.W / sj * t)).ToList() : t > 0 ? [(dk, t)] : new List<(int, double)>();
            nw.AddRange(sr > 1e-6 ? rest.Select(x => (x.B, x.W / sr * (1 - t))) : [(b11("atama"), 1 - t)]);
            o.Add(tidy(nw));
        }
        return o;
    }

    public static List<int[]> SplitRigid(int[] tris, List<List<(int B, double W)>> wl, int minVerts = 300)
    {
        int nt = tris.Length / 3;
        var key = new int?[nt];
        for (int t = 0; t < nt; t++)
        {
            var bs = Enumerable.Range(0, 3).Select(k => wl[tris[3 * t + k]].Count == 1 ? (int?)wl[tris[3 * t + k]][0].B : null).ToArray();
            key[t] = bs[0] != null && bs[0] == bs[1] && bs[1] == bs[2] ? bs[0] : null;
        }
        var groups = new List<(int? K, List<int> T)>();
        for (int t = 0; t < nt; t++)
        {
            int at = groups.FindIndex(g => g.K == key[t]);
            if (at < 0) groups.Add((key[t], [t])); else groups[at].T.Add(t);
        }
        var rest = groups.FirstOrDefault(g => g.K == null).T ?? [];
        var outl = new List<int[]>();
        foreach (var (k, g) in groups.Where(g => g.K != null))
        {
            if (g.SelectMany(t => new[] { tris[3 * t], tris[3 * t + 1], tris[3 * t + 2] }).Distinct().Count() >= minVerts) outl.Add(g.ToArray());
            else rest.AddRange(g);
        }
        if (rest.Count > 0) outl.Add(rest.OrderBy(x => x).ToArray());
        return outl;
    }

    // ---------------------------------------------------------------- textures
    /// <summary>(texture name + suffix, e.g. head_color) -&gt; a file used in its place: the head map with hair painted in</summary>
    public Dictionary<string, string> TexOverride = new(StringComparer.OrdinalIgnoreCase);

    readonly Dictionary<string, Dictionary<string, string>> colorMaps = [];
    public string? FindTex(string chr, string attire, string bas, string suffix)
    {
        if (TexOverride.TryGetValue(bas + suffix, out var over) && File.Exists(over)) return over;
        string? Search(string stem)
        {
            foreach (var folder in TexFirst.Concat([Path.Combine(chr, attire, "Textures"), Path.Combine(chr, "BaseModel", "Textures")]))
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var f in Directory.EnumerateFiles(folder))
                    if (Path.GetFileName(f).ToLowerInvariant().Split('.')[0] == stem.ToLowerInvariant()) return f;
            }
            return null;
        }
        if (Search(bas + suffix) is { } hit) return hit;
        // (the .mtls names another map for this material - 2K23 mods: body -> "bodyx_color", head -> "headx_color";
        //  without this the skin took a flat grey: Cody Rhodes, Ricochet, Eric Bischoff, Joaquin Wilde, 2026-11-13)
        if (!colorMaps.TryGetValue(chr, out var cm)) colorMaps[chr] = cm = PortIT.Engine.Readers.Mtls.ColorMaps(chr);
        if (cm.TryGetValue(bas.ToLowerInvariant(), out var cmap) && cmap.EndsWith("_color", StringComparison.OrdinalIgnoreCase)
            && Search(cmap[..^"_color".Length] + suffix) is { } viaMtls) return viaMtls;
        if (bas.ToLowerInvariant() is "head" or "body" && Search(bas + "x" + suffix) is { } xHit) return xHit;
        var a = Fbx.TexAlias.GetValueOrDefault(bas.ToLowerInvariant())?.GetValueOrDefault(suffix);
        if (a != null && File.Exists(a)) return a;
        // (hair_ALPHA -> hair_color.DDS, as RockFbx.TextureFor)
        return Regex.IsMatch(bas, "(?i)_alpha$") ? FindTex(chr, attire, Regex.Replace(bas, "(?i)_alpha$", ""), suffix) : null;
    }

    /// <summary>a texture fitted into a cell (gutter of edge colour): RGB and A resized apart; normal maps: x, y kept,
    /// green flipped (2K26), z rebuilt -&gt; h x w x 4 bytes</summary>
    public Img CellImage(string path, (int X, int Y, int W, int H) cell, bool normal = false)
    {
        var im = ImageIO.Load(path).ToRgba();
        int iw = cell.W - 2 * Gutter, ih = cell.H - 2 * Gutter;
        var rgb = Resample.Resize(im.ToRgb(), iw, ih, Filter.Lanczos);
        var al = Resample.Resize(im.Channel(3), iw, ih, Filter.Lanczos);
        var a = new double[iw * ih * 4];
        for (int i = 0; i < iw * ih; i++) { for (int c = 0; c < 3; c++) a[4 * i + c] = rgb.Px[3 * i + c]; a[4 * i + 3] = al.Px[i]; }
        if (normal)
            for (int i = 0; i < iw * ih; i++)
            {
                if (NormalFlipG) a[4 * i + 1] = 255.0 - a[4 * i + 1];
                double nx = a[4 * i] / 127.5 - 1, ny = a[4 * i + 1] / 127.5 - 1;
                double nz = Math.Sqrt(Math.Clamp(1 - nx * nx - ny * ny, 0, 1));
                a[4 * i + 2] = (nz + 1) * 127.5;
                a[4 * i + 3] = 255;
            }
        var o = new Img(cell.W, cell.H, 4);
        for (int y = 0; y < cell.H; y++)
            for (int x = 0; x < cell.W; x++)
            {
                int sx = Math.Clamp(x - Gutter, 0, iw - 1), sy = Math.Clamp(y - Gutter, 0, ih - 1);
                for (int c = 0; c < 4; c++) o.Px[4 * (y * cell.W + x) + c] = (byte)Math.Clamp(a[4 * (sy * iw + sx) + c], 0, 255);
            }
        return o;
    }

    public double[] CellUv((int X, int Y, int W, int H) cell, double[] uv)
    {
        int n = uv.Length / 2;
        var o = new double[2 * n];
        for (int i = 0; i < n; i++)
        {
            o[2 * i] = (cell.X + Gutter + Math.Clamp(uv[2 * i], 0, 1) * (cell.W - 2 * Gutter)) / Sheet.W;
            o[2 * i + 1] = (cell.Y + Gutter + (1 - Math.Clamp(uv[2 * i + 1], 0, 1)) * (cell.H - 2 * Gutter)) / Sheet.H;
        }
        return o;
    }

    // ---------------------------------------------------------------- build
    static readonly double[] Turn = [1, 0, 0, 0, -1, 0, 0, 0, -1];

    static double[] Unit(double[] p, double[] origin)
    {
        var o = new double[p.Length];
        for (int i = 0; i < p.Length; i++) o[i] = (p[i] - origin[i % 3]) * 0.1;
        return o;
    }

    static double[] Take(double[] a, int k, IReadOnlyList<int> idx)
    {
        var o = new double[idx.Count * k];
        for (int i = 0; i < idx.Count; i++) for (int c = 0; c < k; c++) o[k * i + c] = a[k * idx[i] + c];
        return o;
    }

    static string NpArr(double[] v)
    {
        // (numpy's array print of rounded floats: shortest digits, '1.' for whole ones, padded to one width)
        var parts = v.Select(x =>
        {
            string s = x.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            if (s.Contains('E')) s = ((decimal)x).ToString(System.Globalization.CultureInfo.InvariantCulture);
            int dot = s.IndexOf('.');
            return dot < 0 ? (s, "") : (s[..dot], s[(dot + 1)..]);
        }).ToList();
        int wi = parts.Max(p => p.Item1.Length), wf = parts.Max(p => p.Item2.Length);
        return "[" + string.Join(" ", parts.Select(p => p.Item1.PadLeft(wi) + "." + p.Item2.PadRight(wf))) + "]";
    }

    static string F(double x, int d) => d == 0
        ? Math.Round(x, MidpointRounding.ToEven).ToString("F0", System.Globalization.CultureInfo.InvariantCulture)
        : x.ToString("F" + d, System.Globalization.CultureInfo.InvariantCulture);

    static JsonArray Arr(double[] a, int k, int nd)
    {
        var o = new JsonArray();
        for (int i = 0; i < a.Length / k; i++)
        {
            var r = new JsonArray();
            for (int c = 0; c < k; c++) r.Add(Np.Round(a[k * i + c], nd));
            o.Add(r);
        }
        return o;
    }

    /// <summary>-&gt; (model.json spec, report); writes out/color.png, out/normal.png, out/model.json</summary>
    public (JsonObject Spec, List<string> Report) Build(string chr, string attire, string outDir, string template, double targetScale = 1.0)
    {
        Directory.CreateDirectory(outDir);
        Fbx.DropMeshes = [.. DropMeshes];
        var (meshes, bones) = Source != null ? Source(Fbx) : Fbx.Build(chr, attire, Path.Combine(outDir, "_fbx_tmp"));
        if (Prepare != null) meshes = Prepare(meshes, bones);
        var C4 = T4(Turn);
        foreach (var b in bones) b.World = Blas.MatMul(C4, b.World, 4);
        foreach (var m in meshes) { m.Verts = Rows.TimesMatT(m.Verts, Turn); m.Normals = Rows.TimesMatT(m.Normals, Turn); }
        var idx = new Dictionary<string, int>();
        for (int i = 0; i < bones.Count; i++) idx[bones[i].Src] = i;
        var P = bones.Select(b => new[] { b.World[3], b.World[7], b.World[11] }).ToList();
        var PL = P[idx["J_Leg_L"]]; var PR = P[idx["J_Leg_R"]];
        double[] origin = [(PL[0] + PR[0]) / 2 * 0, (PL[1] + PR[1]) / 2 * 1, (PL[2] + PR[2]) / 2 * 1];
        var conv = new SvrBuilder.Template(template);
        var Wt = Face.World11(conv);
        var tmplWorld = new Dictionary<string, double[]>();
        for (int i = 0; i < conv.TNodes.Count; i++) tmplWorld[conv.TNodes[i].Name] = [Wt[i][3], Wt[i][7], Wt[i][11]];
        var M = Pose(bones, tmplWorld, origin);
        var Pp = Enumerable.Range(0, bones.Count).Select(i => Apply4(M[i], P[i])).ToList();
        double[] U(double[] p) => Unit(p, origin);

        // joints: his upper body; legs stay the template's
        var J = new Dictionary<string, double[]>();
        var pairs = new List<(string, string)> { ("koshi", "J_Spine1"), ("mune", "J_Chest"), ("kubi", "J_Neck"), ("kubi_x", "H_Neck_tw"), ("atama", "J_Head") };
        foreach (var s in new[] { "L", "R" })
        {
            string x = s.ToLowerInvariant() + "_";
            pairs.AddRange([(x + "eye", "J_Eye_" + s), (x + "sakotsu", "J_Clavicle_" + s), (x + "ninoude", "J_Shoulder_" + s), (x + "kote", "J_Elbow_" + s),
                            (x + "kote_x", $"H_Elbow_{s}_tw01"), (x + "kote_x2", $"H_Elbow_{s}_tw02"), (x + "te", "J_Wrist_" + s)]);
            foreach (var (fname, f) in Fingers)
                for (int k = 1; k <= 3; k++) pairs.Add((x + $"yubi{f}{k - 1}", $"J_{fname}F{k}_{s}"));
        }
        foreach (var (n11, n26) in pairs) J[n11] = U(Pp[idx[n26]]);
        foreach (var s in new[] { "l", "r" })
        {
            var a = J[s + "_ninoude"]; var b = J[s + "_kote"];
            J[s + "_ninoude_x"] = [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2, (a[2] + b[2]) / 2];
        }

        // eye pivots: the eyeballs' own centres where the 2K eye bones sit elsewhere
        var eyes = meshes.Where(m => m.Name == "M_EYE").ToList();
        if (eyes.Count > 0)
        {
            var EP = eyes.SelectMany(m => U(Skin(m.Verts, m.Normals, m.Weights, M).V)).ToArray();
            int ne = EP.Length / 3;
            foreach (var (s, left) in new[] { ("l", true), ("r", false) })
            {
                var side = Enumerable.Range(0, ne).Where(i => left ? EP[3 * i] > 0 : EP[3 * i] < 0).ToList();
                if (side.Count < 20) continue;
                var c = new double[3];
                for (int k = 0; k < 3; k++) c[k] = (side.Min(i => EP[3 * i + k]) + side.Max(i => EP[3 * i + k])) / 2;
                double dist = Blas.Norm(Sub(J[s + "_eye"], c));
                if (dist > 0.1)
                {
                    Log.Add($"{s}_eye: the 2K eye bone is {F(dist * 10, 1)} cm from the eyeball - its centre used");
                    J[s + "_eye"] = c;
                }
            }
        }
        // face rig: the template's face skinning and face bones fitted onto his face
        var hm = meshes.First(m => m.Name == "M_Head");
        var Vh = Skin(hm.Verts, hm.Normals, hm.Weights, M).V;
        var fit = Face.FitFace(conv, U(Vh), hm.Tris, J["l_eye"], J["r_eye"], hm.LipSrc);
        Log.AddRange(fit.Log);
        foreach (var (k, v) in fit.Joints) J[k] = v;
        double mouthY = fit.LDst["mouth"][1];

        // weights -> 2011 names
        var unmapped = new Dictionary<string, double>();
        var names11 = new List<string>();
        var pos11 = new Dictionary<string, int>();
        int B11(string n)
        {
            if (!pos11.TryGetValue(n, out int p)) { p = pos11[n] = names11.Count; names11.Add(n); }
            return p;
        }
        List<(int, double)> TidyW(IEnumerable<(int, double)> e) => Tidy(e, MinWeight);

        int SW = Sheet.W, SH = Sheet.H;
        var sheetC = new byte[SH * SW * 4];
        var sheetN = new byte[SH * SW * 4];
        for (int i = 0; i < SH * SW; i++) { sheetC[4 * i + 3] = 255; sheetN[4 * i] = 128; sheetN[4 * i + 1] = 128; sheetN[4 * i + 2] = 255; }
        (int, int, int, int)? lashCell = null;
        (double[] P, List<List<(int B, double W)>> W, bool[] Tie)? seamBody = null;
        var outMeshes = new JsonArray();
        var report = new List<string>();
        foreach (var m in meshes)
        {
            var (V, N) = Skin(m.Verts, m.Normals, m.Weights, M);
            int n = V.Length / 3;
            var dense = new Dictionary<int, double[]>();
            foreach (var (b, w) in m.Weights)
            {
                string src = bones[b].Src;
                List<(string, double)>? tgt = null;
                int? j = b;
                while (tgt == null && j != null)
                {
                    tgt = To2011(bones[j.Value].Src);
                    if (tgt == null) j = bones[j.Value].Parent;
                }
                if (j != b) unmapped[src] = unmapped.GetValueOrDefault(src, 0) + Np.Sum(w);
                foreach (var (nm, frac) in tgt ?? [("root", 1.0)])
                {
                    int k = B11(nm);
                    var add = new double[n];
                    if (dense.TryGetValue(k, out var cur)) for (int i = 0; i < n; i++) add[i] = cur[i] + frac * w[i];
                    else for (int i = 0; i < n; i++) add[i] = 0 + frac * w[i];
                    dense[k] = add;
                }
            }
            var keys = dense.Keys.OrderBy(x => x).ToList();
            int nb = keys.Count;
            var Wd = new double[n * nb];
            for (int c = 0; c < nb; c++) { var col = dense[keys[c]]; for (int v = 0; v < n; v++) Wd[v * nb + c] = col[v]; }
            // top 4, drop small, renormalise (NumPy's argsort here is its SIMD sort - its order among equal weights is
            //  not stable; C# keeps the first: the same weights per vertex, maybe listed in another order)
            var wl = new List<List<(int B, double W)>>(n);
            var tie = new bool[n];
            var ord = new int[nb];
            for (int v = 0; v < n; v++)
            {
                for (int c = 0; c < nb; c++) ord[c] = c;
                int vv = v;
                var top = ord.OrderBy(c => -Wd[vv * nb + c]).Take(4).Where(c => Wd[vv * nb + c] > 0).Select(c => (keys[c], Wd[vv * nb + c])).ToList();
                double t = 0; foreach (var (_, w) in top) t += w;
                if (t == 0) t = 1.0;
                var e = top.Select(x => (x.Item1, x.Item2 / t)).ToList();
                var e2 = e.Where(x => x.Item2 >= MinWeight).ToList();
                if (e2.Count == 0) e2 = e.Take(1).ToList();
                double t2 = 0; foreach (var (_, w) in e2) t2 += w;
                wl.Add(e2.Select(x => (x.Item1, x.Item2 / t2)).ToList());
                if (TieDebug != null)
                {
                    var pos = Enumerable.Range(0, nb).Select(c => Wd[vv * nb + c]).Where(w => w > 0).ToList();
                    tie[v] = pos.Count != pos.Distinct().Count();
                }
            }
            var tris = m.Tris;
            if (m.Name == "M_Head")
            {
                var own = wl.Select(e => e.Select(x => (names11[x.B], x.W)).ToList()).ToList();
                var nw = FaceTransfer.Transfer(fit, U(V), own, tris: tris, gain: FaceGain, srcSide: m.LipSrc);
                wl = nw.Select(e => TidyW(e.Select(x => (B11(x.B), x.W)).ToList())).Select(l => l.Select(x => (B: x.Item1, W: x.Item2)).ToList()).ToList();
                if (CornerSmooth > 0)
                    wl = SmoothCorners(U(V), tris, wl, fit.LDst, TidyW, CornerRadius, CornerSmooth).Select(l => l.Select(x => (B: x.Item1, W: x.Item2)).ToList()).ToList();
                if (fit.LipsTouch && m.LipSrc != null)
                {
                    m.Tris = tris = FaceTransfer.CutLips(U(V), tris, m.LipSrc, fit.LDst, Log);
                    if (m.JawSrc != null)
                        wl = JawFromSource(wl, m.JawSrc, B11, names11, TidyW).Select(l => l.Select(x => (B: x.Item1, W: x.Item2)).ToList()).ToList();
                }
            }
            else if (m.Name == "M_MouthBag")
            {
                var Uv = U(V);
                wl = Enumerable.Range(0, n).Select(i =>
                {
                    double a = FaceGain * Math.Clamp((Uv[3 * i + 1] - mouthY + 0.03) / 0.15, 0, 1);
                    return TidyW([(B11("d_kuchi"), a), (B11("atama"), 1 - a)]).Select(x => (B: x.Item1, W: x.Item2)).ToList();
                }).ToList();
            }
            else if (m.Name == "M_AEWMouth")
            {
                int dk = B11("d_kuchi");
                wl = wl.Select(e =>
                {
                    var l = e.Select(x => (x.B, x.W * (x.B == dk ? FaceGain : 1.0))).ToList();
                    double sd = 0; foreach (var x in e) if (x.B == dk) sd += x.W;
                    l.Add((B11("atama"), sd * (1 - FaceGain)));
                    return TidyW(l).Select(x => (B: x.Item1, W: x.Item2)).ToList();
                }).ToList();
            }
            if (m.Name is "M_Head" or "M_MouthBag" or "M_AEWMouth")
            {
                keys = wl.SelectMany(e => e.Select(x => x.B)).Distinct().OrderBy(x => x).ToList();
                nb = keys.Count;
                var col = new Dictionary<int, int>();
                for (int k = 0; k < nb; k++) col[keys[k]] = k;
                Wd = new double[n * nb];
                for (int v = 0; v < n; v++) foreach (var (b, w) in wl[v]) Wd[v * nb + col[b]] = w;
            }
            // decimate
            double[] uv;
            if (Target.TryGetValue(m.Name, out int tgtN0) && tgtN0 != 0)
            {
                int tgtN = (int)(tgtN0 * targetScale);
                var imp = new double[n];
                Array.Fill(imp, m.Name == "M_Head" ? 3.0 : 1.0);
                var hand = new double[n];
                for (int k = 0; k < nb; k++)
                    if (Regex.IsMatch(names11[keys[k]], @"_(te|yubi\d\d)$"))
                        for (int v = 0; v < n; v++) hand[v] += Wd[v * nb + k];
                for (int v = 0; v < n; v++) if (hand[v] > 0.5) imp[v] *= 2.0;
                var Wn = new double[n * nb];
                for (int v = 0; v < n; v++)
                {
                    double sm = Math.Max(Np.Sum(Wd.AsSpan(v * nb, nb)), 1e-9);
                    for (int k = 0; k < nb; k++) Wn[v * nb + k] = Wd[v * nb + k] / sm;
                }
                var tris0 = tris;
                var (keep, tr) = Qem.Simplify(V, tris0, tgtN, importance: imp, attr: Wn, attrK: nb, attrWeight: 2.0, lockBorders: m.Name != "M_Teeth");
                if (keep.Length > 1.4 * tgtN && m.Name is not ("M_Head" or "M_Body"))
                {
                    var (keep2, tr2) = Qem.Simplify(V, tris0, tgtN, importance: imp, attr: Wn, attrK: nb, attrWeight: 2.0, lockBorders: false);
                    if (keep2.Length < keep.Length)
                    {
                        Log.Add($"{m.Name}: open-edged pieces - simplified with their edges free ({keep.Length} -> {keep2.Length} points)");
                        (keep, tr) = (keep2, tr2);
                    }
                }
                var remap = new int[n]; Array.Fill(remap, -1);
                for (int i = 0; i < keep.Length; i++) remap[keep[i]] = i;
                tris = tr.Select(t => remap[t]).ToArray();
                V = Take(V, 3, keep); N = Take(N, 3, keep); uv = Take(m.Uv, 2, keep);
                wl = keep.Select(i => wl[i]).ToList();
                tie = keep.Select(i => tie[i]).ToArray();
            }
            else
            {
                var used = tris.Distinct().OrderBy(x => x).ToArray();
                var remap = new int[n]; Array.Fill(remap, -1);
                for (int i = 0; i < used.Length; i++) remap[used[i]] = i;
                tris = tris.Select(t => remap[t]).ToArray();
                V = Take(V, 3, used); N = Take(N, 3, used); uv = Take(m.Uv, 2, used);
                wl = used.Select(i => wl[i]).ToList();
                tie = used.Select(i => tie[i]).ToArray();
            }
            int nv = V.Length / 3;
            // the head / body seam: head vertices on a body vertex take its weights
            if (m.Name == "M_Body") seamBody = (U(V), wl, tie);
            else if (m.Name == "M_Head" && seamBody != null)
            {
                var (bp, bw, btie) = seamBody.Value;
                var hp = U(V);
                int nbp = bp.Length / 3, welded = 0;
                double lim = SeamWeld * SeamWeld;
                for (int i = 0; i < nv; i++)
                {
                    int best = 0; double bd = double.PositiveInfinity;
                    for (int q = 0; q < nbp; q++)
                    {
                        double dx = hp[3 * i] - bp[3 * q], dy = hp[3 * i + 1] - bp[3 * q + 1], dz = hp[3 * i + 2] - bp[3 * q + 2];
                        double d2 = dx * dx + dy * dy + dz * dz;
                        if (d2 < bd) { bd = d2; best = q; }
                    }
                    if (bd < lim) { wl[i] = [.. bw[best]]; tie[i] = btie[best]; welded++; }
                }
                report.Add($"M_Head seam: {welded} vertices take the body's weights");
            }
            // texture cell
            string bas = m.Material.Split(':')[0];
            string tname = bas is "eye_l" or "eye_r" ? "eye" : bas;
            var cell = Cells[tname];
            var cpath = FindTex(chr, attire, tname, "_color");
            var npath = FindTex(chr, attire, tname, "_nrm");
            var (cx0, cy0, cw, ch) = cell;
            bool any = false;
            for (int y = cy0; y < cy0 + ch && !any; y++)
                for (int x = cx0; x < cx0 + cw; x++)
                {
                    int o = 4 * (y * SW + x);
                    if (sheetC[o] != 0 || sheetC[o + 1] != 0 || sheetC[o + 2] != 0) { any = true; break; }
                }
            if (!any)
            {
                Img ci;
                if (cpath != null) ci = CellImage(cpath, cell);
                else
                {
                    var (r, g, b) = Solid.GetValueOrDefault(tname, (128, 128, 128));
                    ci = Img.Filled(cw, ch, (byte)r, (byte)g, (byte)b, 255);
                }
                if (tname != "eyelash" && !Cutout.Contains(tname))
                {
                    byte sw = (byte)Sweat.GetValueOrDefault(tname, 0);
                    for (int i = 0; i < cw * ch; i++) ci.Px[4 * i + 3] = sw;
                }
                for (int y = 0; y < ch; y++) Array.Copy(ci.Px, 4 * y * cw, sheetC, 4 * ((cy0 + y) * SW + cx0), 4 * cw);
                if (npath != null)
                {
                    var ni = CellImage(npath, cell, normal: true);
                    for (int y = 0; y < ch; y++)
                        for (int x = 0; x < cw; x++)
                            for (int c = 0; c < 3; c++) sheetN[4 * ((cy0 + y) * SW + cx0 + x) + c] = ni.Px[4 * (y * cw + x) + c];
                }
                var (baseS, kS) = Shine.GetValueOrDefault(tname, (20, 0));
                for (int y = 0; y < ch; y++)
                    for (int x = 0; x < cw; x++)
                    {
                        int o = 4 * (y * cw + x);
                        double luma = Math.FusedMultiplyAdd(ci.Px[o + 2], 0.114, Math.FusedMultiplyAdd(ci.Px[o], 0.299, Math.FusedMultiplyAdd(ci.Px[o + 1], 0.587, 0.0)));
                        sheetN[4 * ((cy0 + y) * SW + cx0 + x) + 3] = (byte)Math.Clamp(baseS + kS * luma, 0, 254);
                    }
            }
            if (Kind.GetValueOrDefault(m.Name) == "lash") lashCell = cell;
            var suv = CellUv(cell, uv);
            string kind = Kind.GetValueOrDefault(m.Name, "skin");
            int ntri = tris.Length / 3;
            var groups = kind == "skin" ? SplitRigid(tris, wl) : [Enumerable.Range(0, ntri).ToArray()];
            for (int gi = 0; gi < groups.Count; gi++)
            {
                var g = groups[gi];
                var gt = g.SelectMany(t => new[] { tris[3 * t], tris[3 * t + 1], tris[3 * t + 2] }).ToArray();
                var used = gt.Distinct().OrderBy(x => x).ToArray();
                var rm = new int[nv]; Array.Fill(rm, -1);
                for (int i = 0; i < used.Length; i++) rm[used[i]] = i;
                var gb = used.SelectMany(v => wl[v].Select(x => x.B)).Distinct().OrderBy(x => x).ToList();
                if (TieDebug != null) TieDebug[m.Name + (groups.Count == 1 ? "" : "_" + gi)] = Enumerable.Range(0, used.Length).Where(i => tie[used[i]]).ToHashSet();
                var gpos = new Dictionary<int, int>();
                for (int k = 0; k < gb.Count; k++) gpos[gb[k]] = k;
                var Nu = Take(N, 3, used);
                if (StockWinding) for (int i = 0; i < Nu.Length; i++) Nu[i] = -Nu[i];
                var tri = new JsonArray();
                for (int t = 0; t < gt.Length; t += 3)
                    tri.Add(StockWinding ? new JsonArray(rm[gt[t]], rm[gt[t + 2]], rm[gt[t + 1]]) : new JsonArray(rm[gt[t]], rm[gt[t + 1]], rm[gt[t + 2]]));
                var mo = new JsonObject
                {
                    ["name"] = m.Name + (groups.Count == 1 ? "" : "_" + gi), ["kind"] = kind,
                    ["positions"] = Arr(U(Take(V, 3, used)), 3, 5),
                    ["normals"] = Arr(Nu, 3, 5),
                    ["uvs"] = Arr(Take(suv, 2, used), 2, 6),
                    ["bones"] = new JsonArray(gb.Select(b => (JsonNode)names11[b]).ToArray()),
                    ["weights"] = new JsonArray(used.Select(v => (JsonNode)new JsonArray(wl[v].Select(x => (JsonNode)new JsonArray(gpos[x.B], Np.PyRound(x.W, 5))).ToArray())).ToArray()),
                    ["triangles"] = tri,
                };
                if (Cutout.Contains(tname)) mo["material"] = new JsonObject { ["cutout"] = true, ["double_sided"] = true };
                outMeshes.Add(mo);
            }
            report.Add($"{m.Name,-18} {n,6} -> {nv,5} verts {ntri,5} tris  {groups.Count} group(s)");
        }
        // the template's mouth inside: meshes warped into his mouth, its teeth / tongue block of the sheets copied
        var tc = SvrBuilder.TexBundleRead(conv.T[0xc]).ToDictionary(x => x.Item1, x => x.Item2);
        var ta = SvrBuilder.TexBundleRead(conv.T[0xa]).ToDictionary(x => x.Item1, x => x.Item2);
        var tcol = ImageIO.Decode(tc["color"]).ToRgba();
        var tnrm = ImageIO.Decode(ta["normal"]).ToRgba();
        if (tnrm.W != tcol.W || tnrm.H != tcol.H) tnrm = Resample.ResizeRgbaPremultiplied(tnrm, tcol.W, tcol.H, Filter.Bilinear);
        var (sx, sy, sw0, sh0) = TmplMouthSrc;
        var (mx, my) = TmplMouthCell;
        for (int y = 0; y < sh0; y++)
        {
            Array.Copy(tcol.Px, 4 * ((sy + y) * tcol.W + sx), sheetC, 4 * ((my + y) * SW + mx), 4 * sw0);
            Array.Copy(tnrm.Px, 4 * ((sy + y) * tnrm.W + sx), sheetN, 4 * ((my + y) * SW + mx), 4 * sw0);
        }
        if (TemplateMouth)
        {
            var tms = FaceTransfer.TemplateMouth(conv, fit, gain: FaceGain);
            for (int k = 0; k < tms.Count; k++)
            {
                var tm = tms[k];
                var uvm = new double[tm.Uvs.Length];
                for (int i = 0; i < uvm.Length; i += 2)
                {
                    uvm[i] = (tm.Uvs[i] * tcol.W - sx + mx) / SW;
                    uvm[i + 1] = (tm.Uvs[i + 1] * tcol.H - sy + my) / SH;
                }
                var bl = tm.Weights.SelectMany(e => e.Select(x => x.B)).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
                var bpos = new Dictionary<string, int>();
                for (int i = 0; i < bl.Count; i++) bpos[bl[i]] = i;
                var tri = new JsonArray();
                for (int t = 0; t < tm.Triangles.Length; t += 3) tri.Add(new JsonArray(tm.Triangles[t], tm.Triangles[t + 1], tm.Triangles[t + 2]));
                outMeshes.Add(new JsonObject
                {
                    ["name"] = $"mouth_{k}", ["kind"] = "skin",
                    ["positions"] = Arr(tm.Positions, 3, 5), ["normals"] = Arr(tm.Normals, 3, 5), ["uvs"] = Arr(uvm, 2, 6),
                    ["bones"] = new JsonArray(bl.Select(b => (JsonNode)b).ToArray()),
                    ["weights"] = new JsonArray(tm.Weights.Select(e => (JsonNode)new JsonArray(e.Where(x => x.W > 0).Select(x => (JsonNode)new JsonArray(bpos[x.B], Np.PyRound(x.W, 5))).ToArray())).ToArray()),
                    ["triangles"] = tri,
                    ["material"] = tm.Specular != null ? new JsonObject { ["specular"] = tm.Specular.Value } : new JsonObject(),
                });
                report.Add($"mouth_{k} (template)       {tm.Positions.Length / 3,5} verts {tm.Triangles.Length / 3,5} tris");
            }
        }
        ImageIO.SavePng(new Img(SW, SH, 4, sheetC), Path.Combine(outDir, "color.png"));
        ImageIO.SavePng(new Img(SW, SH, 4, sheetN), Path.Combine(outDir, "normal.png"));
        var joints = new JsonObject();
        foreach (var (k, v) in J) joints[k] = new JsonArray(v.Select(c => (JsonNode)Np.PyRound(c, 5)).ToArray());
        var spec = new JsonObject
        {
            ["version"] = 1, ["template"] = template, ["joints"] = joints,
            ["attires"] = new JsonArray(new JsonObject { ["textures"] = new JsonObject { ["color"] = "color.png", ["normal"] = "normal.png" }, ["meshes"] = outMeshes }),
            ["_rock"] = new JsonObject
            {
                ["lash_cell"] = lashCell is { } lc ? new JsonArray(lc.Item1, lc.Item2, lc.Item3, lc.Item4) : null,
                ["origin_cm"] = new JsonArray(origin[0], origin[1], origin[2]),
            },
        };
        File.WriteAllText(Path.Combine(outDir, "model.json"), spec.ToJsonString());
        if (unmapped.Count > 0)
            report.Add("folded into a parent bone: " + string.Join(", ", unmapped.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} {F(kv.Value, 0)}")));
        var Mf = fit.M;
        report.Add($"face fit: landmark error {F(fit.Residual, 3)} units, scale {NpArr([Np.Round(Mf[0], 3), Np.Round(Mf[4], 3), Np.Round(Mf[8], 3)])}; " +
                   $"{fit.Joints.Count} face bones placed, jaw pivot {NpArr(fit.Joints["d_kuchi"].Select(c => Np.Round(c, 2)).ToArray())}");
        int tv = 0; foreach (var mo in outMeshes) tv += mo!["positions"]!.AsArray().Count;
        report.Add($"vertices {tv}, meshes {outMeshes.Count}");
        return (spec, report);
    }
}
