using System.Buffers.Binary;
using PortIT.Engine.Build;
using PortIT.Engine.Formats;
using PortIT.Engine.Util;

namespace PortIT.Engine.Port;

/// <summary>the face weight transfer and the template mouth (port of k26_face.py: cut_lips, lip_sides, transfer,
/// template_mouth)</summary>
public static class FaceTransfer
{
    static double Dist(double[] V, int a, int b)
    {
        return Blas.Norm([V[3 * a] - V[3 * b], V[3 * a + 1] - V[3 * b + 1], V[3 * a + 2] - V[3 * b + 2]]);
    }

    /// <summary>touching lips: triangles joining an upper-lip vertex to a lower-lip one between the corners, near the
    /// mouth's line, left out -&gt; kept triangles</summary>
    public static int[] CutLips(double[] V, int[] tris, int[] srcSide, Dictionary<string, double[]> L, List<string> log, double margin = 0.1)
    {
        double x0 = Math.Min(L["mouth_l"][0], L["mouth_r"][0]), x1 = Math.Max(L["mouth_l"][0], L["mouth_r"][0]);
        var keep = new List<int>();
        int cut = 0;
        for (int t = 0; t < tris.Length; t += 3)
        {
            int a = tris[t], b = tris[t + 1], c = tris[t + 2];
            int mn = Math.Min(srcSide[a], Math.Min(srcSide[b], srcSide[c])), mx = Math.Max(srcSide[a], Math.Max(srcSide[b], srcSide[c]));
            double cx = (V[3 * a] + V[3 * b] + V[3 * c]) / 3, cy = (V[3 * a + 1] + V[3 * b + 1] + V[3 * c + 1]) / 3;
            bool cross = mn < 0 && mx > 0, near = cx > x0 && cx < x1 && Math.Abs(cy - L["mouth"][1]) < margin;
            if (cross && near) { cut++; continue; }
            keep.AddRange([a, b, c]);
        }
        if (cut > 0) log.Add($"face fit: the touching lips opened - {cut} triangles between them left out");
        return keep.ToArray();
    }

    /// <summary>per vertex -1 upper lip, +1 lower lip, 0 neither: the nearer half of the lips' border loop along the surface</summary>
    public static int[] LipSides(double[] V, int[] tris, int[] loop, double radius = 0.35)
    {
        int n = V.Length / 3;
        var rep = Face.WeldRep(V);
        var wt = tris.Select(v => rep[v]).ToArray();
        var adj = new Dictionary<int, HashSet<int>>();
        void Add(int a, int b) { if (!adj.TryGetValue(a, out var s)) adj[a] = s = []; s.Add(b); }
        for (int t = 0; t < wt.Length; t += 3)
            for (int i = 0; i < 3; i++) { int a = wt[t + i], b = wt[t + (i + 1) % 3]; Add(a, b); Add(b, a); }
        var ec = new Dictionary<(int, int), int>();
        var eorder = new List<(int, int)>();
        for (int t = 0; t < wt.Length; t += 3)
            for (int i = 0; i < 3; i++)
            {
                int a = wt[t + i], b = wt[t + (i + 1) % 3];
                var e = (Math.Min(a, b), Math.Max(a, b));
                if (ec.TryGetValue(e, out int c)) ec[e] = c + 1; else { ec[e] = 1; eorder.Add(e); }
            }
        var ring = loop.Select(i => rep[i]).ToHashSet();
        var nb = new Dictionary<int, List<int>>();
        foreach (var (a, b) in eorder)
            if (ec[(a, b)] == 1 && ring.Contains(a) && ring.Contains(b))
            {
                if (!nb.TryGetValue(a, out var la)) nb[a] = la = []; la.Add(b);
                if (!nb.TryGetValue(b, out var lb)) nb[b] = lb = []; lb.Add(a);
            }
        int start = ring.Aggregate((x, y) => V[3 * y] > V[3 * x] ? y : x);
        var used = new HashSet<(int, int)>();
        var cyc = new List<int> { start };
        int? prev = null; int cur = start;
        while (true)
        {
            var nxt = nb.GetValueOrDefault(cur, []).Where(x => !used.Contains((Math.Min(cur, x), Math.Max(cur, x)))).ToList();
            if (nxt.Count == 0) break;
            int n2;
            if (prev == null || nxt.Count == 1) n2 = nxt[0];
            else
            {
                double[] d0 = [V[3 * cur] - V[3 * prev.Value], V[3 * cur + 1] - V[3 * prev.Value + 1]];
                double l0 = Math.Max(Blas.Norm(d0), 1e-9);
                d0 = [d0[0] / l0, d0[1] / l0];
                double Turn(int x)
                {
                    double[] d1 = [V[3 * x] - V[3 * cur], V[3 * x + 1] - V[3 * cur + 1]];
                    double l1 = Math.Max(Blas.Norm(d1), 1e-9);
                    return -Blas.Dot(d0, [d1[0] / l1, d1[1] / l1]);
                }
                n2 = nxt.Aggregate((x, y) => Turn(y) < Turn(x) ? y : x);
            }
            used.Add((Math.Min(cur, n2), Math.Max(cur, n2)));
            prev = cur; cur = n2;
            if (cur == start) break;
            cyc.Add(cur);
        }
        int kmin = cyc.IndexOf(cyc.Aggregate((x, y) => V[3 * y] < V[3 * x] ? y : x));
        var halfA = cyc.Take(kmin + 1).ToList();
        var halfB = cyc.Skip(kmin).Append(start).ToList();
        double OffRingY(List<int> h)
        {
            var ys = h.Skip(1).Take(Math.Max(0, h.Count - 2)).SelectMany(r => adj.GetValueOrDefault(r, []).Where(x => !ring.Contains(x)).Select(x => V[3 * x + 1])).ToArray();
            return ys.Length == 0 ? double.NaN : Np.Sum(ys) / ys.Length;      // (np.mean of nothing: nan)
        }
        var (up, lo) = OffRingY(halfA) < OffRingY(halfB) ? (halfA, halfB) : (halfB, halfA);
        var dist = new Dictionary<int, double>(); var lab = new Dictionary<int, int>();
        var heap = new PriorityQueue<int, (double, int)>();
        foreach (var r in up.Skip(1).Take(Math.Max(0, up.Count - 2))) { dist[r] = 0.0; lab[r] = -1; heap.Enqueue(r, (0.0, r)); }
        foreach (var r in lo.Skip(1).Take(Math.Max(0, lo.Count - 2))) { dist[r] = 0.0; lab[r] = 1; heap.Enqueue(r, (0.0, r)); }
        while (heap.TryDequeue(out int a, out var pr))
        {
            double d = pr.Item1;
            if (d > dist.GetValueOrDefault(a, 1e9) || d > radius) continue;
            foreach (int b in adj.GetValueOrDefault(a, []))
            {
                double nd = d + Dist(V, a, b);
                if (nd < dist.GetValueOrDefault(b, 1e9) && nd <= radius) { dist[b] = nd; lab[b] = lab[a]; heap.Enqueue(b, (nd, b)); }
            }
        }
        var outs = Enumerable.Range(0, n).Select(i => lab.GetValueOrDefault(rep[i], 0)).ToArray();
        var far = Enumerable.Range(0, n).Select(i => dist.GetValueOrDefault(rep[i], 0.0) > 0.08).ToArray();
        var yu = Enumerable.Range(0, n).Where(i => outs[i] == -1 && far[i]).Select(i => V[3 * i + 1]).ToArray();
        var yl = Enumerable.Range(0, n).Where(i => outs[i] == 1 && far[i]).Select(i => V[3 * i + 1]).ToArray();
        if (yu.Length > 0 && yl.Length > 0 && Np.Sum(yu) / yu.Length > Np.Sum(yl) / yl.Length) outs = outs.Select(x => -x).ToArray();
        return outs;
    }

    /// <summary>new head vertices + their own weights -&gt; weights with the template's face bones (gain tones the face)</summary>
    public static List<List<(string B, double W)>> Transfer(Face.Fit fit, double[] V, List<List<(string B, double W)>> own, int[]? tris = null,
                                                          int k = 4, double reach = 0.6, double gain = 1.0, int[]? srcSide = null)
    {
        int n = V.Length / 3;
        var Ld = fit.LDst;
        double[] ml = Ld["mouth_l"], mr = Ld["mouth_r"], mc = Ld["mouth"];
        int[]? sides = null;
        if (tris != null)
        {
            try { sides = LipSides(V, tris, fit.MouthLoop); } catch (Exception) { sides = null; }
        }
        if (srcSide != null && (sides == null || sides.All(x => x == 0)))
        {
            var Lp = fit.MouthLoop;
            sides = new int[n];
            for (int i = 0; i < n; i++)
            {
                double best = double.MaxValue;
                foreach (int q in Lp)
                {
                    double d0 = V[3 * i] - V[3 * q], d1 = V[3 * i + 1] - V[3 * q + 1], d2 = V[3 * i + 2] - V[3 * q + 2];
                    best = Math.Min(best, Math.Sqrt(0.0 + d0 * d0 + d1 * d1 + d2 * d2));
                }
                sides[i] = best < 0.35 ? srcSide[i] : 0;
            }
        }
        if (sides != null && srcSide != null && srcSide.Length == sides.Length)
            sides = sides.Select((s, i) => s != 0 && srcSide[i] != 0 ? srcSide[i] : s).ToArray();
        double above = Math.Min(ml[1], Math.Min(mr[1], mc[1])) - Face.JawFree;
        var Sx = fit.Sx;
        int m = Sx.Length / 3;
        var bb = new double[m];
        for (int j = 0; j < m; j++) bb[j] = 0.0 + Sx[3 * j] * Sx[3 * j] + Sx[3 * j + 1] * Sx[3 * j + 1] + Sx[3 * j + 2] * Sx[3 * j + 2];
        var outl = new List<(string, double)>[n];
        Parallel.For(0, n, i =>
        {
            double vv = 0.0 + V[3 * i] * V[3 * i] + V[3 * i + 1] * V[3 * i + 1] + V[3 * i + 2] * V[3 * i + 2];
            var d = new double[m];
            int s = sides != null ? sides[i] : 0;
            for (int j = 0; j < m; j++)
            {
                double ab = Math.FusedMultiplyAdd(V[3 * i + 2], Sx[3 * j + 2], Math.FusedMultiplyAdd(V[3 * i + 1], Sx[3 * j + 1], Math.FusedMultiplyAdd(V[3 * i], Sx[3 * j], 0.0)));
                d[j] = Math.Sqrt(Math.Max(vv - 2 * ab + bb[j], 0));
                if (s != 0 && fit.SideT[j] != s) d[j] = double.PositiveInfinity;
            }
            // (the k nearest: a stable sort by distance - NumPy's argsort may order exact ties differently)
            var nn = Enumerable.Range(0, m).OrderBy(j => d[j]).Take(k).ToArray();
            if (!double.IsFinite(d[nn[0]]) || (d[nn[0]] > reach && s == 0)) { outl[i] = own[i]; return; }
            var wsum = new List<(string B, double W)>();
            double tot = 0.0;
            foreach (int j in nn)
            {
                if (!double.IsFinite(d[j])) continue;
                double g = 1.0 / (d[j] + 0.02);
                tot += g;
                foreach (var (b, w) in fit.SW[j])
                    if (fit.FaceBones.Contains(b))
                    {
                        int at = wsum.FindIndex(x => x.B == b);
                        if (at < 0) wsum.Add((b, g * w)); else wsum[at] = (b, wsum[at].W + g * w);
                    }
            }
            var face = wsum.Where(x => gain * x.W / tot > 0.01).Select(x => (x.B, gain * x.W / tot)).ToList();
            double F = 0;
            foreach (var (_, w) in face) F += w;
            if (F <= 0) { outl[i] = own[i]; return; }
            F = Math.Min(F, 1.0);
            var mix = face.Concat(own[i].Select(e => (e.B, e.W * (1 - F)))).ToList();
            if (V[3 * i + 1] < above)
            {
                double jw = 0;
                foreach (var (b, w) in mix) if (Face.JawBranch.Contains(b)) jw += w;
                if (jw > 0) mix = mix.Where(e => !Face.JawBranch.Contains(e.Item1)).Append(("atama", jw)).ToList();
            }
            outl[i] = mix;
        });
        return outl.ToList();
    }

    public sealed class Mouth
    {
        public double[] Positions = [], Normals = [], Uvs = [];
        public List<List<(string B, double W)>> Weights = [];
        public int[] Triangles = [];
        public double? Specular;
    }

    /// <summary>the template's mouth inside (small skin meshes on atama / d_kuchi only) warped into the new mouth by a
    /// local stretch from the mouth landmarks</summary>
    public static List<Mouth> TemplateMouth(SvrBuilder.Template c, Face.Fit fit, double gain = 1.0)
    {
        var names = c.TNodes.Select(nd => nd.Name).ToList();
        var outl = new List<Mouth>();
        foreach (var s in c.Body.Meshes)
        {
            if (s.Shader != "yBumpMapSS" || s.Verts.Count > 600) continue;
            int nw = s.Weights.Count;
            var flat = s.Weights.SelectMany(b => b).ToList();
            var wl = Enumerable.Range(0, s.Verts.Count).Select(v => flat.GetRange(v * nw, nw).Where(e => e.Bones[0] != 255 && e.W > 0)
                .Select(e => (B: names[s.Palette[e.Bones[0]] - 1], W: (double)e.W)).ToList()).ToList();
            if (!wl.All(e => e.All(x => x.B is "atama" or "d_kuchi"))) continue;
            var a = fit.LTmpl; var b = fit.LDst;
            double sx = Math.Abs(b["mouth_l"][0] - b["mouth_r"][0]) / Math.Abs(a["mouth_l"][0] - a["mouth_r"][0]);
            double sy = (b["chin"][1] - b["nose"][1]) / (a["chin"][1] - a["nose"][1]);
            double[] sc = [sx, sy, (sx + sy) / 2];
            int nv = s.Verts.Count;
            var Q = new double[3 * nv]; var Nn = new double[3 * nv];
            for (int v = 0; v < nv; v++)
            {
                var pn = s.Verts[v].PosNrm;
                for (int k = 0; k < 3; k++) { Q[3 * v + k] = (pn[k] - a["mouth"][k]) * sc[k] + b["mouth"][k]; Nn[3 * v + k] = pn[3 + k] / sc[k]; }
                double l = Math.Max(Math.Sqrt(0.0 + Nn[3 * v] * Nn[3 * v] + Nn[3 * v + 1] * Nn[3 * v + 1] + Nn[3 * v + 2] * Nn[3 * v + 2]), 1e-9);
                for (int k = 0; k < 3; k++) Nn[3 * v + k] /= l;
            }
            var tris = new List<int>();
            foreach (var (prim, idx) in s.Strips)
            {
                var tr = prim == 6 ? Jboy.StripToTris(idx) : Enumerable.Range(0, Math.Max(0, (idx.Length - 2 + 2) / 3)).Where(j => 3 * j + 2 < idx.Length).Select(j => ((int)idx[3 * j], (int)idx[3 * j + 1], (int)idx[3 * j + 2])).ToList();
                foreach (var (x, y, z) in tr) if (x != y && y != z && x != z) tris.AddRange([x, y, z]);
            }
            var p = s.Params.GroupBy(q => q.Name).ToDictionary(g => g.Key, g => g.Last().Value);
            double? spec = p.TryGetValue("g_fSpecularLev", out var sv) ? BinaryPrimitives.ReadSingleBigEndian(sv) : null;
            var wl2 = wl.Select(e =>
            {
                double dk = 0;
                foreach (var (bn, w) in e) if (bn == "d_kuchi") dk += w;
                return e.Select(x => (x.B, x.W * (x.B == "d_kuchi" ? gain : 1.0))).Append(("atama", dk * (1 - gain))).ToList();
            }).ToList();
            outl.Add(new Mouth
            {
                Positions = Q, Normals = Nn, Uvs = s.Uvs.SelectMany(u => new[] { (double)u.U, (double)u.V }).ToArray(),
                Weights = wl2, Triangles = tris.ToArray(), Specular = spec,
            });
        }
        return outl;
    }
}
