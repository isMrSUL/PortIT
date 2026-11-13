using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace PortIT.Engine.Readers;

/// <summary>
/// A binary FBX file (7.x) read into its node tree: each node's name, its properties (numbers, strings, arrays - the
/// zlib-packed ones unpacked) and its children. Fbx.Scene on top of it gathers the objects and their links.
/// </summary>
public sealed class Fbx
{
    public sealed class Node
    {
        public string Name = "";
        public List<object> Props = [];
        public List<Node> Kids = [];
        public Node? this[string name] => Kids.FirstOrDefault(k => k.Name == name);
        public IEnumerable<Node> All(string name) => Kids.Where(k => k.Name == name);
        public long L(int i) => Convert.ToInt64(Props[i]);
        public string S(int i) => Props[i] as string ?? "";
        public double[] D(int i = 0) => Props[i] switch
        {
            double[] d => d, float[] f => f.Select(x => (double)x).ToArray(), int[] a => a.Select(x => (double)x).ToArray(),
            long[] l => l.Select(x => (double)x).ToArray(), _ => [],
        };
        public int[] I(int i = 0) => Props[i] switch
        {
            int[] a => a, long[] l => l.Select(x => (int)x).ToArray(), double[] d => d.Select(x => (int)x).ToArray(), _ => [],
        };
    }

    public readonly Node Root = new();
    public readonly int Version;

    public Fbx(string path)
    {
        var d = File.ReadAllBytes(path);
        if (d.Length < 27 || Encoding.ASCII.GetString(d, 0, 18) != "Kaydara FBX Binary")
            throw new InvalidDataException("not a binary FBX file (an ASCII FBX: open it in Blender and export it again as binary FBX)");
        Version = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(23));
        long at = 27;
        while (true)
        {
            var n = ReadNode(d, ref at);
            if (n == null) break;
            Root.Kids.Add(n);
        }
    }

    bool Wide => Version >= 7500;

    Node? ReadNode(byte[] d, ref long at)
    {
        long end, np, plen;
        if (Wide)
        {
            if (at + 25 > d.Length) return null;
            end = (long)BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan((int)at)); np = (long)BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan((int)at + 8));
            plen = (long)BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan((int)at + 16)); at += 24;
        }
        else
        {
            if (at + 13 > d.Length) return null;
            end = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)at)); np = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)at + 4));
            plen = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)at + 8)); at += 12;
        }
        int nl = d[at++];
        if (end == 0) return null;
        var node = new Node { Name = Encoding.ASCII.GetString(d, (int)at, nl) };
        at += nl;
        long pend = at + plen;
        for (long i = 0; i < np; i++) node.Props.Add(ReadProp(d, ref at));
        at = pend;
        while (at < end)
        {
            var k = ReadNode(d, ref at);
            if (k == null) break;
            node.Kids.Add(k);
        }
        at = end;
        return node;
    }

    static object ReadProp(byte[] d, ref long at)
    {
        char t = (char)d[at++];
        var s = d.AsSpan((int)at);
        switch (t)
        {
            case 'Y': at += 2; return (long)BinaryPrimitives.ReadInt16LittleEndian(s);
            case 'C': at += 1; return (long)s[0];
            case 'I': at += 4; return (long)BinaryPrimitives.ReadInt32LittleEndian(s);
            case 'F': at += 4; return (double)BinaryPrimitives.ReadSingleLittleEndian(s);
            case 'D': at += 8; return BinaryPrimitives.ReadDoubleLittleEndian(s);
            case 'L': at += 8; return BinaryPrimitives.ReadInt64LittleEndian(s);
            case 'S':
            case 'R':
            {
                int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(s);
                at += 4 + n;
                if (t == 'R') return s.Slice(4, n).ToArray();
                return Encoding.UTF8.GetString(s.Slice(4, n));
            }
            case 'f': case 'd': case 'l': case 'i': case 'b':
            {
                int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(s), enc = (int)BinaryPrimitives.ReadUInt32LittleEndian(s[4..]),
                    clen = (int)BinaryPrimitives.ReadUInt32LittleEndian(s[8..]);
                at += 12 + clen;
                byte[] raw = s.Slice(12, clen).ToArray();
                if (enc == 1)
                {
                    using var z = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
                    using var o = new MemoryStream();
                    z.CopyTo(o);
                    raw = o.ToArray();
                }
                switch (t)
                {
                    case 'f': { var a = new float[count]; for (int i = 0; i < count; i++) a[i] = BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(4 * i)); return a; }
                    case 'd': { var a = new double[count]; for (int i = 0; i < count; i++) a[i] = BinaryPrimitives.ReadDoubleLittleEndian(raw.AsSpan(8 * i)); return a; }
                    case 'l': { var a = new long[count]; for (int i = 0; i < count; i++) a[i] = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(8 * i)); return a; }
                    case 'i': { var a = new int[count]; for (int i = 0; i < count; i++) a[i] = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4 * i)); return a; }
                    default: { var a = new int[count]; for (int i = 0; i < count; i++) a[i] = raw[i]; return a; }
                }
            }
            default: throw new InvalidDataException($"FBX: unknown property type '{t}'");
        }
    }

    // ---------------------------------------------------------------- the scene
    public sealed class Obj
    {
        public long Id;
        public string Kind = "", Name = "", Sub = "";
        public Node Node = new();
        public List<(Obj O, string Prop)> In = [], Out = [];   // In: children linked to this; Out: what this links to
        public Dictionary<string, Node> P70 = [];
        public double[] Vec(string p, double[] def)
        {
            if (!P70.TryGetValue(p, out var n)) return def;
            return [Convert.ToDouble(n.Props[4]), Convert.ToDouble(n.Props[5]), Convert.ToDouble(n.Props[6])];
        }
        public double Num(string p, double def) => P70.TryGetValue(p, out var n) ? Convert.ToDouble(n.Props[4]) : def;
        public long Int(string p, long def) => P70.TryGetValue(p, out var n) ? Convert.ToInt64(n.Props[4]) : def;
    }

    public Dictionary<long, Obj> Objects = [];
    public double UnitCm = 1;
    public int UpAxis = 1, UpSign = 1;

    public void Gather()
    {
        foreach (var n in Root["Objects"]?.Kids ?? [])
        {
            var o = new Obj { Id = n.L(0), Kind = n.Name, Node = n };
            string full = n.S(1);
            int sep = full.IndexOf("\0\u0001", StringComparison.Ordinal);
            o.Name = sep >= 0 ? full[..sep] : full;
            o.Sub = n.Props.Count > 2 ? n.S(2) : "";
            if (n["Properties70"] is Node p) foreach (var k in p.All("P")) o.P70[k.S(0)] = k;
            Objects[o.Id] = o;
        }
        foreach (var c in Root["Connections"]?.All("C") ?? [])
        {
            string ty = c.S(0);
            long a = c.L(1), b = c.L(2);
            string prop = ty == "OP" && c.Props.Count > 3 ? c.S(3) : "";
            if (!Objects.TryGetValue(a, out var oa)) continue;
            if (!Objects.TryGetValue(b, out var ob)) continue;
            oa.Out.Add((ob, prop));
            ob.In.Add((oa, prop));
        }
        var gs = Root["GlobalSettings"]?["Properties70"];
        if (gs != null)
            foreach (var k in gs.All("P"))
            {
                if (k.S(0) == "UnitScaleFactor") UnitCm = Convert.ToDouble(k.Props[4]);
                if (k.S(0) == "UpAxis") UpAxis = (int)Convert.ToInt64(k.Props[4]);
                if (k.S(0) == "UpAxisSign") UpSign = (int)Convert.ToInt64(k.Props[4]);
            }
    }

    // ---------------------------------------------------------------- matrices (column-major 4x4, as FBX stores them)
    public static double[] Mul(double[] a, double[] b)
    {
        var o = new double[16];
        for (int c = 0; c < 4; c++)
            for (int r = 0; r < 4; r++)
            {
                double s = 0;
                for (int k = 0; k < 4; k++) s += a[k * 4 + r] * b[c * 4 + k];
                o[c * 4 + r] = s;
            }
        return o;
    }

    public static double[] Point(double[] m, double x, double y, double z) =>
        [m[0] * x + m[4] * y + m[8] * z + m[12], m[1] * x + m[5] * y + m[9] * z + m[13], m[2] * x + m[6] * y + m[10] * z + m[14]];

    public static double[] Dir(double[] m, double x, double y, double z) =>
        [m[0] * x + m[4] * y + m[8] * z, m[1] * x + m[5] * y + m[9] * z, m[2] * x + m[6] * y + m[10] * z];

    public static double[] Translate(double[] t) => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, t[0], t[1], t[2], 1];
    public static double[] Scale(double[] s) => [s[0], 0, 0, 0, 0, s[1], 0, 0, 0, 0, s[2], 0, 0, 0, 0, 1];

    /// <summary>Euler XYZ in degrees (FBX's default order: R = Rz Ry Rx)</summary>
    public static double[] Euler(double[] e)
    {
        double rx = e[0] * Math.PI / 180, ry = e[1] * Math.PI / 180, rz = e[2] * Math.PI / 180;
        double[] X = [1, 0, 0, 0, 0, Math.Cos(rx), Math.Sin(rx), 0, 0, -Math.Sin(rx), Math.Cos(rx), 0, 0, 0, 0, 1];
        double[] Y = [Math.Cos(ry), 0, -Math.Sin(ry), 0, 0, 1, 0, 0, Math.Sin(ry), 0, Math.Cos(ry), 0, 0, 0, 0, 1];
        double[] Z = [Math.Cos(rz), Math.Sin(rz), 0, 0, -Math.Sin(rz), Math.Cos(rz), 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
        return Mul(Z, Mul(Y, X));
    }

    /// <summary>a Model's local matrix from its Lcl / Pre / Post properties (pivots left out)</summary>
    public static double[] Local(Obj o)
    {
        var T = o.Vec("Lcl Translation", [0, 0, 0]);
        var R = o.Vec("Lcl Rotation", [0, 0, 0]);
        var S = o.Vec("Lcl Scaling", [1, 1, 1]);
        var Pre = o.Vec("PreRotation", [0, 0, 0]);
        var Post = o.Vec("PostRotation", [0, 0, 0]);
        var m = Mul(Translate(T), Mul(Euler(Pre), Euler(R)));
        if (Post.Any(x => x != 0))
        {
            var p = Euler(Post);
            double[] pt = [p[0], p[4], p[8], 0, p[1], p[5], p[9], 0, p[2], p[6], p[10], 0, 0, 0, 0, 1];
            m = Mul(m, pt);
        }
        return Mul(m, Scale(S));
    }

    readonly Dictionary<long, double[]> world = [];
    public Obj? ParentModel(Obj o) => o.Out.Select(x => x.O).FirstOrDefault(x => x.Kind == "Model");

    public double[] World(Obj o)
    {
        if (world.TryGetValue(o.Id, out var w)) return w;
        var p = ParentModel(o);
        w = p != null ? Mul(World(p), Local(o)) : Local(o);
        world[o.Id] = w;
        return w;
    }
}
