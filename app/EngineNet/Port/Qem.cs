using PortIT.Engine.Util;

namespace PortIT.Engine.Port;

/// <summary>
/// Mesh decimation by half-edge collapses ordered by quadric error (Garland-Heckbert) - port of k26/qem.py. A collapse
/// only removes a vertex (b goes onto its neighbour a, which keeps its position and attributes).
/// The costs are computed in NumPy's own order (measured on its results: 1-D dots and lengths are fused multiply-add
/// chains from 0; the 4x4 vector-matrix product is OpenBLAS's - fused, rows 1, 0, 2, 3; einsum's 3-term dot is
/// (p0 + p2) + p1), so the same vertices are removed in the same order as the Python engine.
/// </summary>
public static class Qem
{
    /// <summary>-&gt; keep: surviving vertex indices (sorted), tris: n x 3 in the original indices</summary>
    public static (int[] Keep, int[] Tris) Simplify(double[] V, int[] Fin, int target, bool[]? locked = null, double[]? importance = null,
                                                   double[]? attr = null, int attrK = 0, double attrWeight = 0.0, double minDot = 0.5,
                                                   double minQuality = 0.15, bool lockBorders = true)
    {
        int n = V.Length / 3;
        var F = new List<int[]>();
        for (int i = 0; i + 2 < Fin.Length; i += 3)
        {
            int a = Fin[i], b = Fin[i + 1], c = Fin[i + 2];
            if (a != b && b != c && a != c) F.Add([a, b, c]);
        }
        var lck = locked != null ? (bool[])locked.Clone() : new bool[n];
        var imp = importance ?? Enumerable.Repeat(1.0, n).ToArray();
        var ecount = new Dictionary<(int, int), int>();
        foreach (var t in F)
            for (int i = 0; i < 3; i++)
            {
                var e = (Math.Min(t[i], t[(i + 1) % 3]), Math.Max(t[i], t[(i + 1) % 3]));
                ecount[e] = ecount.GetValueOrDefault(e) + 1;
            }
        var border = new bool[n];
        var bedges = new HashSet<(int, int)>();
        foreach (var ((a, b), c) in ecount)
        {
            if (c == 1) { border[a] = border[b] = true; bedges.Add((a, b)); }
            if (c > 2 || (c == 1 && lockBorders)) lck[a] = lck[b] = true;
        }
        var Q = Quadrics(V, F, n);
        var faces = F.Select(t => (int[])t.Clone()).ToList();
        var aliveF = Enumerable.Repeat(true, faces.Count).ToArray();
        var vf = new HashSet<int>[n];
        for (int i = 0; i < n; i++) vf[i] = [];
        for (int fi = 0; fi < faces.Count; fi++) foreach (var v in faces[fi]) vf[v].Add(fi);
        var used = new bool[n];
        foreach (var t in F) foreach (var v in t) used[v] = true;
        var alive = (bool[])used.Clone();
        int count = used.Count(x => x);
        var stamp = new int[n];

        HashSet<int> Nbrs(int v)
        {
            var s = new HashSet<int>();
            foreach (var fi in vf[v]) foreach (var x in faces[fi]) s.Add(x);
            s.Remove(v);
            return s;
        }

        double Sq3(int a, int b)        // ((V[a] - V[b]) ** 2).sum(): sequential from 0
        {
            double d0 = V[3 * a] - V[3 * b], d1 = V[3 * a + 1] - V[3 * b + 1], d2 = V[3 * a + 2] - V[3 * b + 2];
            return 0.0 + d0 * d0 + d1 * d1 + d2 * d2;
        }

        double Cost(int b, int a)
        {
            Span<double> q = stackalloc double[16];
            for (int k = 0; k < 16; k++) q[k] = Q[16 * a + k] + Q[16 * b + k];
            Span<double> x = [V[3 * a], V[3 * a + 1], V[3 * a + 2], 1.0];
            Span<double> xq = stackalloc double[4];
            for (int j = 0; j < 4; j++)
            {
                double s = 0.0;
                s = Math.FusedMultiplyAdd(x[1], q[4 + j], s);
                s = Math.FusedMultiplyAdd(x[0], q[j], s);
                s = Math.FusedMultiplyAdd(x[2], q[8 + j], s);
                s = Math.FusedMultiplyAdd(x[3], q[12 + j], s);
                xq[j] = s;
            }
            double c = 0.0;
            for (int j = 0; j < 4; j++) c = Math.FusedMultiplyAdd(xq[j], x[j], c);
            if (attr != null && attrWeight != 0)
            {
                double L2 = Sq3(a, b);
                Span<double> d = attrK <= 256 ? stackalloc double[attrK] : new double[attrK];
                for (int k = 0; k < attrK; k++) d[k] = Math.Abs(attr[a * attrK + k] - attr[b * attrK + k]);
                c += attrWeight * Np.Sum(d) * L2;
            }
            return Math.Max(c, 0.0) * imp[b] + 1e-12 * Sq3(a, b);
        }

        var heap = new PriorityQueue<(int B, int A, int Sb, int Sa), (double, int, int, int, int)>(Comparer<(double, int, int, int, int)>.Create(Cmp));
        void Push(int v, int u, int sv, int su) { double c = Cost(v, u); heap.Enqueue((v, u, sv, su), (c, v, u, sv, su)); }
        void PushEdges(int v)
        {
            foreach (var u in Nbrs(v))
            {
                if (!lck[v]) Push(v, u, stamp[v], stamp[u]);
                if (!lck[u]) Push(u, v, stamp[u], stamp[v]);
            }
        }
        foreach (var (a, b) in ecount.Keys)
        {
            if (!lck[a]) Push(a, b, 0, 0);
            if (!lck[b]) Push(b, a, 0, 0);
        }

        double[] Normal(int p0, int p1, int p2)
        {
            double ax = V[3 * p1] - V[3 * p0], ay = V[3 * p1 + 1] - V[3 * p0 + 1], az = V[3 * p1 + 2] - V[3 * p0 + 2];
            double bx = V[3 * p2] - V[3 * p0], by = V[3 * p2 + 1] - V[3 * p0 + 1], bz = V[3 * p2 + 2] - V[3 * p0 + 2];
            return [ay * bz - az * by, az * bx - ax * bz, ax * by - ay * bx];
        }
        static double Dot3(double[] a, double[] b) => Math.FusedMultiplyAdd(a[2], b[2], Math.FusedMultiplyAdd(a[1], b[1], Math.FusedMultiplyAdd(a[0], b[0], 0.0)));
        static double Norm3(double[] a) => Math.Sqrt(Dot3(a, a));
        double Quality(int p0, int p1, int p2)      // 1 for an equilateral triangle, 0 for a sliver
        {
            double a = Norm3(Normal(p0, p1, p2));
            double e = Sq3(p1, p0) + Sq3(p2, p1) + Sq3(p0, p2);
            return e > 0 ? 2 * 1.7320508075688772 * a / e : 0.0;
        }

        while (count > target && heap.Count > 0)
        {
            var (b, a, sb, sa) = heap.Dequeue();
            if (!(alive[a] && alive[b]) || stamp[b] != sb || stamp[a] != sa) continue;
            var shared = vf[b].Where(fi => faces[fi].Contains(a)).ToList();
            if (shared.Count == 0) continue;
            // (borders unlocked: a border vertex only slides along its own border, onto a border neighbour)
            if (border[b] && !bedges.Contains((Math.Min(a, b), Math.Max(a, b)))) continue;
            // link condition: the common neighbours are exactly the shared triangles' third vertices
            var third = new HashSet<int>(shared.SelectMany(fi => faces[fi]));
            third.Remove(a); third.Remove(b);
            var common = Nbrs(a);
            common.IntersectWith(Nbrs(b));
            if (!common.SetEquals(third)) continue;
            bool ok = true;
            foreach (var fi in vf[b])
            {
                if (shared.Contains(fi)) continue;
                var t = faces[fi];
                var old = Normal(t[0], t[1], t[2]);
                int[] nt = [t[0] == b ? a : t[0], t[1] == b ? a : t[1], t[2] == b ? a : t[2]];
                var nw = Normal(nt[0], nt[1], nt[2]);
                double lo = Norm3(old), ln = Norm3(nw);
                if (ln < 1e-12 || (lo > 1e-12 && Dot3(old, nw) < minDot * lo * ln)) { ok = false; break; }
                double qn = Quality(nt[0], nt[1], nt[2]);
                if (qn < minQuality && qn < Quality(t[0], t[1], t[2])) { ok = false; break; }   // (no new slivers)
            }
            if (!ok) continue;
            foreach (var fi in shared)
            {
                aliveF[fi] = false;
                foreach (var v in faces[fi]) vf[v].Remove(fi);
            }
            foreach (var fi in vf[b].ToList())
            {
                var t = faces[fi];
                for (int k = 0; k < 3; k++) if (t[k] == b) t[k] = a;
                vf[a].Add(fi);
            }
            vf[b].Clear();
            for (int k = 0; k < 16; k++) Q[16 * a + k] += Q[16 * b + k];
            alive[b] = false;
            count--;
            stamp[a]++;
            PushEdges(a);
        }
        var tris = new List<int>();
        for (int fi = 0; fi < faces.Count; fi++) if (aliveF[fi]) tris.AddRange(faces[fi]);
        var keep = Enumerable.Range(0, n).Where(i => alive[i] && used[i]).ToArray();
        return (keep, tris.ToArray());
    }

    static int Cmp((double, int, int, int, int) x, (double, int, int, int, int) y)
    {
        int c = x.Item1.CompareTo(y.Item1);
        if (c != 0) return c;
        if ((c = x.Item2.CompareTo(y.Item2)) != 0) return c;
        if ((c = x.Item3.CompareTo(y.Item3)) != 0) return c;
        if ((c = x.Item4.CompareTo(y.Item4)) != 0) return c;
        return x.Item5.CompareTo(y.Item5);
    }

    /// <summary>per-vertex 4x4 error quadrics (qem._quadrics), NumPy's arithmetic order</summary>
    static double[] Quadrics(double[] V, List<int[]> F, int n)
    {
        var Q = new double[16 * n];
        int m = F.Count;
        var K = new double[16 * m];
        for (int r = 0; r < m; r++)
        {
            int ia = F[r][0], ib = F[r][1], ic = F[r][2];
            double ax = V[3 * ia], ay = V[3 * ia + 1], az = V[3 * ia + 2];
            double ux = V[3 * ib] - ax, uy = V[3 * ib + 1] - ay, uz = V[3 * ib + 2] - az;
            double vx = V[3 * ic] - ax, vy = V[3 * ic + 1] - ay, vz = V[3 * ic + 2] - az;
            double n0 = uy * vz - uz * vy, n1 = uz * vx - ux * vz, n2 = ux * vy - uy * vx;
            double area = Math.Sqrt(0.0 + n0 * n0 + n1 * n1 + n2 * n2);
            bool ok = area > 1e-12;
            if (ok) { n0 /= area; n1 /= area; n2 /= area; }
            double d = -((n0 * ax + n2 * az) + n1 * ay);
            double w = area * (ok ? 1.0 : 0.0);
            double[] p = [n0, n1, n2, d];
            for (int j = 0; j < 4; j++) for (int k = 0; k < 4; k++) K[16 * r + 4 * j + k] = w * p[j] * p[k];
        }
        // (np.add.at per corner: every triangle's first vertex, then the second, then the third)
        for (int corner = 0; corner < 3; corner++)
            for (int r = 0; r < m; r++)
            {
                int v = F[r][corner];
                for (int k = 0; k < 16; k++) Q[16 * v + k] += K[16 * r + k];
            }
        return Q;
    }
}
