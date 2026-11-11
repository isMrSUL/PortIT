using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using PortIT.Engine.Formats;
using PortIT.Engine.Util;

namespace PortIT.Engine.Build;

/// <summary>Bone rest rotations of a superstar model lined up with SvR 2011's own (port of svr/prop_bones.py): every
/// body bone whose rest is further than 0.2 from all the stock ones takes the nearest stock rotation, parents first,
/// each node's translation recomputed so every joint stays where it was. Models already like the stock ones are left
/// byte for byte.</summary>
public static class PropBones
{
    public const double Tol = 0.2;
    const string FaceFrom = "atama";
    static readonly HashSet<string> Body = new(("root l_momo l_momo_x l_sune l_ashi l_tsumasaki r_momo r_momo_x r_sune r_ashi r_tsumasaki koshi mune " +
        "r_sakotsu r_ninoude r_ninoude_x r_kote r_kote_x r_kote_x2 r_te r_yubi10 r_yubi11 r_yubi12 r_yubi20 r_yubi21 r_yubi22 " +
        "r_yubi30 r_yubi31 r_yubi32 r_yubi40 r_yubi41 r_yubi42 r_yubi00 r_yubi01 r_yubi02 r_ninoude_m r_kote_m r_kata_m " +
        "l_sakotsu l_ninoude l_ninoude_x l_kote l_kote_x l_kote_x2 l_te l_yubi30 l_yubi31 l_yubi32 l_yubi10 l_yubi11 l_yubi12 " +
        "l_yubi40 l_yubi41 l_yubi42 l_yubi20 l_yubi21 l_yubi22 l_yubi00 l_yubi01 l_yubi02 l_kote_m l_ninoude_m l_kata_m " +
        "l_mune_dummy l_sakotsu_m r_mune_dummy r_sakotsu_m kubi kubi_x atama onaka").Split(' ', StringSplitOptions.RemoveEmptyEntries));

    public static string DefaultStock => Path.Combine(AppContext.BaseDirectory, "Engine", "svr", "prop_bones_stock.json");

    public sealed record Result(bool Fixed, List<(string Bone, double[] Old, double[] New)> Bones);

    static double[] Rotm(double[] r)
    {
        double x = r[0], y = r[1], z = r[2];
        double cx = Math.Cos(x), sx = Math.Sin(x), cy = Math.Cos(y), sy = Math.Sin(y), cz = Math.Cos(z), sz = Math.Sin(z);
        return [cy * cz, sx * sy * cz - cx * sz, cx * sy * cz + sx * sz,
                cy * sz, sx * sy * sz + cx * cz, cx * sy * sz - sx * cz,
                -sy, sx * cy, cx * cy];
    }

    sealed class N { public int O; public string Name = ""; public double[] T = [], R = []; public uint Parent; }

    static List<N> Nodes(byte[] d)
    {
        int n = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(0x20)), ptr = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(0x28));
        var o = new List<N>();
        for (int i = 0; i < n; i++)
        {
            int at = 8 + ptr + 80 * i;
            var nm = d.AsSpan(at, 16);
            int z = nm.IndexOf((byte)0);
            double F(int x) => BinaryPrimitives.ReadSingleBigEndian(d.AsSpan(x));
            o.Add(new N
            {
                O = at, Name = Encoding.Latin1.GetString(z < 0 ? nm : nm[..z]),
                T = [F(at + 16), F(at + 20), F(at + 24)], R = [F(at + 28), F(at + 32), F(at + 36)],
                Parent = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(at + 48)),
            });
        }
        return o;
    }

    static HashSet<string> Face(List<N> nodes)
    {
        var kids = new Dictionary<int, List<int>>();
        for (int i = 0; i < nodes.Count; i++)
            if (nodes[i].Parent < nodes.Count && nodes[i].Parent != i)
            {
                if (!kids.TryGetValue((int)nodes[i].Parent, out var l)) kids[(int)nodes[i].Parent] = l = [];
                l.Add(i);
            }
        var outs = new HashSet<string>();
        var stack = new Stack<int>(Enumerable.Range(0, nodes.Count).Where(i => nodes[i].Name == FaceFrom));
        while (stack.Count > 0)
        {
            int i = stack.Pop();
            foreach (var k in kids.GetValueOrDefault(i, [])) { outs.Add(nodes[k].Name); stack.Push(k); }
        }
        return outs;
    }

    public static Dictionary<string, List<double[]>> Reference(string? stockJson)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(stockJson ?? DefaultStock));
        return doc.RootElement.GetProperty("bones").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(v => v.EnumerateArray().Select(x => x.GetDouble()).ToArray()).ToList());
    }

    static double MaxAbs(double[] a, double[] b) { double m = double.NegativeInfinity; for (int i = 0; i < a.Length; i++) m = Math.Max(m, Math.Abs(a[i] - b[i])); return m; }

    public static (byte[]? New, List<(string, double[], double[])> Changed) FixJboy(byte[] d, Dictionary<string, List<double[]>> refs)
    {
        var nodes = Nodes(d);
        var face = Face(nodes);
        var changed = new List<(string, double[], double[])>();
        var newR = new List<double[]>();
        foreach (var n in nodes)
        {
            var opts = Body.Contains(n.Name) && !face.Contains(n.Name) ? refs.GetValueOrDefault(n.Name) : null;
            if (opts is { Count: > 0 })
            {
                var want = opts[0];
                foreach (var v in opts) if (MaxAbs(n.R, v) < MaxAbs(n.R, want)) want = v;     // (min: the first of equals)
                if (MaxAbs(n.R, want) > Tol) { newR.Add(want); changed.Add((n.Name, n.R, want)); continue; }
            }
            newR.Add(n.R);
        }
        if (changed.Count == 0) return (null, changed);
        int Nn = nodes.Count;
        (double[][] R, double[][] P) Worlds(List<double[]> rs, List<double[]> ts)
        {
            var R = new double[Nn][]; var P = new double[Nn][];
            void Go(int i)
            {
                if (R[i] != null) return;
                uint p = nodes[i].Parent;
                if (p >= Nn || p == i) { R[i] = Rotm(rs[i]); P[i] = (double[])ts[i].Clone(); return; }
                Go((int)p);
                R[i] = Blas.MatMul(R[p], Rotm(rs[i]), 3);
                var rv = Blas.MatVec3(R[p], 3, ts[i]);
                P[i] = [P[p][0] + rv[0], P[p][1] + rv[1], P[p][2] + rv[2]];
            }
            for (int i = 0; i < Nn; i++) Go(i);
            return (R, P);
        }
        var ts0 = nodes.Select(n => n.T).ToList();
        var (_, Pold) = Worlds(nodes.Select(n => n.R).ToList(), ts0);
        var (Rnew, _) = Worlds(newR, ts0);
        var outb = (byte[])d.Clone();
        for (int i = 0; i < Nn; i++)
        {
            var n = nodes[i];
            uint p = n.Parent;
            double[] t = p >= Nn || p == i ? n.T : Blas.MatTVec3(Rnew[p], 3, [Pold[i][0] - Pold[p][0], Pold[i][1] - Pold[p][1], Pold[i][2] - Pold[p][2]]);
            if (MaxAbs(t, n.T) > 1e-6 || !newR[i].SequenceEqual(n.R))
            {
                for (int c = 0; c < 3; c++)
                {
                    BinaryPrimitives.WriteSingleBigEndian(outb.AsSpan(n.O + 16 + 4 * c), (float)t[c]);
                    BinaryPrimitives.WriteSingleBigEndian(outb.AsSpan(n.O + 28 + 4 * c), (float)newR[i][c]);
                }
            }
        }
        return (outb, changed);
    }

    /// <summary>every attire's JBOYs lined up with the stock rests; out: the fixed copy (written only when changed)</summary>
    public static Result FixPac(string src, string? stockJson = null, string? outPath = null)
    {
        var stock = Reference(stockJson);
        var raw = File.ReadAllBytes(src);
        var arc = Epk8.Read(raw);
        var entries = new List<(byte[], byte[])>();
        var bones = new List<(string, double[], double[])>();
        bool changed = false;
        foreach (var e in arc.Groups[0].Entries)
        {
            if (e.Name.All(b => b == 0)) continue;
            var items = Pach.Read(Bpe.Unpack(e.Data));
            var nw = new List<Entry>();
            bool hit = false;
            foreach (var it in items)
            {
                var x = it.Data;
                var d = Bpe.Unpack(x);
                if (Jboy.IsJboy(d))
                {
                    var (nd, ch) = FixJboy(d, stock);
                    if (nd != null)
                    {
                        x = Bpe.Encode(nd, true); hit = true;
                        foreach (var c in ch) if (!bones.Any(b => b.Item1 == c.Item1)) bones.Add((c.Item1, c.Item2.Select(v => Math.Round(v, 2)).ToArray(), c.Item3.Select(v => Math.Round(v, 2)).ToArray()));
                    }
                }
                nw.Add(new Entry(it.Id, x));
            }
            var blob = e.Data;
            if (hit) { changed = true; blob = Pach.Write(nw); }
            entries.Add((e.Name, blob));
        }
        if (outPath != null && changed) File.WriteAllBytes(outPath, Epk8.Write(raw, entries));
        return new Result(changed, bones);
    }

    public static string? ReportLine(Result r)
    {
        if (!r.Fixed) return null;
        static string T(double[] v) => "(" + string.Join(", ", v.Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture))) + ")";
        return "bone rests put back to 2011's (the model's own ones distort it in motion): " +
               string.Join(", ", r.Bones.OrderBy(b => b.Bone, StringComparer.Ordinal).Select(b => $"{b.Bone} {T(b.Old)} -> {T(b.New)}"));
    }
}
