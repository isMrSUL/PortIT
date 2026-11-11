namespace PortIT.Engine.Build;

/// <summary>triangle list -&gt; one degenerate-joined strip (svr11_build.stripify: strip_tris convention, odd triangles
/// flipped; each start triangle tried in its three rotations, the longest strip kept)</summary>
public static class Strip
{
    public static List<int> Stripify(List<(int, int, int)> input)
    {
        var tris = input.Where(t => t.Item1 != t.Item2 && t.Item2 != t.Item3 && t.Item1 != t.Item3).ToList();
        // (edge -> triangles with that directed edge, in triangle order)
        var edge = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < tris.Count; i++)
        {
            var (a, b, c) = tris[i];
            foreach (var e in new[] { (a, b), (b, c), (c, a) })
            {
                if (!edge.TryGetValue(e, out var l)) edge[e] = l = [];
                l.Add(i);
            }
        }
        var used = new bool[tris.Count];
        static bool InCyc((int, int, int) t, (int, int, int) d) =>
            d == t || d == (t.Item2, t.Item3, t.Item1) || d == (t.Item3, t.Item1, t.Item2);
        (List<int> S, List<int> Took) Grow(int first, (int, int, int) start)
        {
            var s = new List<int> { start.Item1, start.Item2, start.Item3 };
            var took = new List<int> { first };
            var tookSet = new HashSet<int> { first };
            while (true)
            {
                int a = s[^2], b = s[^1];
                int k = s.Count - 2;               // (the new triangle's index in the strip)
                (int J, int D)? nxt = null;
                foreach (var e in new[] { (a, b), (b, a) })
                {
                    var cands = new List<int>();
                    if (edge.TryGetValue((e.Item2, e.Item1), out var l1)) cands.AddRange(l1);
                    if (edge.TryGetValue(e, out var l2)) cands.AddRange(l2);
                    foreach (int j in cands)
                    {
                        if (used[j] || tookSet.Contains(j)) continue;
                        var t = tris[j];
                        int d = t.Item1 != a && t.Item1 != b ? t.Item1 : t.Item2 != a && t.Item2 != b ? t.Item2 : t.Item3;
                        var drawn = k % 2 == 0 ? (a, b, d) : (b, a, d);
                        if (InCyc(t, drawn)) { nxt = (j, d); break; }
                    }
                    if (nxt != null) break;
                }
                if (nxt == null) return (s, took);
                took.Add(nxt.Value.J);
                tookSet.Add(nxt.Value.J);
                s.Add(nxt.Value.D);
            }
        }
        var strips = new List<List<int>>();
        for (int i = 0; i < tris.Count; i++)
        {
            if (used[i]) continue;
            var t = tris[i];
            (List<int> S, List<int> Took)? best = null;
            foreach (var rot in new[] { t, (t.Item2, t.Item3, t.Item1), (t.Item3, t.Item1, t.Item2) })
            {
                var g = Grow(i, rot);
                if (best == null || g.S.Count > best.Value.S.Count) best = g;
            }
            used[i] = true;
            foreach (int j in best!.Value.Took) used[j] = true;
            strips.Add(best.Value.S);
        }
        var o = new List<int>();
        foreach (var s in strips)
        {
            if (o.Count > 0)
            {
                o.Add(o[^1]); o.Add(s[0]);
                if (o.Count % 2 == 1) o.Add(s[0]);   // (keep the next strip's first triangle on an even index)
            }
            o.AddRange(s);
        }
        return o;
    }
}
