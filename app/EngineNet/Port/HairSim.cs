using System.Text;
using PortIT.Engine.Formats;

namespace PortIT.Engine.Port;

/// <summary>
/// The 3D view's hair test: the model's head and body moved by a test motion, and its kami_b hair chains swung by the
/// game's own hair solver (gBaH), as the port makers wrote it down from the game's code (HAIR_PHYSICS.md section 7:
/// root follow, RK4 with the accumulating trial state, bend, head-turn kick, length passes, capsule spheres, floor,
/// write-back by shortest arcs). Matrices here are 4 x 4 row-major for column vectors (SvrBuilder.Frame), so the doc's
/// row-vector products are turned round: World(child) = World(parent) * Local(child).
/// </summary>
public sealed class HairSim
{
    sealed class Model
    {
        public List<Jboy.Node> Nodes = [];
        public int[] Parent = [];
        public double[][] BindLocal = [], BindWorld = [], InvBind = [], Local = [], World = [];
        public Dictionary<string, int> Index = [];
    }

    sealed class HNode { public double[] P = new double[3], V = new double[3], F = new double[3], Prev = new double[3]; public bool Pinned; public int Bone; }
    sealed class Chain
    {
        public List<HNode> N = [];
        public int Parent;
        public double[] L = [];
        public double Radius, Visc, Grav, Spring, Damp, Bend, Move;
        public int A;
        public double[] PrevDir = new double[3];
    }

    readonly Dictionary<int, Model> models = [];
    readonly List<Chain> chains = [];
    readonly List<(int Bone, double[] P0, double R0, double[] P1, double R1)> caps = [];
    readonly int hairModel;
    public int Chains => chains.Count;
    public double FloorY;
    double time;
    /// <summary>the whole model turned by the viewer (the "you turn it" test): a 3 x 3 rotation about Pivot, or null</summary>
    public double[]? Turn;
    public double[] Pivot = new double[3];

    /// <summary>the turn the 3D view's camera gives the model (Viewer3D.Draw's yaw then pitch), as a model rotation</summary>
    public static double[] ViewTurn(double yaw, double pitch)
    {
        double cy = Math.Cos(yaw), sy = Math.Sin(yaw), cp = Math.Cos(pitch), sp = Math.Sin(pitch);
        return [cy, 0, -sy, -sy * sp, cp, -cy * sp, sy * cp, sp, cy * cp];
    }

    // ---------------------------------------------------------------- matrices
    static double[] Rot(double rx, double ry, double rz)
    {
        double cx = Math.Cos(rx), sx = Math.Sin(rx), cy = Math.Cos(ry), sy = Math.Sin(ry), cz = Math.Cos(rz), sz = Math.Sin(rz);
        // Rz Ry Rx (SvrBuilder.Rot)
        double[] Rx = [1, 0, 0, 0, cx, -sx, 0, sx, cx], Ry = [cy, 0, sy, 0, 1, 0, -sy, 0, cy], Rz = [cz, -sz, 0, sz, cz, 0, 0, 0, 1];
        return M3(M3(Rz, Ry), Rx);
    }
    static double[] M3(double[] a, double[] b)
    {
        var o = new double[9];
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) o[3 * i + j] = a[3 * i] * b[j] + a[3 * i + 1] * b[3 + j] + a[3 * i + 2] * b[6 + j];
        return o;
    }
    static double[] Frame(double[] R, double[] t) => [R[0], R[1], R[2], t[0], R[3], R[4], R[5], t[1], R[6], R[7], R[8], t[2], 0, 0, 0, 1];
    static double[] Mul(double[] a, double[] b)
    {
        var o = new double[16];
        for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) o[4 * i + j] = a[4 * i] * b[j] + a[4 * i + 1] * b[4 + j] + a[4 * i + 2] * b[8 + j] + a[4 * i + 3] * b[12 + j];
        return o;
    }
    static double[] RigidInv(double[] m)
    {
        // R^T, -R^T t
        double[] o = [m[0], m[4], m[8], 0, m[1], m[5], m[9], 0, m[2], m[6], m[10], 0, 0, 0, 0, 1];
        o[3] = -(o[0] * m[3] + o[1] * m[7] + o[2] * m[11]);
        o[7] = -(o[4] * m[3] + o[5] * m[7] + o[6] * m[11]);
        o[11] = -(o[8] * m[3] + o[9] * m[7] + o[10] * m[11]);
        return o;
    }
    static double[] Pt(double[] m, double[] p) => [m[0] * p[0] + m[1] * p[1] + m[2] * p[2] + m[3], m[4] * p[0] + m[5] * p[1] + m[6] * p[2] + m[7], m[8] * p[0] + m[9] * p[1] + m[10] * p[2] + m[11]];
    static double[] Dir(double[] m, double[] d) => [m[0] * d[0] + m[1] * d[1] + m[2] * d[2], m[4] * d[0] + m[5] * d[1] + m[6] * d[2], m[8] * d[0] + m[9] * d[1] + m[10] * d[2]];
    static double[] Org(double[] m) => [m[3], m[7], m[11]];
    static double[] Sub(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
    static double[] Add(double[] a, double[] b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];
    static double[] Sc(double[] a, double s) => [a[0] * s, a[1] * s, a[2] * s];
    static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    static double Len(double[] a) => Math.Sqrt(Dot(a, a));
    static double[] Cross(double[] a, double[] b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
    static double[] Unit(double[] a) { double l = Len(a); return l > 1e-12 ? Sc(a, 1 / l) : [1, 0, 0]; }
    /// <summary>the shortest-arc rotation (3 x 3) turning unit a onto unit b (826A8890: identity when nearly parallel)</summary>
    static double[] Arc(double[] a, double[] b)
    {
        var ax = Cross(a, b);
        double s = Len(ax), c = Math.Clamp(Dot(a, b), -1, 1);
        if (s < 1e-6 || Math.Abs(Math.Acos(c)) < 1e-4) return [1, 0, 0, 0, 1, 0, 0, 0, 1];
        var k = Sc(ax, 1 / s);
        double t = 1 - c;
        return [c + k[0] * k[0] * t, k[0] * k[1] * t - k[2] * s, k[0] * k[2] * t + k[1] * s,
                k[1] * k[0] * t + k[2] * s, c + k[1] * k[1] * t, k[1] * k[2] * t - k[0] * s,
                k[2] * k[0] * t - k[1] * s, k[2] * k[1] * t + k[0] * s, c + k[2] * k[2] * t];
    }

    static double[] NodeLocal(Jboy.Node n) => Frame(Rot(n.R[1], n.R[2], BitConverter.UInt32BitsToSingle(n.U40)), n.T);

    // ---------------------------------------------------------------- setup
    public HairSim(Dictionary<int, List<Jboy.Node>> modelNodes, string? settings, string? capsules, double floorY)
    {
        FloorY = floorY;
        foreach (var (id, nodes) in modelNodes)
        {
            var m = new Model { Nodes = nodes, Parent = nodes.Select(n => n.Parent >= nodes.Count ? -1 : n.Parent).ToArray() };
            int n = nodes.Count;
            m.BindLocal = nodes.Select(NodeLocal).ToArray();
            m.BindWorld = new double[n][];
            for (int i = 0; i < n; i++) m.BindWorld[i] = m.Parent[i] < 0 ? m.BindLocal[i] : Mul(m.BindWorld[m.Parent[i]], m.BindLocal[i]);
            m.InvBind = m.BindWorld.Select(RigidInv).ToArray();
            m.Local = m.BindLocal.Select(x => (double[])x.Clone()).ToArray();
            m.World = m.BindWorld.Select(x => (double[])x.Clone()).ToArray();
            for (int i = 0; i < n; i++) m.Index.TryAdd(nodes[i].Name, i);
            models[id] = m;
        }
        hairModel = models.Keys.FirstOrDefault(k => models[k].Index.Keys.Any(x => x.StartsWith("kami_b")), -1);
        if (hairModel < 0 || settings == null) return;
        var hm = models[hairModel];
        foreach (var line in settings.Split('\n'))
        {
            var tk = line.Split(';')[0].Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (tk.Length < 10 || !hm.Index.TryGetValue(tk[0], out int root)) continue;
            double F(int i) => double.Parse(tk[i], System.Globalization.CultureInfo.InvariantCulture);
            var c = new Chain { Radius = F(1), Visc = F(2), Grav = F(3), Spring = F(4), Damp = F(5), A = (int)F(6), Bend = F(8), Move = F(9), Parent = hm.Parent[root] };
            c.N.Add(new HNode { Bone = root, Pinned = true });
            for (int k = 0; hm.Index.TryGetValue($"{tk[0]}_{k:00}", out int b); k++) c.N.Add(new HNode { Bone = b });
            if (c.N.Count < 3) continue;
            double tip = hm.BindLocal[c.N[^1].Bone][3];        // (every rest length: the last node's local x)
            c.L = Enumerable.Repeat(tip, c.N.Count - 1).ToArray();
            chains.Add(c);
        }
        foreach (var line in (capsules ?? "").Split('\n'))
        {
            var tk = line.Split(';')[0].Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (tk.Length < 9 || !hm.Index.TryGetValue(tk[0], out int b)) continue;
            double F(int i) => double.Parse(tk[i], System.Globalization.CultureInfo.InvariantCulture);
            caps.Add((b, [F(1), F(2), F(3)], F(4), [F(5), F(6), F(7)], F(8)));
        }
    }

    /// <summary>the test motion: the body sways side to side and bobs, the head turns and nods; t in seconds</summary>
    void Animate(Model m, double t, double amount)
    {
        for (int i = 0; i < m.Nodes.Count; i++) m.Local[i] = (double[])m.BindLocal[i].Clone();
        if (amount > 0)
        {
            void Turn(string name, double ax, double ay, double az)
            {
                if (!m.Index.TryGetValue(name, out int i)) return;
                var R = Rot(ax, ay, az);
                var L = m.Local[i];
                double[] R0 = [L[0], L[1], L[2], L[4], L[5], L[6], L[8], L[9], L[10]];
                m.Local[i] = Frame(M3(R0, R), [L[3], L[7], L[11]]);
            }
            Turn("kubi", Math.Sin(t * 2 * Math.PI / 2.2) * 0.6 * amount, 0, Math.Sin(t * 2 * Math.PI / 1.3) * 0.25 * amount);
            Turn("atama", Math.Sin(t * 2 * Math.PI / 2.2 + 0.5) * 0.3 * amount, 0, 0);
        }
        // (the viewer's turn: the whole model rotated about the pivot, gravity still pulling down the screen)
        double[]? G = null;
        if (Turn != null)
        {
            var T = Turn; var c = Pivot;
            double[] tr = [c[0] - (T[0] * c[0] + T[1] * c[1] + T[2] * c[2]), c[1] - (T[3] * c[0] + T[4] * c[1] + T[5] * c[2]), c[2] - (T[6] * c[0] + T[7] * c[1] + T[8] * c[2])];
            G = Frame(T, tr);
        }
        for (int i = 0; i < m.Nodes.Count; i++)
            m.World[i] = m.Parent[i] < 0 ? (G != null ? Mul(G, m.Local[i]) : m.Local[i]) : Mul(m.World[m.Parent[i]], m.Local[i]);
    }

    /// <summary>the game's reset (8216A830): each chain straight down from its parent, then 30 settle steps as at a load</summary>
    public void Reset()
    {
        time = 0;
        foreach (var m in models.Values) Animate(m, 0, 0);
        if (hairModel < 0) return;
        var W = models[hairModel].World;
        foreach (var c in chains)
        {
            var p = Org(W[c.Parent]); double y = 0;
            for (int j = 0; j < c.N.Count; j++)
            {
                var n = c.N[j];
                n.P = Add(p, [0, y, 0]); n.Prev = (double[])n.P.Clone(); n.V = new double[3]; n.F = new double[3];
                n.Pinned = j == 0;
                if (j < c.L.Length) y += c.L[j];
            }
            c.PrevDir = new double[3];
        }
        for (int k = 0; k < 30; k++) HairFrame();
    }

    /// <summary>one 60 Hz frame: the test motion (amount 0 = standing still), then the hair</summary>
    public void Step(double amount)
    {
        time += 1 / 60.0;
        foreach (var m in models.Values) Animate(m, time, amount);
        HairFrame();
    }

    // ---------------------------------------------------------------- the solver (HAIR_PHYSICS.md section 7)
    double[][] Forces(Chain c, double[][] P, double[][] V)
    {
        int n = P.Length;
        var F = new double[n][];
        for (int j = 0; j < n; j++) { F[j] = Sc(V[j], -c.Visc); F[j][1] += c.Grav; }
        for (int j = 0; j < n - 1; j++)
        {
            var d = Sub(P[j], P[j + 1]); double l = Len(d);
            if (l < 1e-4) { d[1] = 1e-4; l = 1e-4; }
            double s = (c.Spring * (l - c.L[j]) + c.Damp * Dot(Sub(V[j], V[j + 1]), d) / l) / l;
            F[j] = Sub(F[j], Sc(d, s)); F[j + 1] = Add(F[j + 1], Sc(d, s));
        }
        return F;
    }

    void Rk4(Chain c)
    {
        int n = c.N.Count;
        var P0 = c.N.Select(x => x.P).ToArray(); var V0 = c.N.Select(x => x.V).ToArray(); var pin = c.N.Select(x => x.Pinned).ToArray();
        var F1 = Forces(c, P0, V0);
        var AV = V0.Select(v => (double[])v.Clone()).ToArray(); var AF = F1.Select(v => (double[])v.Clone()).ToArray();
        var TV = new double[n][]; var TP = new double[n][];
        for (int j = 0; j < n; j++) { TV[j] = Add(V0[j], Sc(F1[j], 0.5)); TP[j] = pin[j] ? P0[j] : Add(P0[j], Sc(V0[j], 0.5)); }
        var F2 = Forces(c, TP, TV); var V2 = TV.Select(v => (double[])v.Clone()).ToArray();
        for (int j = 0; j < n; j++) { AF[j] = Add(AF[j], Sc(F2[j], 2)); AV[j] = Add(AV[j], Sc(V2[j], 2)); TV[j] = Add(TV[j], Sc(F2[j], 0.5)); if (!pin[j]) TP[j] = Add(TP[j], Sc(V2[j], 0.5)); }
        var F3 = Forces(c, TP, TV); var V3 = TV.Select(v => (double[])v.Clone()).ToArray();
        for (int j = 0; j < n; j++) { AF[j] = Add(AF[j], Sc(F3[j], 2)); AV[j] = Add(AV[j], Sc(V3[j], 2)); TV[j] = Add(TV[j], F3[j]); if (!pin[j]) TP[j] = Add(TP[j], V3[j]); }
        var F4 = Forces(c, TP, TV); var V4 = TV.Select(v => (double[])v.Clone()).ToArray();
        for (int j = 0; j < n; j++) { AF[j] = Add(AF[j], F4[j]); AV[j] = Add(AV[j], V4[j]); }
        for (int j = 0; j < n; j++) { c.N[j].V = Add(c.N[j].V, Sc(AF[j], 1 / 6.0)); if (!pin[j]) c.N[j].P = Add(c.N[j].P, Sc(AV[j], 1 / 6.0)); }
    }

    void HairFrame()
    {
        if (hairModel < 0 || chains.Count == 0) return;
        var M = models[hairModel];
        var W = M.World;
        // capsule spheres
        var S = new List<(double[] C, double R)>();
        foreach (var (b, p0, r0, p1, r1) in caps)
        {
            var P0 = Pt(W[b], p0); var P1 = Pt(W[b], p1);
            var Mm = Sc(Add(P0, P1), 0.5); double RM = (r0 + r1) / 2;
            var S3 = Sc(Add(P1, Mm), 0.5); var S4 = Sc(Add(P0, S3), 0.5);
            S.AddRange([(P0, r0), (P1, r1), (Mm, RM), (S3, (RM + r1) / 2), (S4, (RM + r0) / 2)]);
        }
        // A: root follow, pin, RK4
        foreach (var c in chains)
        {
            var n0 = c.N[0]; var Wr = W[n0.Bone];
            n0.P = Org(Wr);
            var d = Sub(n0.P, n0.Prev);
            if (Len(d) <= 1.0)
            {
                var loc = Unit(Pt(RigidInv(Wr), n0.Prev));
                double f = c.Move;
                if (loc[1] < 0) f = (loc[1] + 1) * c.Move;
                f = Math.Max(f, 0.5);
                d = Sc(d, f);
            }
            for (int j = 1; j < c.N.Count; j++) { c.N[j].P = Add(c.N[j].P, d); c.N[j].Prev = Add(c.N[j].Prev, d); }
            c.N[1].P = Org(W[c.N[1].Bone]); c.N[1].Pinned = true;
            Rk4(c);
        }
        // B: bend, head-turn kick, RK4
        foreach (var c in chains)
        {
            var v = Dir(W[c.Parent], [0, 0, -1]);
            double turn = Math.Min(Len(Sub(v, c.PrevDir)) * 0.1, 0.02); c.PrevDir = v;
            var N = c.N;
            for (int j = 2; j < N.Count; j++)
            {
                var e = Sub(Sub(Sc(N[j - 1].P, 2), N[j - 2].P), N[j].P);
                if (Len(e) / c.L[0] > 0.1) N[j].V = Add(N[j].V, Sc(e, c.Bend * (j >= 4 ? 0.5 : j == 3 ? 0.75 : 1.0)));
            }
            if (turn > 0.001)
            {
                var z = Sc(Dir(W[N[0].Bone], [0, 0, -1]), turn);
                for (int j = 2; j < N.Count; j++) N[j].V = Add(N[j].V, z);
            }
        }
        foreach (var c in chains) Rk4(c);
        // C: lengths, spheres, floor
        foreach (var c in chains)
        {
            var N = c.N; int n = N.Count;
            for (int it = 0; it < 4; it++)
            {
                var pair = new (double[] A, double[] B)[n];
                for (int s = 1; s < n - 1; s++)
                {
                    var a = N[s].P; var b = N[s + 1].P;
                    if (Len(Sub(a, b)) > c.L[s]) { var m = Sc(Add(a, b), 0.5); var u = Unit(Sub(b, m)); pair[s] = (Sub(m, Sc(u, c.L[s] / 2)), Add(m, Sc(u, c.L[s] / 2))); }
                    else pair[s] = (a, b);
                }
                for (int j = 2; j < n - 1; j++) N[j].P = Sc(Add(pair[j - 1].B, pair[j].A), 0.5);
                if (n > 2) N[n - 1].P = pair[n - 2].B;
            }
            for (int s = 1; s < n - 1; s++)
                if (Len(Sub(N[s + 1].P, N[s].P)) > c.L[s]) N[s + 1].P = Add(N[s].P, Sc(Unit(Sub(N[s + 1].P, N[s].P)), c.L[s]));
        }
        foreach (var c in chains)
        {
            foreach (var nd in c.N.Skip(2))
                foreach (var (ctr, r) in S)
                {
                    double R = r + c.Radius; var dd = Sub(nd.P, ctr); double l = Len(dd);
                    if (l < R && l > 1e-9) { nd.P = Add(ctr, Sc(dd, R / l)); nd.V = new double[3]; }
                }
            double lim = FloorY - c.Radius;
            foreach (var nd in c.N.Skip(2)) if (nd.P[1] > lim) nd.P[1] = lim;
        }
        foreach (var c in chains) foreach (var nd in c.N) nd.Prev = (double[])nd.P.Clone();
        // D: write-back (A == 0)
        foreach (var c in chains)
        {
            var N = c.N;
            var Wc = W[N[0].Bone];
            double ln = M.Local[N[1].Bone][3];
            for (int j = 1; j < N.Count - 1; j++)
            {
                var t = Pt(RigidInv(Wc), N[j + 1].P); t[0] -= ln;
                double newLen = Len(Sub(N[j + 1].P, N[j].P));
                var Lj = M.Local[N[j].Bone];
                var R = Arc([1, 0, 0], Unit(t));
                double[] tr = j >= 2 ? [ln, 0, 0] : [Lj[3], Lj[7], Lj[11]];
                M.Local[N[j].Bone] = Frame(R, tr);
                Wc = Mul(Wc, M.Local[N[j].Bone]);
                W[N[j].Bone] = Wc;
                ln = newLen;
            }
            // (the bones below the written ones follow their parents)
            for (int j = 1; j < N.Count - 1; j++) N[j].P = Org(W[N[j].Bone]);
            var last = N[^1].Bone;
            W[last] = Mul(W[M.Parent[last]], M.Local[last]);
        }
    }

    /// <summary>the skin matrix of a model's bone now (world * inverse bind)</summary>
    public double[]? Skin(int model, int bone) => models.TryGetValue(model, out var m) && bone >= 0 && bone < m.World.Length ? Mul(m.World[bone], m.InvBind[bone]) : null;
    public bool Has(int model) => models.ContainsKey(model);
    public int Bones(int model) => models.TryGetValue(model, out var m) ? m.World.Length : 0;
    public (double[] P, double[] Tip)[] ChainPoints() => chains.Select(c => (Org(models[hairModel].World[c.N[0].Bone]), c.N[^1].P)).ToArray();
}
