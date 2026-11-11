using PortIT.Engine.Build;
using PortIT.Engine.Formats;
using PortIT.Engine.Util;

namespace PortIT.Engine.Port;

/// <summary>
/// Face rig for a ported head - port of k26/k26_face.py: landmarks on both faces, a per-axis stretch fit (least
/// squares) refined by closest points, a thin-plate spline, the template's face-bone weights copied by nearest
/// template vertices, the face bones moved through the fit; the template's mouth inside warped in.
/// The least squares and the spline's linear solve stand in for LAPACK (lstsq / solve): their results agree to the
/// last few bits only; everything else is in NumPy's arithmetic order.
/// </summary>
public static class Face
{
    public static readonly string[] Upper = ["u_kuchi", "u_kuchi_00", "l_u_kuchi", "r_u_kuchi"];
    public static readonly string[] Lower = ["d_kuchi", "d_kuchi_00", "d_kuchi_01", "l_kuchi", "r_kuchi"];
    public static readonly string[] JawBranch = ["d_kuchi", "d_kuchi_00", "d_kuchi_01", "l_kuchi", "r_kuchi"];
    public const double JawFree = 0.12;

    public static double[] P3(double[] V, int i) => [V[3 * i], V[3 * i + 1], V[3 * i + 2]];
    static double Dist3(double[] a, int i, double[] b, int j)
    {
        double d0 = a[3 * i] - b[3 * j], d1 = a[3 * i + 1] - b[3 * j + 1], d2 = a[3 * i + 2] - b[3 * j + 2];
        return Math.Sqrt(0.0 + d0 * d0 + d1 * d1 + d2 * d2);     // (norm along an axis: one by one from 0)
    }

    // ---------------------------------------------------------------- NumPy helpers
    /// <summary>np.percentile(a, q) (linear): NumPy's lerp, a + d * t, or b - d * (1 - t) from t = 0.5</summary>
    public static double Percentile(double[] a, double q)
    {
        var s = (double[])a.Clone();
        Array.Sort(s);
        double vi = q / 100.0 * (s.Length - 1);
        int lo = (int)Math.Floor(vi);
        if (lo >= s.Length - 1) return s[^1];
        double t = vi - lo, x = s[lo], y = s[lo + 1], diff = y - x;
        return t >= 0.5 ? y - diff * (1 - t) : x + diff * t;
    }

    static int[] Unique(IEnumerable<int> v) => v.Distinct().OrderBy(x => x).ToArray();

    // ---------------------------------------------------------------- mesh helpers
    static List<int[]> BorderLoops(int[] tris)
    {
        var ec = new Dictionary<(int, int), int>();
        var order = new List<(int, int)>();
        for (int t = 0; t < tris.Length; t += 3)
            for (int i = 0; i < 3; i++)
            {
                int a = tris[t + i], b = tris[t + (i + 1) % 3];
                var e = (Math.Min(a, b), Math.Max(a, b));
                if (ec.TryGetValue(e, out int c)) ec[e] = c + 1; else { ec[e] = 1; order.Add(e); }
            }
        var adj = new Dictionary<int, List<int>>();
        var adjOrder = new List<int>();
        foreach (var (a, b) in order)
        {
            if (ec[(a, b)] != 1) continue;
            if (!adj.TryGetValue(a, out var la)) { adj[a] = la = []; adjOrder.Add(a); }
            la.Add(b);
            if (!adj.TryGetValue(b, out var lb)) { adj[b] = lb = []; adjOrder.Add(b); }
            lb.Add(a);
        }
        var seen = new HashSet<int>();
        var loops = new List<int[]>();
        foreach (int s in adjOrder)
        {
            if (seen.Contains(s)) continue;
            var comp = new List<int>();
            var st = new List<int> { s };
            while (st.Count > 0)
            {
                int x = st[^1]; st.RemoveAt(st.Count - 1);
                if (!seen.Add(x)) continue;
                comp.Add(x);
                st.AddRange(adj[x]);
            }
            loops.Add(comp.ToArray());
        }
        return loops;
    }

    /// <summary>vertices at one position -&gt; one (UV seams); never across sides -&gt; per vertex its representative</summary>
    public static int[] WeldRep(double[] V, int[]? side = null, double tol = 1e-3)
    {
        int n = V.Length / 3;
        var key = new Dictionary<(long, long, long, int), int>();
        var rep = new int[n];
        for (int i = 0; i < n; i++)
        {
            var k = ((long)Math.Round(V[3 * i] / tol, MidpointRounding.ToEven), (long)Math.Round(V[3 * i + 1] / tol, MidpointRounding.ToEven),
                     (long)Math.Round(V[3 * i + 2] / tol, MidpointRounding.ToEven), side != null ? side[i] : int.MinValue);
            if (!key.TryGetValue(k, out int r)) key[k] = r = i;
            rep[i] = r;
        }
        return rep;
    }

    public sealed record Marks(Dictionary<string, double[]> L, int[] Loop);

    public static Marks Landmarks(double[] V, int[] tris, double[] eyeL, double[] eyeR, int[]? side = null, int[]? weldSide = null)
    {
        var rep = WeldRep(V, side ?? weldSide);
        var wt = tris.Select(v => rep[v]).ToArray();
        double[] mid = [(eyeL[0] + eyeR[0]) / 2, (eyeL[1] + eyeR[1]) / 2, (eyeL[2] + eyeR[2]) / 2];
        double iod = Blas.Norm([eyeL[0] - eyeR[0], eyeL[1] - eyeR[1], eyeL[2] - eyeR[2]]);
        int[]? best = null;
        if (side != null)
        {
            var used = Unique(tris);
            var up = used.Where(i => side[i] == -1).ToArray();
            var lo = used.Where(i => side[i] == 1).ToArray();
            var nearU = new bool[up.Length]; var nearL = new bool[lo.Length];
            for (int a = 0; a < up.Length; a++)
                for (int b = 0; b < lo.Length; b++)
                    if (Dist3(V, up[a], V, lo[b]) < 0.02) { nearU[a] = true; nearL[b] = true; }
            var corner = new List<int>();
            for (int t = 0; t < tris.Length; t += 3)
            {
                bool hu = false, hl = false;
                for (int k = 0; k < 3; k++) { hu |= side[tris[t + k]] == -1; hl |= side[tris[t + k]] == 1; }
                if (hu && hl) corner.AddRange([tris[t], tris[t + 1], tris[t + 2]]);
            }
            var b2 = up.Where((_, a) => nearU[a]).Concat(lo.Where((_, b) => nearL[b])).Concat(Unique(corner)).ToArray();
            if (b2.Length > 0) best = b2;
        }
        if (best == null)
            foreach (var L in BorderLoops(wt))
            {
                // (mean over rows: one by one from the first, / n)
                double[] c = P3(V, L[0]);
                for (int q = 1; q < L.Length; q++) for (int k = 0; k < 3; k++) c[k] += V[3 * L[q] + k];
                for (int k = 0; k < 3; k++) c[k] /= L.Length;
                if (!(0.6 * iod < c[1] - mid[1] && c[1] - mid[1] < 2.2 * iod) || Math.Abs(c[0] - mid[0]) > 0.3 * iod) continue;
                if (c[2] - mid[2] > 0.6 * iod) continue;
                double w = L.Max(i => V[3 * i]) - L.Min(i => V[3 * i]);
                if (!(0.4 * iod < w && w < 2.0 * iod)) continue;
                if (best == null || L.Length > best.Length) best = L;
            }
        if (best == null) throw new InvalidDataException("no mouth opening found");
        int ArgBy(IList<int> ids, Func<int, double> f, bool max)
        {
            int bi = ids[0];
            foreach (int i in ids) if (max ? f(i) > f(bi) : f(i) < f(bi)) bi = i;
            return bi;
        }
        var ml = P3(V, ArgBy(best, i => V[3 * i], true));
        var mr = P3(V, ArgBy(best, i => V[3 * i], false));
        double[] mc = [Np.Sum(best.Select(i => V[3 * i]).ToArray()) / best.Length, Np.Sum(best.Select(i => V[3 * i + 1]).ToArray()) / best.Length, best.Min(i => V[3 * i + 2])];
        var usedAll = Unique(tris);
        var sel = usedAll.Where(i => Math.Abs(V[3 * i] - mid[0]) < 0.25 * iod && V[3 * i + 1] > mid[1] && V[3 * i + 1] < mc[1]).ToList();
        var nose = P3(V, ArgBy(sel, i => V[3 * i + 2], false));
        sel = usedAll.Where(i => Math.Abs(V[3 * i] - mid[0]) < 0.25 * iod && V[3 * i + 1] > mc[1] + 0.3 * iod && V[3 * i + 1] < mc[1] + 1.6 * iod).ToList();
        var chin = P3(V, ArgBy(sel, i => V[3 * i + 1] - V[3 * i + 2], true));
        var Ld = new Dictionary<string, double[]>
        {
            ["eye_l"] = eyeL, ["eye_r"] = eyeR, ["mouth_l"] = ml, ["mouth_r"] = mr, ["mouth"] = mc, ["nose"] = nose, ["chin"] = chin,
        };
        return new Marks(Ld, best);
    }

    // ---------------------------------------------------------------- fits
    /// <summary>least squares x' = s x + t per axis (np.linalg.lstsq stand-in: the normal equations, centred), the
    /// depth scale tied to the mean of the other two -&gt; (4, 3) row-major: [x 1] @ M</summary>
    public static double[] Affine(double[] src, double[] dst, double[] w, double depthTie = 4.0)
    {
        int n = w.Length;
        var sc = new double[3]; var tr = new double[3];
        for (int ax = 0; ax < 3; ax++)
        {
            // minimise sum w (s x + t - y)^2: weighted means, then the slope
            double sw = 0, sx = 0, sy = 0;
            for (int i = 0; i < n; i++) { sw += w[i]; sx += w[i] * src[3 * i + ax]; sy += w[i] * dst[3 * i + ax]; }
            double mx = sx / sw, my = sy / sw, sxx = 0, sxy = 0;
            for (int i = 0; i < n; i++) { double dx = src[3 * i + ax] - mx; sxx += w[i] * dx * dx; sxy += w[i] * dx * (dst[3 * i + ax] - my); }
            sc[ax] = sxx > 0 ? sxy / sxx : 0;
            tr[ax] = my - sc[ax] * mx;
        }
        double sm = (sc[0] + sc[1]) / 2;
        double W2 = Np.Sum(w);
        var s2 = Enumerable.Range(0, n).Select(i => src[3 * i + 2]).ToArray();
        var d2 = Enumerable.Range(0, n).Select(i => dst[3 * i + 2]).ToArray();
        double avgD = Np.Sum(d2.Select((v, i) => v * w[i]).ToArray()) / Np.Sum(w);
        double avgS = Np.Sum(s2.Select((v, i) => v * w[i]).ToArray()) / Np.Sum(w);
        double meanS = Np.Sum(s2) / n;
        double varS = Np.Sum(s2.Select(v => (v - meanS) * (v - meanS)).ToArray()) / n;
        double num = Np.Sum(Enumerable.Range(0, n).Select(i => w[i] * s2[i] * (d2[i] - avgD)).ToArray()) + depthTie * W2 * sm * varS;
        double den = Np.Sum(Enumerable.Range(0, n).Select(i => w[i] * s2[i] * (s2[i] - avgS)).ToArray()) + depthTie * W2 * varS + 1e-12;
        double sz = num / den;
        sc[2] = sz;
        tr[2] = Np.Sum(Enumerable.Range(0, n).Select(i => (d2[i] - sz * s2[i]) * w[i]).ToArray()) / Np.Sum(w);
        var M = new double[12];
        M[0] = sc[0]; M[4] = sc[1]; M[8] = sc[2];
        M[9] = tr[0]; M[10] = tr[1]; M[11] = tr[2];
        return M;
    }

    /// <summary>[P 1] @ M for n x 3 points and a (4, 3) M: fma over k = 0..3 from 0</summary>
    public static double[] ApplyAffine(double[] P, double[] M)
    {
        int n = P.Length / 3;
        var o = new double[3 * n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < 3; j++)
            {
                double s = 0.0;
                for (int k = 0; k < 3; k++) s = Math.FusedMultiplyAdd(P[3 * i + k], M[3 * k + j], s);
                o[3 * i + j] = Math.FusedMultiplyAdd(1.0, M[9 + j], s);
            }
        return o;
    }

    /// <summary>np.append(p, 1) @ M for one point: the vector-matrix order (fma over rows 1, 0, 2, 3)</summary>
    public static double[] ApplyAffine1(double[] p, double[] M)
    {
        var o = new double[3];
        for (int j = 0; j < 3; j++)
        {
            double s = 0.0;
            s = Math.FusedMultiplyAdd(p[1], M[3 + j], s);
            s = Math.FusedMultiplyAdd(p[0], M[j], s);
            s = Math.FusedMultiplyAdd(p[2], M[6 + j], s);
            s = Math.FusedMultiplyAdd(1.0, M[9 + j], s);
            o[j] = s;
        }
        return o;
    }

    public sealed class Tps { public double[] X = [], W = [], A = []; }

    /// <summary>3D thin-plate spline (kernel r), per-point smoothing lam (np.linalg.solve stand-in: Gaussian elimination
    /// with partial pivoting)</summary>
    public static Tps TpsFit(double[] X, double[] Y, double[] lam)
    {
        int n = lam.Length, N = n + 4;
        var A = new double[N, N];
        var B = new double[N, 3];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) A[i, j] = Dist3(X, i, X, j) + (i == j ? lam[i] : 0);
            A[i, n] = 1; A[i, n + 1] = X[3 * i]; A[i, n + 2] = X[3 * i + 1]; A[i, n + 3] = X[3 * i + 2];
            A[n, i] = 1; A[n + 1, i] = X[3 * i]; A[n + 2, i] = X[3 * i + 1]; A[n + 3, i] = X[3 * i + 2];
            for (int c = 0; c < 3; c++) B[i, c] = Y[3 * i + c];
        }
        var sol = Solve(A, B);
        var t = new Tps { X = X, W = new double[3 * n], A = new double[12] };
        for (int i = 0; i < n; i++) for (int c = 0; c < 3; c++) t.W[3 * i + c] = sol[i, c];
        for (int i = 0; i < 4; i++) for (int c = 0; c < 3; c++) t.A[3 * i + c] = sol[n + i, c];
        return t;
    }

    static double[,] Solve(double[,] A0, double[,] B0)
    {
        int n = A0.GetLength(0), m = B0.GetLength(1);
        var A = (double[,])A0.Clone(); var B = (double[,])B0.Clone();
        for (int c = 0; c < n; c++)
        {
            int piv = c;
            for (int r = c + 1; r < n; r++) if (Math.Abs(A[r, c]) > Math.Abs(A[piv, c])) piv = r;
            if (piv != c)
            {
                for (int k = 0; k < n; k++) (A[c, k], A[piv, k]) = (A[piv, k], A[c, k]);
                for (int k = 0; k < m; k++) (B[c, k], B[piv, k]) = (B[piv, k], B[c, k]);
            }
            for (int r = c + 1; r < n; r++)
            {
                double f = A[r, c] / A[c, c];
                if (f == 0) continue;
                for (int k = c; k < n; k++) A[r, k] -= f * A[c, k];
                for (int k = 0; k < m; k++) B[r, k] -= f * B[c, k];
            }
        }
        var X = new double[n, m];
        for (int r = n - 1; r >= 0; r--)
            for (int k = 0; k < m; k++)
            {
                double s = B[r, k];
                for (int q = r + 1; q < n; q++) s -= A[r, q] * X[q, k];
                X[r, k] = s / A[r, r];
            }
        return X;
    }

    public static double[] TpsApply(Tps t, double[] P)
    {
        int n = P.Length / 3, m = t.X.Length / 3;
        var o = new double[3 * n];
        for (int i = 0; i < n; i++)
            for (int c = 0; c < 3; c++)
            {
                double s = 0.0;
                for (int j = 0; j < m; j++) s = Math.FusedMultiplyAdd(Dist3(P, i, t.X, j), t.W[3 * j + c], s);
                double a = 0.0;
                a = Math.FusedMultiplyAdd(1.0, t.A[c], a);
                for (int k = 0; k < 3; k++) a = Math.FusedMultiplyAdd(P[3 * i + k], t.A[3 * (k + 1) + c], a);
                o[3 * i + c] = s + a;
            }
        return o;
    }

    /// <summary>index of the nearest B point for each A point, and the distance (k26_face._nearest's formula)</summary>
    public static (int[] J, double[] D) Nearest(double[] A, double[] B)
    {
        int na = A.Length / 3, nb = B.Length / 3;
        var bb = new double[nb];
        for (int j = 0; j < nb; j++) bb[j] = 0.0 + B[3 * j] * B[3 * j] + B[3 * j + 1] * B[3 * j + 1] + B[3 * j + 2] * B[3 * j + 2];
        var J = new int[na]; var Dd = new double[na];
        Parallel.For(0, na, i =>
        {
            double aa = 0.0 + A[3 * i] * A[3 * i] + A[3 * i + 1] * A[3 * i + 1] + A[3 * i + 2] * A[3 * i + 2];
            int bj = 0; double bd = double.PositiveInfinity;
            for (int j = 0; j < nb; j++)
            {
                double ab = Math.FusedMultiplyAdd(A[3 * i + 2], B[3 * j + 2], Math.FusedMultiplyAdd(A[3 * i + 1], B[3 * j + 1], Math.FusedMultiplyAdd(A[3 * i], B[3 * j], 0.0)));
                double d = aa - 2 * ab + bb[j];
                if (d < bd) { bd = d; bj = j; }
            }
            J[i] = bj; Dd[i] = Math.Sqrt(Math.Max(bd, 0));
        });
        return (J, Dd);
    }

    // ---------------------------------------------------------------- the template's face
    public sealed class TemplateFace
    {
        public double[] V = [], N = [];
        public int[] T = [];
        public List<List<(string B, double W)>> W = [];
        public HashSet<string> FaceBones = [];
        public Dictionary<string, double[]> Pos = [];
        public double[] EyeL = [], EyeR = [];
    }

    public static List<double[]> World11(SvrBuilder.Template c)
    {
        var Wn = new List<double[]>();
        for (int j = 0; j < c.TNodes.Count; j++)
        {
            var (_, r, p) = c.TFrame[j];
            var R = SvrBuilder.Rot(r);
            var t = c.TNodes[j].T;
            double[] M = [R[0], R[1], R[2], t[0], R[3], R[4], R[5], t[1], R[6], R[7], R[8], t[2], 0, 0, 0, 1];
            Wn.Add(p < 0 ? M : Blas.MatMul(Wn[p], M, 4));
        }
        return Wn;
    }

    static List<(int, int, int)> MeshTris(Jboy.Mesh s)
    {
        var o = new List<(int, int, int)>();
        foreach (var (prim, idx) in s.Strips)
        {
            if (prim == 6) o.AddRange(Jboy.StripToTris(idx));
            else for (int j = 0; j + 2 < idx.Length; j += 3) o.Add((idx[j], idx[j + 1], idx[j + 2]));
        }
        return o;
    }

    public static TemplateFace TemplateFaceOf(SvrBuilder.Template c)
    {
        var Wn = World11(c);
        var names = c.TNodes.Select(n => n.Name).ToList();
        int atama = c.TIndex["atama"];
        var under = new HashSet<int>();
        for (int i = 0; i < c.TFrame.Count; i++)
        {
            int j = i;
            while (j >= 0) { if (j == atama) { under.Add(i); break; } j = c.TFrame[j].Parent; }
        }
        var tf = new TemplateFace();
        var V = new List<double>(); var N = new List<double>(); var T = new List<int>();
        foreach (var s in c.Body.Meshes)
        {
            if (s.Shader == "yReflect") continue;
            int nw = s.Weights.Count, vc = s.Verts.Count;
            var flat = s.Weights.SelectMany(b => b).ToList();
            var bset = flat.Where(e => e.Bones[0] != 255 && e.W > 0).Select(e => names[s.Palette[e.Bones[0]] - 1]).ToHashSet();
            if (vc <= 600 && bset.IsSubsetOf(["atama", "d_kuchi"])) continue;
            int off = V.Count / 3;
            for (int v = 0; v < vc; v++)
            {
                for (int k = 0; k < 3; k++) { V.Add(s.Verts[v].PosNrm[k]); N.Add(s.Verts[v].PosNrm[3 + k]); }
                tf.W.Add(flat.GetRange(v * nw, nw).Where(e => e.Bones[0] != 255 && e.W > 0).Select(e => (names[s.Palette[e.Bones[0]] - 1], (double)e.W)).ToList());
            }
            foreach (var (a, b, cc) in MeshTris(s))
                if (a != b && b != cc && a != cc) T.AddRange([a + off, b + off, cc + off]);
        }
        tf.V = V.ToArray(); tf.N = N.ToArray(); tf.T = T.ToArray();
        tf.FaceBones = under.Select(i => names[i]).Where(n => n is not ("atama" or "l_eye" or "r_eye")).ToHashSet();
        foreach (int i in under) tf.Pos[names[i]] = [Wn[i][3], Wn[i][7], Wn[i][11]];
        int le = c.TIndex["l_eye"], re = c.TIndex["r_eye"];
        tf.EyeL = [Wn[le][3], Wn[le][7], Wn[le][11]];
        tf.EyeR = [Wn[re][3], Wn[re][7], Wn[re][11]];
        return tf;
    }

    // ---------------------------------------------------------------- the fit
    public sealed class Fit
    {
        public Tps Warp = new();
        public Dictionary<string, double[]> LTmpl = [], LDst = [];
        public double[] M = [];
        public Dictionary<string, double[]> Joints = [];
        public double[] Sx = [];
        public List<List<(string B, double W)>> SW = [];
        public double[] SideT = [];
        public HashSet<string> FaceBones = [];
        public int[] MouthLoop = [];
        public double Residual;
        public bool LipsTouch;
        public readonly List<string> Log = [];
        public double[] Apply(double[] P) => TpsApply(Warp, P);
    }

    static int SideOf(List<(string B, double W)> e)
    {
        double up = 0, lo = 0;
        foreach (var (b, w) in e) { if (Upper.Contains(b)) up += w; if (Lower.Contains(b)) lo += w; }
        return up > lo + 0.05 ? -1 : lo > up + 0.05 ? 1 : 0;
    }

    public static Fit FitFace(SvrBuilder.Template c, double[] V, int[] tris, double[] eyeL, double[] eyeR, int[]? srcSide = null, int icpRounds = 8)
    {
        var tf = TemplateFaceOf(c);
        var fit = new Fit { FaceBones = tf.FaceBones };
        var head = tf.W.Select(wl => wl.Any(e => tf.FaceBones.Contains(e.B) || e.B == "atama")).ToArray();
        var th = Enumerable.Range(0, head.Length).Where(i => head[i]).ToArray();
        var keptT = new List<int>();
        for (int t = 0; t < tf.T.Length; t += 3) if (head[tf.T[t]] && head[tf.T[t + 1]] && head[tf.T[t + 2]]) keptT.AddRange([tf.T[t], tf.T[t + 1], tf.T[t + 2]]);
        var sideAll = tf.W.Select(SideOf).ToArray();
        var Ls = Landmarks(tf.V, keptT.ToArray(), tf.EyeL, tf.EyeR, side: sideAll).L;
        Marks md;
        bool touching = false;
        try { md = Landmarks(V, tris, eyeL, eyeR); }
        catch (InvalidDataException)
        {
            if (srcSide == null) throw;
            md = Landmarks(V, tris, eyeL, eyeR, weldSide: srcSide.Select(x => x + 1).ToArray());
            touching = true;
            fit.Log.Add("face fit: the lips touch - opened along the source rig lip line");
        }
        var Ld = md.L;
        var keys = Ls.Keys.ToList();
        var src = keys.SelectMany(k => Ls[k]).ToArray();
        var dst = keys.SelectMany(k => Ld[k]).ToArray();
        var lw = keys.Select(k => k.StartsWith("eye") || k.StartsWith("mouth") ? 8.0 : 4.0).ToArray();
        var M = Affine(src, dst, lw);
        var S = th.Where(i => tf.V[3 * i + 1] < Ls["chin"][1]).SelectMany(i => P3(tf.V, i)).ToArray();
        var used = Unique(tris);
        var D = used.SelectMany(i => P3(V, i)).ToArray();
        for (int r = 0; r < icpRounds; r++)
        {
            var Sx0 = ApplyAffine(S, M);
            var (j, d) = Nearest(Sx0, D);
            double lim = Percentile(d, 85);
            var ok = Enumerable.Range(0, d.Length).Where(i => d[i] < lim).ToArray();
            var s2 = ok.SelectMany(i => P3(S, i)).Concat(src).ToArray();
            var d2 = ok.SelectMany(i => P3(D, j[i])).Concat(dst).ToArray();
            double f = Math.Max(1.0, ok.Length / 40.0);
            var w2 = Enumerable.Repeat(1.0, ok.Length).Concat(lw.Select(x => x * f)).ToArray();
            M = Affine(s2, d2, w2);
        }
        var Sx = ApplyAffine(S, M);
        var (jj, dd) = Nearest(Sx, D);
        double lim80 = Percentile(dd, 80);
        var okIdx = Enumerable.Range(0, dd.Length).Where(i => dd[i] < lim80).ToArray();
        var pick = NpRandom.Seed0().ChooseIndices(okIdx.Length, Math.Min(okIdx.Length, 220)).Select(x => okIdx[x]).ToArray();
        var ctrl = src.Concat(pick.SelectMany(i => P3(S, i))).ToArray();
        var goal = dst.Concat(pick.SelectMany(i => P3(D, jj[i]))).ToArray();
        var lam = Enumerable.Repeat(0.0, src.Length / 3).Concat(Enumerable.Repeat(0.05, pick.Length)).ToArray();
        fit.Warp = TpsFit(ctrl, goal, lam);
        // bone pivots: deep ones through the whole-head stretch, the rest through the warp
        double eyeZ = (tf.EyeL[2] + tf.EyeR[2]) / 2;
        var joints = new Dictionary<string, double[]>();
        foreach (var b in tf.FaceBones)
        {
            if (!tf.Pos.TryGetValue(b, out var p)) continue;
            joints[b] = p[2] > eyeZ + 0.3 ? ApplyAffine1(p, M) : fit.Apply(p);
        }
        // the guard: lip bones by the template mouth's local stretch; others far from the stretch take the stretch
        var a = Ls; var bd = Ld;
        double sxm = Math.Abs(bd["mouth_l"][0] - bd["mouth_r"][0]) / Math.Max(Math.Abs(a["mouth_l"][0] - a["mouth_r"][0]), 1e-6);
        double den = a["chin"][1] - a["nose"][1];
        double sym = (bd["chin"][1] - bd["nose"][1]) / (den != 0 ? den : 1e-6);
        double[] msc = [sxm, sym, (sxm + sym) / 2];
        var guarded = new List<string>();
        foreach (var b in joints.Keys.ToList())
        {
            var p = tf.Pos[b];
            var pa = ApplyAffine1(p, M);
            double[] q;
            if (b.Contains("kuchi") && b != "d_kuchi") q = [(p[0] - a["mouth"][0]) * msc[0] + bd["mouth"][0], (p[1] - a["mouth"][1]) * msc[1] + bd["mouth"][1], (p[2] - a["mouth"][2]) * msc[2] + bd["mouth"][2]];
            else if (Blas.Norm([joints[b][0] - pa[0], joints[b][1] - pa[1], joints[b][2] - pa[2]]) > 0.25) q = pa;
            else continue;
            double mv = Blas.Norm([joints[b][0] - q[0], joints[b][1] - q[1], joints[b][2] - q[2]]);
            if (mv > 0.05) guarded.Add($"{b} {mv:F2}");
            joints[b] = q;
        }
        if (guarded.Count > 0) fit.Log.Add("face fit: bones placed by the stretch, not the spline (moved by): " + string.Join(", ", guarded));
        var TV = th.SelectMany(i => P3(tf.V, i)).ToArray();
        var SxT = fit.Apply(TV);
        var Sa = ApplyAffine(TV, M);
        int sw = 0;
        for (int i = 0; i < th.Length; i++)
            if (Dist3(SxT, i, Sa, i) > 0.3) { for (int k = 0; k < 3; k++) SxT[3 * i + k] = Sa[3 * i + k]; sw++; }
        if (sw > 0) fit.Log.Add($"face fit: {sw} template vertices placed by the stretch (the spline swung them)");
        fit.LTmpl = Ls; fit.LDst = Ld; fit.M = M; fit.Joints = joints; fit.Sx = SxT;
        fit.SW = th.Select(i => tf.W[i]).ToList();
        fit.SideT = th.Select(i => (double)SideOf(tf.W[i])).ToArray();
        fit.MouthLoop = md.Loop;
        fit.LipsTouch = touching;
        var ap = fit.Apply(src);
        var rr = new double[ap.Length / 3];
        for (int i = 0; i < rr.Length; i++) rr[i] = Dist3(ap, i, dst, i);
        fit.Residual = Np.Sum(rr) / rr.Length;
        return fit;
    }
}
