using System.Text;
using PortIT.Engine.Formats;

namespace PortIT.Engine.Build;

/// <summary>
/// Moving hair (the user's request 2026-11-12): bone chains for the hair that hangs from the head, the way SvR 2011's own
/// long-haired superstars have them, so the game's hair solver ("gBaH") swings it. What the game wants (the port makers'
/// HAIR_PHYSICS.md, checked against stock ch110 Edge):
///  - chains kami_bNN (NN 00-15) with children kami_bNN_00, _01, ... in the hair model (ch.pac child 0x2710);
///    kami_bNN's parent is atama, each child hangs off the one before; every bone points along its local +X and every
///    segment of a chain is one length L (the child's local translation is (L, 0, 0));
///  - node 0 is a fixed pivot, node 1 follows the head, nodes 2.. are simulated, the last node is only a target: the hair
///    is skinned to nodes 0 .. n-2 only;
///  - child 0x3C: one text line per chain "kami_bNN radius visc grav spring damp A lencorr bend movecorr";
///    child 0x3D: the body's collision capsules (taken from a stock superstar).
/// Rotations are stored as (rx, ry, rz) at node bytes 32 / 36 / 40 (Jboy.Node: R[1], R[2], U40) and turn as Rz Ry Rx.
/// </summary>
public static class HairChains
{
    /// <summary>a stock-like line (Edge: radius 0.15-0.20, visc 0.10-0.15, grav 0.08, spring 0.02, bend 0.03-0.05)</summary>
    public const string Params = "0.18 0.12\t0.08\t0.02\t0.0\t0\t0.0\t0.04\t0.0";
    public const double SegLen = 0.85;       // (stock segments: 0.83 - 0.85 units)
    public const int MaxChains = 16;
    /// <summary>a hanging hair point further than this from every strand (units, ~0.11 m) keeps its weights</summary>
    public const double MaxReach = 1.6;

    public sealed class Result
    {
        public List<Jboy.Node> Nodes = [];                   // (the hair model's node table: the shared nodes + the chains)
        public string Settings = "";                         // (child 0x3C)
        public int Chains;
        public List<string> Notes = [];
        public List<int> ExtrasMoved = [];                   // (the extras - hair ties, beads - that went onto the chains)
    }

    static double[] Sub(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
    static double[] Add(double[] a, double[] b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];
    static double[] Mul(double[] a, double s) => [a[0] * s, a[1] * s, a[2] * s];
    static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    static double[] Cross(double[] a, double[] b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
    static double Len(double[] a) => Math.Sqrt(Dot(a, a));
    static double[] Unit(double[] a) { double l = Math.Max(Len(a), 1e-12); return Mul(a, 1 / l); }

    /// <summary>3 x 3 rotation (row-major, columns = the frame's X / Y / Z axes) of a 4 x 4 frame</summary>
    static double[] Rot3(double[] W) => [W[0], W[1], W[2], W[4], W[5], W[6], W[8], W[9], W[10]];
    static double[] Pos(double[] W) => [W[3], W[7], W[11]];
    static double[] MulT(double[] R, double[] v) => [R[0] * v[0] + R[3] * v[1] + R[6] * v[2], R[1] * v[0] + R[4] * v[1] + R[7] * v[2], R[2] * v[0] + R[5] * v[1] + R[8] * v[2]];
    static double[] MatTMat(double[] A, double[] B)
    {
        var o = new double[9];
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) o[3 * i + j] = A[i] * B[j] + A[3 + i] * B[3 + j] + A[6 + i] * B[6 + j];
        return o;
    }
    /// <summary>R = Rz(c) Ry(b) Rx(a) -&gt; (a, b, c) (SvrBuilder.Rot's order)</summary>
    static double[] Euler(double[] R)
    {
        double b = Math.Asin(Math.Clamp(-R[6], -1, 1));
        double a = Math.Atan2(R[7], R[8]);
        double c = Math.Atan2(R[3], R[0]);
        return [a, b, c];
    }
    static double[] Columns(double[] x, double[] y, double[] z) => [x[0], y[0], z[0], x[1], y[1], z[1], x[2], y[2], z[2]];

    /// <summary>the chains for the hair meshes (positions per mesh, their weights as (node index, weight) lists - rewritten
    /// in place for the hair that moves), the shared node table and its world frames (4 x 4, column vectors)</summary>
    public static Result? Build(List<Jboy.Node> nodes, List<double[]> worlds, Dictionary<string, int> index,
                                List<(double[][] Pos, List<List<(int B, double W)>> W)> hair, List<double[][]> skin, Action<string> note,
                                List<(double[][] Pos, List<List<(int B, double W)>> W)>? extras = null)
    {
        if (!index.TryGetValue("atama", out int ia)) { note("moving hair: no atama bone - left out"); return null; }
        var Wa = worlds[ia];
        var Pa = Pos(Wa); var Ra = Rot3(Wa);
        // the face's front (from the eyes) and the head's size (from the head skin above the neck)
        double[] front = [0, 0, -1];
        if (index.TryGetValue("l_eye", out int le) && index.TryGetValue("r_eye", out int re))
        {
            var mid = Mul(Add(Pos(worlds[le]), Pos(worlds[re])), 0.5);
            var f = Sub(mid, Pa); f[1] = 0;
            if (Len(f) > 1e-6) front = Unit(f);
        }
        double crown = Pa[1];
        foreach (var s in skin) foreach (var p in s) if (Math.Abs(p[0] - Pa[0]) < 1.5 && Math.Abs(p[2] - Pa[2]) < 1.5) crown = Math.Min(crown, p[1]);
        double headH = Math.Max(Pa[1] - crown, 1.0);              // (+Y is down: the crown has the smallest y)
        double rootY = Pa[1] - 0.55 * headH;                        // (the roots: a little above the ears, as Edge's)
        double hangY = Pa[1] + 0.35 * headH;                        // (hair below this hangs: under the jaw)
        // hair points that hang: well below the roots, not at the face's front, weighted to the head (not a beard on the jaw)
        double Az(double[] p) { var d = Sub(p, Pa); d[1] = 0; double ang = Math.Atan2(Dot(Cross(front, d), [0, -1, 0]), Dot(front, d)); return ang; }
        const int K = 12;                                           // (sectors round the head)
        double secW = 2 * Math.PI / K;
        int Sector(double az) => ((int)Math.Floor((az + Math.PI) / secW) % K + K) % K;
        double SecMid(int s) => -Math.PI + (s + 0.5) * secW;
        bool Face(double az) => Math.Abs(az) < 0.95;                // (+-54 degrees of the front: bangs, beards stay rigid)
        var pts = new List<double[]>[K];
        for (int s = 0; s < K; s++) pts[s] = [];
        int headBones = ia;
        foreach (var (P, W) in hair)
            for (int v = 0; v < P.Length; v++)
            {
                var p = P[v];
                if (p[1] < rootY) continue;
                double az = Az(p);
                if (Face(az)) continue;
                pts[Sector(az)].Add(p);
            }
        // a chain per sector whose hair reaches below the jaw
        var chains = new List<(int Sector, List<double[]> Pts)>();
        for (int s = 0; s < K; s++)
        {
            var ps = pts[s];
            if (ps.Count < 30) continue;
            double low = ps.Max(p => p[1]);
            if (low < hangY) continue;
            // the strand's path: the mean point of each height slice from the roots down
            var path = new List<double[]>();
            const double slice = 0.35;
            for (double y = rootY; y <= low + 1e-6; y += slice)
            {
                var sl = ps.Where(p => p[1] >= y && p[1] < y + slice).ToList();
                if (sl.Count < 4) continue;
                var c = new double[3];
                foreach (var p in sl) for (int k = 0; k < 3; k++) c[k] += p[k] / sl.Count;
                path.Add(c);
            }
            if (path.Count < 3) continue;
            // resampled at one length L (as the game needs), from the first point down to the last
            double total = 0;
            for (int i = 1; i < path.Count; i++) total += Len(Sub(path[i], path[i - 1]));
            int nseg = Math.Clamp((int)Math.Round(total / SegLen), 2, 8);
            double L = total / nseg;
            var nodesAt = new List<double[]> { path[0] };
            double want = L, run = 0;
            for (int i = 1; i < path.Count && nodesAt.Count <= nseg; i++)
            {
                double seg = Len(Sub(path[i], path[i - 1]));
                while (run + seg >= want - 1e-9 && nodesAt.Count <= nseg)
                {
                    double t = (want - run) / Math.Max(seg, 1e-12);
                    nodesAt.Add(Add(path[i - 1], Mul(Sub(path[i], path[i - 1]), t)));
                    want += L;
                }
                run += seg;
            }
            while (nodesAt.Count < nseg + 1) nodesAt.Add(path[^1]);
            // exact equal segments: each node L from the one before along its direction
            for (int i = 1; i < nodesAt.Count; i++) nodesAt[i] = Add(nodesAt[i - 1], Mul(Unit(Sub(nodesAt[i], nodesAt[i - 1])), L));
            chains.Add((s, nodesAt));
        }
        if (chains.Count == 0) { note("moving hair: the hair doesn't hang below the jaw - it stays as it is"); return null; }
        if (chains.Count > MaxChains) chains = chains.Take(MaxChains).ToList();

        // nodes: kami_bNN under atama, then the children; frames turned so +X points along the strand
        var res = new Result { Nodes = nodes.Select(n => n.Clone()).ToList(), Chains = chains.Count };
        var sb = new StringBuilder();
        sb.Append(";PortIT moving hair: name radius visc grav spring damp A lencorr bend movecorr\r\n");
        var chainBones = new List<(int Sector, int First, int Count, List<double[]> P, double L)>();
        for (int c = 0; c < chains.Count; c++)
        {
            var P = chains[c].Pts;
            int n = P.Count;
            double L = Len(Sub(P[1], P[0]));
            string root = $"kami_b{c:00}";
            int first = res.Nodes.Count;
            double[] Rp = Ra, Pp = Pa;
            int parent = ia;
            double[]? prevY = null, prevX = null;
            for (int j = 0; j < n; j++)
            {
                var x = j < n - 1 ? Unit(Sub(P[j + 1], P[j])) : Unit(Sub(P[j], P[j - 1]));
                // y: the previous frame's y turned by the shortest arc from its x onto the new x - the turn the game's
                // write-back gives a bone (826A8890), so the hair is not twisted when the solver takes over
                double[] y;
                if (prevY == null)
                {
                    var y0 = new double[] { Ra[1], Ra[4], Ra[7] };
                    y = Sub(y0, Mul(x, Dot(y0, x)));
                    if (Len(y) < 1e-6) { var t = Math.Abs(x[0]) < 0.9 ? new double[] { 1, 0, 0 } : [0, 0, 1]; y = Sub(t, Mul(x, Dot(t, x))); }
                }
                else
                {
                    var ax = Cross(prevX!, x);
                    double sn = Len(ax), cs = Dot(prevX!, x);
                    if (sn < 1e-9) y = prevY;
                    else
                    {
                        var k = Mul(ax, 1 / sn);
                        // Rodrigues: v cos + (k x v) sin + k (k.v)(1 - cos)
                        y = Add(Add(Mul(prevY, cs), Mul(Cross(k, prevY), sn)), Mul(k, Dot(k, prevY) * (1 - cs)));
                    }
                }
                y = Unit(Sub(y, Mul(x, Dot(y, x))));
                var z = Cross(x, y);
                var Rw = Columns(x, y, z);
                var Rl = MatTMat(Rp, Rw);
                var e = Euler(Rl);
                var T = j == 0 ? MulT(Rp, Sub(P[0], Pp)) : [L, 0, 0];
                var nd = new Jboy.Node
                {
                    Name = j == 0 ? root : $"{root}_{j - 1:00}",
                    T = T, R = [0, e[0], e[1]], U40 = BitConverter.SingleToUInt32Bits((float)e[2]), U44 = 0,
                    U48 = [(uint)parent, 0, 0, 0],
                    Sphere = j == 0 ? [0, 0, 0, 0] : [P[j][0], P[j][1], P[j][2], L * 0.8],
                };
                res.Nodes.Add(nd);
                parent = res.Nodes.Count - 1;
                Rp = Rw; Pp = P[j]; prevY = y; prevX = x;
            }
            chainBones.Add((chains[c].Sector, first, n, P, L));
            sb.Append($"{root}\t{Params}\r\n");
        }
        res.Settings = sb.ToString();

        // weights: a hanging point goes to the bone of the segment it lies along, blended between the two nearest chains
        // (by angle) and, near a joint, with the bone before; points above the roots keep theirs
        var faceBones = index.Where(kv => System.Text.RegularExpressions.Regex.IsMatch(kv.Key, "mayu|hoho|kuchi|hana|mabuta|meziri|miken|eye")).Select(kv => kv.Value).ToHashSet();
        int moved = 0;
        foreach (var (P, W) in hair) moved += Assign(P, W, false);
        // pieces worn on the hanging hair (hair ties, beads): when nearly all of a piece lies on the strands, it goes with them
        if (extras != null)
            for (int ei = 0; ei < extras.Count; ei++)
            {
                var (P, W) = extras[ei];
                if (P.Length == 0) continue;
                int on = P.Count(p => p[1] >= rootY && !Face(Az(p)) && chainBones.Any(cb => NearStrand(cb.P, p) < 0.6));
                if (on < 0.8 * P.Length) continue;
                Assign(P, W, true);
                res.ExtrasMoved.Add(ei);
            }

        double NearStrand(List<double[]> Pn, double[] p)
        {
            double best = double.MaxValue;
            for (int j = 0; j < Pn.Count - 1; j++)
            {
                var ab = Sub(Pn[j + 1], Pn[j]);
                double t = Math.Clamp(Dot(Sub(p, Pn[j]), ab) / Math.Max(Dot(ab, ab), 1e-12), 0, 1);
                best = Math.Min(best, Len(Sub(Add(Pn[j], Mul(ab, t)), p)));
            }
            return best;
        }

        int Assign(double[][] P, List<List<(int B, double W)>> W, bool all)
        {
          int count = 0;
          for (int v = 0; v < P.Length; v++)
            {
                var p = P[v];
                if (p[1] < rootY) continue;
                double az = Az(p);
                if (Face(az)) continue;
                // (a beard / face card: its weights are on face bones, not the head - left alone)
                // (hanging hair lying on the back is on mune / sakotsu now - it moves; only face-bone hair stays)
                double faceW = W[v].Where(e => faceBones.Contains(e.B)).Sum(e => e.W);
                if (faceW > 0.5 && !all) continue;
                // the two nearest chains by distance to their strand (a braid crossing sides stays on its chain)
                var near = new List<(int C, double D, int Seg, double Frac)>();
                for (int ci = 0; ci < chainBones.Count; ci++)
                {
                    var Pn = chainBones[ci].P;
                    double best = double.MaxValue; int bs = 0; double bf = 0;
                    for (int j = 0; j < Pn.Count - 1; j++)
                    {
                        var a = Pn[j]; var b = Pn[j + 1];
                        var ab = Sub(b, a);
                        double t = Math.Clamp(Dot(Sub(p, a), ab) / Math.Max(Dot(ab, ab), 1e-12), 0, 1);
                        double dd = Len(Sub(Add(a, Mul(ab, t)), p));
                        if (dd < best) { best = dd; bs = j; bf = t; }
                    }
                    near.Add((ci, best, bs, bf));
                }
                near = near.OrderBy(x => x.D).Take(2).ToList();
                if (near[0].D > MaxReach) continue;                   // (far from every strand: stays as it is)
                var ws = new List<(int B, double W)>();
                double wsum = 0;
                foreach (var (ci, d, seg0, frac) in near)
                {
                    if (d > MaxReach) continue;
                    // (inverse-distance share; a point right on one strand goes to it alone)
                    double aw = near.Count == 2 && near[1].D <= MaxReach ? 1.0 / Math.Max(d, 0.05) : 1.0;
                    var cb = chainBones[ci];
                    int seg = Math.Min(seg0, cb.Count - 2);
                    int bone = cb.First + seg;
                    if (seg > 0 && frac < 0.3)
                    {
                        double g = (0.3 - frac) / 0.6;
                        ws.Add((bone, aw * (1 - g))); ws.Add((bone - 1, aw * g));
                    }
                    else ws.Add((bone, aw));
                    wsum += aw;
                }
                if (ws.Count == 0 || wsum <= 0) continue;
                var merged = ws.GroupBy(x => x.B).Select(g => (B: g.Key, W: g.Sum(x => x.W) / wsum)).OrderByDescending(x => x.W).Take(4).ToList();
                double tot = merged.Sum(x => x.W);
                W[v] = merged.Select(x => (x.B, x.W / tot)).ToList();
                count++;
            }
          return count;
        }
        if (res.ExtrasMoved.Count > 0) note($"moving hair: {res.ExtrasMoved.Count} piece(s) worn on the hair move with it");
        res.Notes.Add($"moving hair: {chains.Count} chains ({string.Join(", ", chainBones.Select(c => c.Count - 1))} segments of {chainBones.Average(c => c.L):F2}), {moved} hair points on them");
        return res;
    }

    /// <summary>child 0x3D (body collision capsules) from a stock superstar with moving hair in the template's folder</summary>
    public static byte[]? StockCapsules(string templatePath)
    {
        string dir = Path.GetDirectoryName(templatePath) ?? ".";
        foreach (var name in new[] { "ch110.pac", "ch102.pac", "ch103.pac" }.Concat(Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "ch*.pac").Select(Path.GetFileName).OfType<string>() : []))
        {
            string p = Path.Combine(dir, name);
            if (!File.Exists(p)) continue;
            try
            {
                var arc = Epk8.Read(File.ReadAllBytes(p));
                foreach (var e in Pach.Read(arc.Groups[0].Entries[0].Data))
                    if (e.Id == 0x3d) return Bpe.Unpack(e.Data);
            }
            catch (Exception) { }
        }
        return null;
    }
}
