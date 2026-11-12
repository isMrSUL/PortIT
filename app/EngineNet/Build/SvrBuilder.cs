using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PortIT.Engine.Formats;
using PortIT.Engine.Imaging;
using PortIT.Engine.Util;

namespace PortIT.Engine.Build;

/// <summary>
/// model.json -&gt; SvR 2011 ch.pac: port of svr/svr11_build.py as PortIT runs it through k26/rock_build.py (one sheet
/// cell - every mesh already points into one drawn sheet; the colour alpha as drawn), with model_budget.py and
/// prop_bones.py. The Mod Maker's other routes (hair chains, entrance gear, packed per-mesh sheets) are not used by
/// PortIT's ports and are refused. Arithmetic in NumPy's order (Util/Blas) so the nodes come out the same.
/// </summary>
public sealed class SvrBuilder
{
    public const int MaxPal = 20, MaxVerts = 65535;
    public (int W, int H) ColorSize = (2048, 1024), NormalSize = (1024, 1024);
    public static readonly (int W, int H) MkSize = (512, 256);
    public int PageLimit = Budget.Limit;
    static readonly Dictionary<string, string> SkinSlots = new() { ["texDiffuse"] = "color", ["texNormal"] = "normal", ["texOcclusion"] = "mk", ["texTilingMap"] = "noise" };
    static readonly Dictionary<string, string> EyeSlots = new() { ["texDiffuse"] = "color", ["texCubeRefction"] = "cubemap", ["texRefctionReg"] = "color", ["texOcclusion"] = "mk" };
    static readonly Dictionary<string, string> LashSlots = new() { ["texDiffuse"] = "color" };
    static readonly string[] TexBody = ["color", "normal", "mk", "noise", "cubemap"];

    public sealed class BuildError(string m) : Exception(m);

    readonly JsonObject spec;
    readonly string bas;
    public readonly List<string> Warnings = [];
    readonly Template conv;
    readonly string templatePath;
    List<Jboy.Node> nodes;
    Dictionary<string, int> index;
    bool faced;
    string? lashPng;

    public SvrBuilder(JsonObject spec, string baseDir)
    {
        this.spec = spec;
        bas = baseDir;
        templatePath = Path((string)spec["template"]!);
        conv = new Template(templatePath);
        if (spec["portrait"] != null) throw new BuildError("a portrait is not supported by PortIT's builder");
        nodes = PosedNodes(spec["joints"] as JsonObject);
        index = Index(nodes);
    }

    static Dictionary<string, int> Index(List<Jboy.Node> ns)
    {
        var d = new Dictionary<string, int>();
        for (int i = 0; i < ns.Count; i++) d[ns[i].Name] = i;
        return d;
    }

    string Path(string p) => System.IO.Path.IsPathRooted(p) ? p : System.IO.Path.Combine(bas, p);

    // ---------------------------------------------------------------- the template (w13_char.Conv, the parts used)
    public sealed class Template
    {
        public readonly Dictionary<int, byte[]> T = [];
        public readonly Jboy Body, Lash;
        public readonly Jboy? Hair;
        public readonly List<Jboy.Node> TNodes;
        public readonly List<(double[] T, double[] R, int Parent)> TFrame = [];
        public readonly Dictionary<string, int> TIndex;
        public readonly Jboy.Mesh PSkin, PEye, PHair, PLash;
        public readonly List<(string Name, byte[] Data)> TTex;

        public Template(string path)
        {
            var arc = Epk8.Read(File.ReadAllBytes(path));
            foreach (var e in Pach.Read(Bpe.Unpack(arc.Groups[0].Entries[0].Data))) T[(int)e.Id] = Bpe.Unpack(e.Data);
            Body = Jboy.Read(T[0]);
            Hair = T.ContainsKey(0x2710) ? Jboy.Read(T[0x2710]) : null;
            Lash = Jboy.Read(T[0x4e20]);
            TNodes = Body.Nodes;
            var d = T[0];
            int nn = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(0x20)), np = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(0x28));
            for (int i = 0; i < nn; i++)
            {
                int o = 8 + np + 80 * i;
                double F(int x) => BinaryPrimitives.ReadSingleBigEndian(d.AsSpan(x));
                TFrame.Add(([F(o + 16), F(o + 20), F(o + 24)], [F(o + 32), F(o + 36), F(o + 40)], BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(o + 48))));
            }
            TIndex = Index(TNodes);
            PSkin = SkinTemplate(Body.Meshes);
            PEye = Body.Meshes.First(s => s.Shader == "yReflect");
            PHair = Hair != null ? Hair.Meshes[^1] : PSkin;
            PLash = Lash.Meshes[0];
            TTex = TexBundleRead(T[0xa]);
        }

        /// <summary>w13_char.skin_template: the parameter set most yBumpMapSS meshes share, no wrinkle / muscle /
        /// bruise part, the largest - a copy with those part numbers 0</summary>
        static Jboy.Mesh SkinTemplate(List<Jboy.Mesh> meshes)
        {
            var skin = meshes.Where(s => s.Shader == "yBumpMapSS").ToList();
            string Sig(Jboy.Mesh s) => string.Join("|", s.Params.Where(p => p.Type != 0x0f && !(p.Name.StartsWith("g_f2Wrinkle") || p.Name.StartsWith("g_f2Muscle") || p.Name.StartsWith("g_i")))
                                                                .Select(p => p.Name + "=" + Convert.ToHexString(p.Value)));
            int Parts(Jboy.Mesh s) => s.Params.Count(p => p.Name is "g_iWrinkleParts" or "g_iMuscleParts" or "g_iBruiseParts" && BinaryPrimitives.ReadInt32BigEndian(p.Value) != 0);
            // (Counter.most_common(1): the highest count, the first seen among equals)
            var counts = new List<(string Sig, int N)>();
            foreach (var s in skin)
            {
                var g = Sig(s);
                int at = counts.FindIndex(c => c.Sig == g);
                if (at < 0) counts.Add((g, 1)); else counts[at] = (g, counts[at].N + 1);
            }
            var common = counts.Aggregate((a, b) => b.N > a.N ? b : a).Sig;
            var pick = skin.Where(s => Sig(s) == common).ToList();
            var best = pick.Aggregate((a, b) => (Parts(b), -b.Verts.Count).CompareTo((Parts(a), -a.Verts.Count)) < 0 ? b : a);
            var o = new Jboy.Mesh
            {
                Raw = best.Raw, StripCount = best.StripCount, Palette = best.Palette, U5c = best.U5c, U74 = best.U74, Shader = best.Shader,
                U88 = best.U88, Vfmt = best.Vfmt, Ua0 = best.Ua0, Vc2 = best.Vc2, Sphere = best.Sphere, Verts = best.Verts,
                Weights = best.Weights, Uvs = best.Uvs, Strips = best.Strips,
                Params = best.Params.Select(p => new Jboy.Param
                {
                    Name = p.Name, Type = p.Type,
                    Value = p.Name is "g_iWrinkleParts" or "g_iMuscleParts" or "g_iBruiseParts" ? [0, 0, 0, 0, .. p.Value.AsSpan(4)] : p.Value,
                }).ToList(),
            };
            return o;
        }
    }

    public static List<(string, byte[])> TexBundleRead(byte[] b)
    {
        int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(b);
        var o = new List<(string, byte[])>();
        for (int i = 0; i < n; i++)
        {
            int r = 16 + 32 * i;
            int sz = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(r + 20)), off = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(r + 24));
            var nm = b.AsSpan(r, 16);
            int z = nm.IndexOf((byte)0);
            o.Add((Encoding.Latin1.GetString(z < 0 ? nm : nm[..z]), b.AsSpan(off, sz).ToArray()));
        }
        return o;
    }

    public static byte[] TexBundleWrite(IReadOnlyList<(string Name, byte[] Data)> texs, byte[]? head)
    {
        int n = texs.Count, off = (16 + 32 * n + 15) & ~15;
        var recs = new MemoryStream();
        var blobs = new MemoryStream();
        Span<byte> x = stackalloc byte[12];
        foreach (var (name, dd) in texs)
        {
            var nm = new byte[16];
            var nb = Encoding.Latin1.GetBytes(name);
            nb.AsSpan(0, Math.Min(16, nb.Length)).CopyTo(nm);
            recs.Write(nm); recs.Write("dds\0"u8);
            BinaryPrimitives.WriteUInt32LittleEndian(x, (uint)dd.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(x[4..], (uint)(off + blobs.Length));
            recs.Write(x);
            blobs.Write(dd);
            while (blobs.Length % 16 != 0) blobs.WriteByte(0);
        }
        var o = new byte[off + blobs.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(o, (uint)n);
        if (head != null) head.AsSpan(4, 12).CopyTo(o.AsSpan(4));
        recs.ToArray().CopyTo(o, 16);
        blobs.ToArray().CopyTo(o, off);
        return o;
    }

    public static byte[] PackKids(SortedDictionary<int, byte[]> kids) =>
        Pach.Write(kids.Select(kv => new Entry((uint)kv.Key, Bpe.Encode(kv.Value, true))).ToList());

    /// <summary>w13_char.rot: Rz @ Ry @ Rx (3 x 3, row-major), NumPy's product order</summary>
    public static double[] Rot(double[] r)
    {
        double cx = Math.Cos(r[0]), sx = Math.Sin(r[0]), cy = Math.Cos(r[1]), sy = Math.Sin(r[1]), cz = Math.Cos(r[2]), sz = Math.Sin(r[2]);
        double[] Rx = [1, 0, 0, 0, cx, -sx, 0, sx, cx], Ry = [cy, 0, sy, 0, 1, 0, -sy, 0, cy], Rz = [cz, -sz, 0, sz, cz, 0, 0, 0, 1];
        return Blas.MatMul(Blas.MatMul(Rz, Ry, 3), Rx, 3);
    }

    static double[] Frame(double[] R3, double[] t)
    {
        var M = new double[16];
        for (int i = 0; i < 3; i++) { for (int j = 0; j < 3; j++) M[4 * i + j] = R3[3 * i + j]; M[4 * i + 3] = t[i]; }
        M[15] = 1;
        return M;
    }

    // ---------------------------------------------------------------- nodes
    List<Jboy.Node> PosedNodes(JsonObject? joints)
    {
        joints ??= [];
        var unknown = joints.Select(kv => kv.Key).Where(j => !conv.TIndex.ContainsKey(j)).ToList();
        if (unknown.Count > 0) Warnings.Add("joints not in the template, ignored: " + string.Join(", ", unknown.Take(10)));
        var ns = conv.TNodes.Select(n => n.Clone()).ToList();
        var Wn = new List<double[]>();
        for (int j = 0; j < ns.Count; j++)
        {
            var fr = conv.TFrame[j];
            int p = j > 0 ? fr.Parent : -1;
            if (joints[ns[j].Name] is JsonArray ja && p >= 0)
            {
                var Pp = new[] { Wn[p][3], Wn[p][7], Wn[p][11] };
                double[] jp = [ja[0]!.GetValue<double>() - Pp[0], ja[1]!.GetValue<double>() - Pp[1], ja[2]!.GetValue<double>() - Pp[2]];
                ns[j].T = Blas.MatTVec3(Wn[p], 4, jp);
            }
            var M = Frame(Rot(fr.R), ns[j].T);
            Wn.Add(p < 0 ? M : Blas.MatMul(Wn[p], M, 4));
        }
        return ns;
    }

    (Jboy.Mesh Tmpl, Dictionary<string, string> Slots) TemplateMesh(string kind) => kind switch
    {
        "skin" => (conv.PSkin, SkinSlots), "eye" => (conv.PEye, EyeSlots), "hair" => (conv.PHair, SkinSlots), "lash" => (conv.PLash, LashSlots),
        _ => throw new BuildError("kind " + kind),
    };

    // ---------------------------------------------------------------- one mesh of the neutral model
    sealed class NMesh
    {
        public string Name = "", Kind = "skin";
        public double[][] Pos = [], Nrm = [], Uv = [];
        public List<string> Bones = [];
        public List<List<(double B, double W)>> Weights = [];
        public int[][] Tris = [];
        public JsonObject? Material, Textures;
        public List<List<(int B, double W)>> W = [];
    }

    static NMesh ReadMesh(JsonObject m)
    {
        double[][] Arr(string key) => (m[key] as JsonArray ?? []).Select(v => v!.AsArray().Select(x => x!.GetValue<double>()).ToArray()).ToArray();
        var o = new NMesh
        {
            Name = (string?)m["name"] ?? "", Kind = (string?)m["kind"] ?? "skin",
            Pos = Arr("positions"), Nrm = Arr("normals"), Uv = Arr("uvs"),
            Bones = (m["bones"] as JsonArray ?? []).Select(x => (string)x!).ToList(),
            Weights = (m["weights"] as JsonArray ?? []).Select(v => v!.AsArray().Select(e => (e![0]!.GetValue<double>(), e[1]!.GetValue<double>())).ToList()).ToList(),
            Tris = (m["triangles"] as JsonArray ?? []).Select(t => t!.AsArray().Select(x => x!.GetValue<int>()).ToArray()).ToArray(),
            Material = m["material"] as JsonObject, Textures = m["textures"] as JsonObject,
        };
        return o;
    }

    /// <summary>bone names -&gt; node indices; top 4 per vertex, renormalised (Builder.weights)</summary>
    List<List<(int, double)>> Weights(NMesh m)
    {
        var bidx = new List<int>();
        foreach (var b in m.Bones)
        {
            if (!index.ContainsKey(b)) Warnings.Add($"{m.Name}: bone {b} is not in the template, its weights go to root");
            bidx.Add(index.TryGetValue(b, out int i) ? i : index.GetValueOrDefault("root", 0));
        }
        var outl = new List<List<(int, double)>>();
        foreach (var vw in m.Weights)
        {
            var acc = new List<(int B, double W)>();
            foreach (var (bi, w) in vw)
            {
                if (!(w > 0)) continue;
                int k = bidx[(int)bi];
                int at = acc.FindIndex(x => x.B == k);
                if (at < 0) acc.Add((k, w)); else acc[at] = (k, acc[at].W + w);
            }
            var top = acc.OrderBy(x => -x.W).Take(4).ToList();          // (stable: equal weights keep their order)
            double tot = 0;
            foreach (var (_, w) in top) tot += w;
            outl.Add(tot > 0 ? top.Select(x => (x.B, x.W / tot)).ToList() : [(index.GetValueOrDefault("root", 0), 1.0)]);
        }
        return outl;
    }

    /// <summary>is this mesh drawn with the game's skin shader (Builder.skin_shader)</summary>
    bool SkinShader(NMesh m)
    {
        if (m.Kind is not ("skin" or "hair")) return false;
        var tmpl = TemplateMesh(m.Kind).Tmpl;
        if (tmpl.Shader != "yBumpMapSS") return false;
        uint fl = BinaryPrimitives.ReadUInt32BigEndian(tmpl.Raw.AsSpan(0x88));
        fl = Flags(fl, m.Material);
        var p = tmpl.Params.GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.Last().Value);
        bool tiling = p.TryGetValue("texTilingMap", out var tv) && BinaryPrimitives.ReadInt32BigEndian(tv) >= 0;
        bool spa = p.TryGetValue("g_fSweatSPA", out var sv) && BinaryPrimitives.ReadSingleBigEndian(sv) != 0;
        return (fl & 0x2) != 0 && (fl & 0x110) == 0 && tiling && spa;
    }

    static uint Flags(uint fl, JsonObject? mat)
    {
        if (mat?["double_sided"] is JsonNode ds) fl = ds.GetValue<bool>() ? fl & ~0x2u : fl | 0x2u;
        if (mat?["cutout"] is JsonNode co) fl = co.GetValue<bool>() ? fl | 0x110u : fl & ~0x110u;
        return fl;
    }

    static bool Truthy(JsonNode? n) => n switch
    {
        null => false,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<double>(out var d) => d != 0,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0,
        _ => true,
    };

    Jboy.Mesh JMesh(NMesh m, string kind, List<int> triIdx, List<string> textures)
    {
        var (tmpl, slots) = TemplateMesh(kind);
        var tris = triIdx.Select(i => m.Tris[i]).ToList();
        var used = tris.SelectMany(t => t).Distinct().OrderBy(v => v).ToList();
        var remap = new Dictionary<int, int>();
        for (int k = 0; k < used.Count; k++) remap[used[k]] = k;
        var pal = used.SelectMany(v => m.W[v].Select(e => e.B)).Distinct().OrderBy(b => b).ToList();
        var slot = new Dictionary<int, int>();
        for (int k = 0; k < pal.Count; k++) slot[pal[k]] = k;
        int nw = Math.Max(1, used.Max(v => m.W[v].Count));
        var s = new Jboy.Mesh { Raw = (byte[])tmpl.Raw.Clone(), U74 = 0, Shader = tmpl.Shader, Vfmt = tmpl.Vfmt, Palette = pal.Select(b => b + 1).ToList() };
        foreach (var v in used)
            s.Verts.Add(([(float)m.Pos[v][0], (float)m.Pos[v][1], (float)m.Pos[v][2], (float)m.Nrm[v][0], (float)m.Nrm[v][1], (float)m.Nrm[v][2]], 0xFFFFFFFF));
        foreach (var v in used) s.Uvs.Add(((float)m.Uv[v][0], (float)m.Uv[v][1]));
        var flat = new List<(byte[], float)>();
        foreach (var v in used)
        {
            var ws = m.W[v];
            foreach (var (b, w) in ws) flat.Add(([(byte)slot[b], 0, 0, 0], (float)w));
            for (int k = ws.Count; k < nw; k++) flat.Add(([255, 0, 0, 0], 0f));
        }
        int vc = used.Count;
        for (int j = 0; j < nw; j++) s.Weights.Add(flat.GetRange(j * vc, vc).ToArray());   // (vertex-major data, jboy's block view)
        s.Strips = [(6u, Strip.Stripify(tris.Select(t => (remap[t[0]], remap[t[1]], remap[t[2]])).ToList()).Select(x => (ushort)x).ToArray())];
        var mat = m.Material;
        foreach (var p in tmpl.Params)
        {
            var val = p.Value;
            if (p.Type == 0x0f && BinaryPrimitives.ReadInt32BigEndian(val) >= 0)      // (a slot the template leaves unbound stays so)
            {
                int k = slots.TryGetValue(p.Name, out var want) ? textures.IndexOf(want) : -1;
                val = [.. BE32(k), .. val.AsSpan(4)];
            }
            if (p.Name == "g_fSweatSPA" && Truthy(mat?["cutout"])) val = [.. BEF(0f), .. val.AsSpan(4)];
            if (p.Name == "g_fSpecularLev" && mat?["specular"] is JsonNode sp && sp.GetValueKind() != JsonValueKind.Null)
                val = [.. BEF((float)Math.Min(100.0, Math.Max(0.0, sp.GetValue<double>()))), .. val.AsSpan(4)];
            if (p.Name == "g_f4MatDifCol" && Truthy(mat?["tint"]) && val.Length >= 16)
            {
                var rgba = Enumerable.Range(0, 4).Select(i => (double)BinaryPrimitives.ReadSingleBigEndian(val.AsSpan(4 * i))).ToArray();
                var tint = TintRgb(mat!["tint"]!);
                for (int i = 0; i < 3; i++) rgba[i] *= tint[i];
                val = [.. BEF((float)rgba[0]), .. BEF((float)rgba[1]), .. BEF((float)rgba[2]), .. BEF((float)rgba[3]), .. val.AsSpan(16)];
            }
            s.Params.Add(new Jboy.Param { Name = p.Name, Type = p.Type, Value = val });
        }
        // bounding sphere from the vertices as given (doubles; the radius' sum of squares one by one)
        double[] mn = [double.MaxValue, double.MaxValue, double.MaxValue], mx = [double.MinValue, double.MinValue, double.MinValue];
        foreach (var v in used) for (int c = 0; c < 3; c++) { mn[c] = Math.Min(mn[c], m.Pos[v][c]); mx[c] = Math.Max(mx[c], m.Pos[v][c]); }
        double[] cen = [(mn[0] + mx[0]) / 2, (mn[1] + mx[1]) / 2, (mn[2] + mx[2]) / 2];
        double r = 0;
        foreach (var v in used)
        {
            double d0 = m.Pos[v][0] - cen[0], d1 = m.Pos[v][1] - cen[1], d2 = m.Pos[v][2] - cen[2];
            r = Math.Max(r, Math.Sqrt(0.0 + d0 * d0 + d1 * d1 + d2 * d2));
        }
        var md = s.Raw;
        uint fl = Flags(BinaryPrimitives.ReadUInt32BigEndian(md.AsSpan(0x88)), mat);
        BinaryPrimitives.WriteUInt32BigEndian(md.AsSpan(0x88), fl);
        // (vertex format: a cut-out yBumpMapSS mesh gets 4, as all 331 of the stock ones (hair, 0x2710) - the skin
        //  template's 8 drew Seth Rollins' beard cards whole, as blocks; 2026-11-12)
        uint vf = (fl & 0x110) != 0 && tmpl.Shader == "yBumpMapSS" ? 4u : tmpl.Vfmt;
        s.Vfmt = vf;
        BinaryPrimitives.WriteUInt32BigEndian(md.AsSpan(0x8C), vf);
        BinaryPrimitives.WriteSingleBigEndian(md.AsSpan(0xA4), (float)cen[0]);
        BinaryPrimitives.WriteSingleBigEndian(md.AsSpan(0xA8), (float)cen[1]);
        BinaryPrimitives.WriteSingleBigEndian(md.AsSpan(0xAC), (float)cen[2]);
        BinaryPrimitives.WriteSingleBigEndian(md.AsSpan(0xB0), (float)r);
        return s;
    }

    static byte[] BE32(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); return b; }
    static byte[] BEF(float v) { var b = new byte[4]; BinaryPrimitives.WriteSingleBigEndian(b, v); return b; }

    static double[] TintRgb(JsonNode t)
    {
        if (t is JsonValue jv && jv.TryGetValue<string>(out var h))
        {
            h = h.TrimStart('#');
            return [Convert.ToInt32(h[..2], 16) / 255.0, Convert.ToInt32(h[2..4], 16) / 255.0, Convert.ToInt32(h[4..6], 16) / 255.0];
        }
        var v = t.AsArray().Take(3).Select(x => x!.GetValue<double>()).ToArray();
        double sc = v.Max() > 1.0 ? 255.0 : 1.0;
        return v.Select(x => Math.Min(1.0, Math.Max(0.0, x / sc))).ToArray();
    }

    // ---------------------------------------------------------------- face pivots (Builder.face_pivots)
    void FacePivots(List<NMesh> meshes)
    {
        List<double[]> Worlds(List<Jboy.Node> ns)
        {
            var Wn = new List<double[]>();
            for (int j = 0; j < ns.Count; j++)
            {
                if (j >= conv.TFrame.Count) throw new BuildError("appended bones are not supported");
                var fr = conv.TFrame[j];
                int p = j > 0 ? fr.Parent : -1;
                var M = Frame(Rot(fr.R), ns[j].T);
                Wn.Add(p < 0 ? M : Blas.MatMul(Wn[p], M, 4));
            }
            return Wn;
        }
        var sides = new[] { "l_eye", "r_eye" }.Where(index.ContainsKey).Select(n => index[n]).ToList();
        if (!index.ContainsKey("atama") || sides.Count == 0) return;
        var mine = sides.ToDictionary(b => b, _ => new List<double[]>());
        foreach (var m in meshes)
        {
            if (m.Kind != "eye") continue;
            for (int v = 0; v < m.W.Count; v++)
            {
                var ws = m.W[v];
                var best = ws[0];
                foreach (var e in ws) if (e.W > best.W) best = e;          // (max: the first of equals)
                if (mine.TryGetValue(best.B, out var l)) l.Add(m.Pos[v]);
            }
        }
        var tmpl = sides.ToDictionary(b => b, _ => new List<double[]>());
        foreach (var s in conv.Body.Meshes)
        {
            if (s.Shader != "yReflect") continue;
            int nw = s.Weights.Count;
            var flat = s.Weights.SelectMany(blk => blk).ToList();
            for (int v = 0; v < s.Verts.Count; v++)
            {
                var e = flat[v * nw];
                for (int k = 1; k < nw; k++) if (flat[v * nw + k].W > e.W) e = flat[v * nw + k];
                int b = e.Bones[0] < s.Palette.Count ? s.Palette[e.Bones[0]] - 1 : -1;
                if (tmpl.TryGetValue(b, out var l)) l.Add([s.Verts[v].PosNrm[0], s.Verts[v].PosNrm[1], s.Verts[v].PosNrm[2]]);
            }
        }
        var Wm = Worlds(nodes);
        var Wt = Worlds(conv.TNodes);
        // (np.mean over rows: the rows added one by one from the first, then / n)
        static double[]? Mean(List<double[]> P)
        {
            if (P.Count == 0) return null;
            var s = (double[])P[0].Clone();
            for (int i = 1; i < P.Count; i++) for (int c = 0; c < 3; c++) s[c] += P[i][c];
            for (int c = 0; c < 3; c++) s[c] /= P.Count;
            return s;
        }
        var d = new List<double[]>();
        foreach (var b in sides)
        {
            var cm = Mean(mine[b]); var ct = Mean(tmpl[b]);
            if (cm == null || ct == null) continue;
            d.Add([(cm[0] - Wm[b][3]) - (ct[0] - Wt[b][3]), (cm[1] - Wm[b][7]) - (ct[1] - Wt[b][7]), (cm[2] - Wm[b][11]) - (ct[2] - Wt[b][11])]);
        }
        if (d.Count == 0) return;
        var delta = Mean(d)!;
        if (Blas.Norm(delta) <= 0.15) return;
        int a = index["atama"];
        var Rd = Blas.MatTVec3(Wm[a], 4, delta);
        for (int j = 0; j < nodes.Count; j++)
            if (conv.TFrame[j].Parent == a && j > 0)
                nodes[j].T = [nodes[j].T[0] + Rd[0], nodes[j].T[1] + Rd[1], nodes[j].T[2] + Rd[2]];
        Warnings.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "face pivots moved by ({0:F2}, {1:F2}, {2:F2}) onto the model eyes", delta[0], delta[1], delta[2]));
    }

    // ---------------------------------------------------------------- textures
    static bool HasAlpha(Img im) { for (int i = 3; i < im.Px.Length; i += 4) if (im.Px[i] < 255) return true; return false; }

    /// <summary>colour alpha = a mid value from the brightness (40 + L // 3)</summary>
    static Img SpecAlpha(Img im)
    {
        var l = im.ToRgb().ToL();
        var a = new Img(l.W, l.H, 1);
        for (int i = 0; i < l.Px.Length; i++) a.Px[i] = (byte)(40 + l.Px[i] / 3);
        return im.ToRgb().WithAlpha(a);
    }

    Img Sheet(Dictionary<string, Img> images, List<string> keys, (int W, int H) size, byte[] fill)
    {
        // (rock_build.OneCell: the first skin-shader cell's image, else the first; the colour sheet's alpha from the
        //  drawn png - sweat on the skin, the lashes' cut-out)
        var pick = keys.Where(k => k.EndsWith("|S") && images.ContainsKey(k)).ToList();
        if (pick.Count == 0) pick = keys.Where(images.ContainsKey).ToList();
        if (pick.Count == 0) return Img.Filled(size.W, size.H, fill);
        var outi = Resample.ResizeApart(images[pick[0]].ToRgba(), size.W, size.H, Filter.Lanczos);
        if (size == ColorSize && fill[2] != 255 && lashPng != null)
        {
            var src = ImageIO.Load(lashPng).ToRgba();
            outi = outi.WithAlpha(Resample.Resize(src.Channel(3), size.W, size.H, Filter.Lanczos));
        }
        return outi;
    }

    // ---------------------------------------------------------------- an attire
    (SortedDictionary<int, byte[]> Kids, JsonObject Report) Attire(JsonObject a, string entryName)
    {
        if (a["hair_chains"] != null || a["entrance_gear"] != null) throw new BuildError("hair chains / entrance gear are not supported by PortIT's builder");
        var meshes = (a["meshes"] as JsonArray ?? []).Select(x => ReadMesh(x!.AsObject())).ToList();
        foreach (var m in meshes)
        {
            int n = m.Pos.Length;
            if (m.Nrm.Length != n || m.Uv.Length != n || m.Weights.Count != n) throw new BuildError($"{m.Name}: {n} positions but other counts differ");
            foreach (var arr in new[] { m.Pos, m.Nrm, m.Uv })
                if (arr.Any(v => v.Any(x => !double.IsFinite(x)))) throw new BuildError($"{m.Name}: NaN / infinite values");
            if (m.Kind is not ("skin" or "eye" or "hair" or "lash")) throw new BuildError($"{m.Name}: kind {m.Kind}");
            m.W = Weights(m);
        }
        if (!faced) { faced = true; FacePivots(meshes); }
        // moving hair (HairChains): bone chains in the hair model + the game's hair settings (0x3C) and capsules (0x3D)
        List<Jboy.Node>? hairNodes = null; byte[]? hairSettings = null;
        if (Truthy(a["hair_physics"]) && meshes.Any(m => m.Kind == "hair"))
        {
            var Wn = new List<double[]>();
            for (int j = 0; j < nodes.Count; j++)
            {
                var fr = conv.TFrame[j];
                int p = j > 0 ? fr.Parent : -1;
                var M = Frame(Rot(fr.R), nodes[j].T);
                Wn.Add(p < 0 ? M : Blas.MatMul(Wn[p], M, 4));
            }
            var hm = meshes.Where(m => m.Kind == "hair").ToList();
            // (gear that may be worn on the hair - not the body, head, eyes or mouth)
            var ex = meshes.Where(m => m.Kind == "skin" && !System.Text.RegularExpressions.Regex.IsMatch(m.Name, "^(M_)?(Body|Head|EYE|MouthBag|Teeth|Tongue)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();
            var hr = HairChains.Build(nodes, Wn, index, hm.Select(m => (m.Pos, m.W)).ToList(),
                                      meshes.Where(m => m.Kind == "skin" && m.Name.StartsWith("M_Head", StringComparison.OrdinalIgnoreCase) || m.Name.StartsWith("M_Body", StringComparison.OrdinalIgnoreCase)).Select(m => m.Pos).ToList(),
                                      Warnings.Add, ex.Select(m => (m.Pos, m.W)).ToList());
            if (hr != null)
            {
                hairNodes = hr.Nodes; hairSettings = Encoding.ASCII.GetBytes(hr.Settings); Warnings.AddRange(hr.Notes);
                foreach (int i in hr.ExtrasMoved) ex[i].Kind = "hair";     // (into the hair model: the chain bones are there)
            }
        }
        var at = a["textures"] as JsonObject ?? [];
        string? NormOf(NMesh m) => (string?)m.Material?["normal"] ?? (string?)m.Textures?["normal"] ?? (string?)at["normal"];
        string AlphaMode(NMesh m) => Truthy(m.Material?["cutout"]) ? "texture" : (string?)m.Material?["specular_alpha"] ?? "";
        string ColKey(NMesh m)
        {
            var c = (string?)m.Textures?["color"] ?? (string?)at["color"] ?? "";
            return c.Length == 0 ? "" : $"{c}|{NormOf(m) ?? ""}|{AlphaMode(m)}|{(SkinShader(m) ? "S" : "")}";
        }
        var keys = meshes.Where(m => m.Kind != "lash" || m.Textures?["color"] != null).Select(ColKey).Distinct().ToList();
        if (keys.Count == 0) keys = [""];
        var colors = new Dictionary<string, Img>();
        var normals = new Dictionary<string, Img>();
        foreach (var m in meshes)
        {
            string k = ColKey(m);
            if (k.Length == 0 || colors.ContainsKey(k)) continue;
            var im = ImageIO.Load(Path(k.Split('|')[0])).ToRgba();
            string mode = AlphaMode(m);
            var nk = NormOf(m);
            var nim = nk != null ? ImageIO.Load(Path(nk)).ToRgba() : null;
            if (SkinShader(m))
            {
                Img mask;
                if (mode == "flat") mask = Img.Filled(im.W, im.H, 255);
                else if (mode == "texture" && HasAlpha(im)) mask = im.Channel(3);
                else if (mode is "" or "texture" && nim != null && HasAlpha(nim)) mask = nim.Channel(3);
                else mask = SpecAlpha(im).Channel(3);
                var bse = nim ?? Img.Filled(im.W, im.H, 128, 128, 255, 255);
                normals[k] = bse.ToRgba().WithAlpha(Resample.Resize(mask, bse.W, bse.H, Filter.Lanczos));
                var rgb = im.ToRgb().ToRgba().Copy();
                if (m.Material?["sweat_mask"] is JsonNode sw && (string?)sw is { Length: > 0 } swp)
                    rgb = rgb.WithAlpha(Resample.Resize(ImageIO.Load(Path(swp)).ToRgba().ToL(), rgb.W, rgb.H, Filter.Lanczos));
                else
                {
                    byte lv = (byte)(m.Material?["sweat"] is JsonNode sv ? (int)sv.GetValue<double>() : 47);
                    for (int i = 3; i < rgb.Px.Length; i += 4) rgb.Px[i] = lv;
                }
                colors[k] = rgb;
                continue;
            }
            if (mode == "flat") { im = im.Copy(); for (int i = 3; i < im.Px.Length; i += 4) im.Px[i] = 255; }
            else if (mode == "brightness") im = SpecAlpha(im);
            else if (mode != "texture" && !HasAlpha(im)) im = SpecAlpha(im);
            colors[k] = im;
            if (nim != null) normals[k] = nim;
        }
        var color = Sheet(colors, keys, ColorSize, [128, 128, 128, 255]);
        if (colors.Count == 0) color = SpecAlpha(color);
        var normal = Sheet(normals, keys, NormalSize, [128, 128, 255, 255]);
        Img mk;
        if (at["mask"] != null)
        {
            mk = Resample.ResizeApart(ImageIO.Load(Path((string)at["mask"]!)).ToRgba(), MkSize.W, MkSize.H, Filter.Lanczos).Copy();
            for (int i = 0; i < mk.Px.Length; i += 4) mk.Px[i] = 0;
        }
        else mk = Img.Filled(MkSize.W, MkSize.H, 0, 230, 230, 255);

        var kids = new SortedDictionary<int, byte[]>();
        foreach (var (key, kinds, texs) in new[] { (0, new[] { "skin", "eye" }, TexBody), (0x2710, new[] { "hair" }, TexBody), (0x4e20, new[] { "lash" }, new[] { "color" }) })
        {
            var sel = meshes.Where(m => kinds.Contains(m.Kind)).ToList();
            if (sel.Count == 0 && key != 0) continue;
            var tm = key switch { 0 => conv.Body, 0x2710 => conv.Hair ?? conv.Body, _ => conv.Lash };
            var model = new Jboy { Header = tm.Header, Name = tm.Name, Group = tm.Group, Textures = texs.ToList(), Nodes = key == 0x2710 && hairNodes != null ? hairNodes : nodes };
            foreach (var m in sel)
            {
                var vb = m.W.Select(ws => ws.Select(e => e.B).ToList()).ToList();
                foreach (var g in SplitByPalette(m.Tris, vb))
                {
                    var s = JMesh(m, m.Kind, g, texs.ToList());
                    s.U74 = (uint)model.Meshes.Count;
                    model.Meshes.Add(s);
                }
            }
            if (model.Meshes.Count == 0) throw new BuildError("no skin / eye meshes");
            kids[key] = model.Write();
        }
        var t = conv.T;
        for (int i = 3; i < mk.Px.Length; i += 4) mk.Px[i] = 255;   // (Mk as stock: DXT1, alpha 255)
        var texA = new List<(string, byte[])> { ("normal", DdsWriter.Encode(normal, "DXT5")), ("noise", conv.TTex.First(x => x.Name == "noise").Data), ("Mk", DdsWriter.Encode(mk, "DXT1")) };
        texA.AddRange(conv.TTex.Where(x => x.Name.ToLowerInvariant().StartsWith("bloodhair") || x.Name.ToLowerInvariant().StartsWith("bloodhand") || x.Name.ToLowerInvariant().StartsWith("blood")));
        kids[0xa] = TexBundleWrite(texA, t[0xa][..16]);
        // (the hair cards' cells, from the sheet PortIT drew, at this sheet's size: their alpha cut hard in every level)
        List<(int X, int Y, int W, int H)>? cutCells = null;
        if (a["cut_cells"] is JsonArray ccs && a["cut_sheet"] is JsonArray csz)
        {
            double kx = (double)color.W / csz[0]!.GetValue<int>(), ky = (double)color.H / csz[1]!.GetValue<int>();
            cutCells = ccs.Select(c => c!.AsArray()).Select(c => ((int)Math.Floor(c[0]!.GetValue<int>() * kx), (int)Math.Floor(c[1]!.GetValue<int>() * ky),
                                                               (int)Math.Ceiling(c[2]!.GetValue<int>() * kx), (int)Math.Ceiling(c[3]!.GetValue<int>() * ky))).ToList();
        }
        kids[0xc] = TexBundleWrite([("color", cutCells != null ? DdsWriter.Levels(color, "DXT5", cutCells) : DdsWriter.Encode(color, "DXT5"))], t[0xc][..16]);
        foreach (int k in new[] { 0xd, 0xe, 0xf })
            if (t.TryGetValue(k, out var tk))
            {
                var (n0, d0) = TexBundleRead(tk)[0];
                var blank = new byte[d0.Length];
                d0.AsSpan(0, 128).CopyTo(blank);
                kids[k] = TexBundleWrite([(n0, blank)], tk[..16]);
            }
        foreach (int k in new[] { 0xb, 0x28, 0x32, 0x50, 0x64, 0x7530, 0x7531, 0x7532 }) if (t.TryGetValue(k, out var tk)) kids[k] = tk;
        foreach (int k in new[] { 0xc8, 0xc9 }) if (t.TryGetValue(k, out var tk)) kids[k] = tk;
        if (hairSettings != null && kids.ContainsKey(0x2710))
        {
            kids[0x3c] = hairSettings;
            if (HairChains.StockCapsules(templatePath) is { } caps) kids[0x3d] = caps;
            else Warnings.Add("moving hair: no stock body capsules found (0x3D) - the hair may pass through the shoulders");
        }
        // budget
        var dropped = new List<string>();
        var (pages, parts) = Budget.AttirePages(kids);
        foreach (int k in new[] { 0x7532, 0x7531, 0x7530 })
        {
            if (pages <= PageLimit) break;
            if (kids.Remove(k)) { dropped.Add($"0x{k:x}"); (pages, parts) = Budget.AttirePages(kids); }
        }
        if (pages > PageLimit)
            throw new BuildError($"{entryName}: {pages} vertex pages, the game has {PageLimit} per wrestler ({string.Join(", ", parts.Select(kv => $"{kv.Key} {kv.Value}"))}) - reduce the vertex / triangle count");
        var over = parts.Where(kv => kv.Value > Budget.PartLimit).ToList();
        if (over.Count > 0)
            throw new BuildError($"{entryName}: a part over {Budget.PartLimit} vertex pages ({string.Join(", ", over.Select(kv => $"{kv.Key} {kv.Value}"))}) - the match draws at most 2,048 per part");
        int nmesh = new[] { 0, 0x2710, 0x4e20 }.Where(kids.ContainsKey).Sum(k => Jboy.Read(kids[k]).Meshes.Count);
        var rep = new JsonObject { ["name"] = entryName, ["meshes"] = nmesh, ["pages"] = pages, ["parts"] = new JsonObject(parts.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))), ["dropped"] = new JsonArray(dropped.Select(x => (JsonNode?)x).ToArray()) };
        return (kids, rep);
    }

    /// <summary>-&gt; triangle index groups, each needing at most 20 bones and 65535 vertices</summary>
    static List<List<int>> SplitByPalette(int[][] tris, List<List<int>> vbones)
    {
        int Min(int v) => vbones[v].Count > 0 ? vbones[v].Min() : 0;
        int Max(int v) => vbones[v].Count > 0 ? vbones[v].Max() : 0;
        // (by the triangle's highest bone first: moving hair's chain bones are numbered chain by chain, so a chain's
        //  triangles stay together - by the lowest, atama on most of them mixed every chain: 50 meshes for Seth Rollins)
        var order = Enumerable.Range(0, tris.Length).OrderBy(i => (Math.Max(Max(tris[i][0]), Math.Max(Max(tris[i][1]), Max(tris[i][2]))), Min(tris[i][0]), i)).ToList();
        var groups = new List<List<int>>();
        var cur = new List<int>();
        var bones = new HashSet<int>();
        var verts = new HashSet<int>();
        foreach (int i in order)
        {
            var tb = new HashSet<int>(vbones[tris[i][0]]);
            tb.UnionWith(vbones[tris[i][1]]); tb.UnionWith(vbones[tris[i][2]]);
            if (cur.Count > 0)
            {
                var bu = new HashSet<int>(bones); bu.UnionWith(tb);
                var vu = new HashSet<int>(verts); vu.UnionWith(tris[i]);
                if (bu.Count > MaxPal || vu.Count > MaxVerts) { groups.Add(cur); cur = []; bones = []; verts = []; }
            }
            cur.Add(i);
            bones.UnionWith(tb);
            verts.UnionWith(tris[i]);
        }
        if (cur.Count > 0) groups.Add(cur);
        return groups;
    }

    // ---------------------------------------------------------------- the pac
    public JsonArray Build(string outPath)
    {
        lashPng = (spec["attires"]![0]!["textures"]?["color"] is JsonNode c) ? Path((string)c!) : null;
        var arc = Epk8.Read(File.ReadAllBytes(templatePath));
        var names = arc.Groups[0].Entries.Select(e => e.Name).Where(n => n.Any(b => b != 0)).ToList();
        var entries = new List<(byte[], byte[])>();
        var rep = new JsonArray();
        var attires = spec["attires"]!.AsArray();
        for (int i = 0; i < attires.Count; i++)
        {
            var a = attires[i]!.AsObject();
            var name = (names.Count > 0 ? names[0][..6] : "000104"u8.ToArray()).Concat(Encoding.ASCII.GetBytes($"{i}2")).ToArray();
            if (a["name"] is JsonNode nm && (string?)nm is { Length: > 0 } nms && Encoding.Latin1.GetString(name) != nms)
                Warnings.Add($"attire {i + 1}: \"name\" '{nms}' is not the entry name {Encoding.Latin1.GetString(name)} the game looks up - ignored");
            var (kids, r) = Attire(a, Encoding.Latin1.GetString(name));
            entries.Add((name, PackKids(kids)));
            rep.Add(r);
        }
        File.WriteAllBytes(outPath, Epk8.Write(File.ReadAllBytes(templatePath), entries));
        return rep;
    }

    /// <summary>rock_build + svr11_build main: model.json -&gt; ch.pac and the report (full: no page limit - the big-model
    /// route; the colour / normal sheets at the stock sizes either way)</summary>
    public static JsonObject Run(string modelJson, string outPac, bool noLimit, string? stockBones = null)
    {
        var rep = new JsonObject { ["ok"] = false, ["out"] = outPac, ["warnings"] = new JsonArray() };
        try
        {
            var spec = JsonNode.Parse(File.ReadAllText(modelJson))!.AsObject();
            var b = new SvrBuilder(spec, System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(modelJson))!);
            if (noLimit) b.PageLimit = 1 << 30;
            rep["attires"] = b.Build(outPac);
            rep["warnings"] = new JsonArray(b.Warnings.Select(w => (JsonNode?)w).ToArray());
            try
            {
                var pr = PropBones.FixPac(outPac, stockBones, outPac);
                if (pr.Fixed) rep["prop_bones"] = PropBones.ReportLine(pr);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or KeyNotFoundException or JsonException)
            {
                rep["warnings"]!.AsArray().Add($"bone rests not checked (prop_bones: {e.Message})");
            }
            rep["ok"] = true;
        }
        catch (BuildError e) { rep["error"] = e.Message; }
        catch (Exception e) when (e is KeyNotFoundException or ArgumentException or IOException or InvalidDataException)
        {
            rep["error"] = $"{e.GetType().Name}: {e.Message}";
        }
        return rep;
    }
}

/// <summary>a model pac's vertex pages per attire (port of model_budget.py)</summary>
public static class Budget
{
    public const int Page = 4096, OldLimit = 192, Limit = 1600, Warn = 1530, PartLimit = 1900;
    public const string NeedsVersion = "2.1.6";

    public static int Pages(long n) => (int)((n + Page - 1) / Page);

    public static int MeshPages(Jboy.Mesh s)
    {
        int stride = Math.Max(1, (int)s.U5c[0]) == 1 ? 32 : 44;
        long idx = s.Strips.Sum(st => (long)st.Idx.Length);
        return Pages(s.Verts.Count * (long)stride) + Pages(idx * 2);
    }

    public static (int Total, SortedDictionary<string, int> Parts) AttirePages(IDictionary<int, byte[]> kids)
    {
        int total = 0;
        var parts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var ordered = new List<(string, int)>();
        foreach (var k in kids.Keys.OrderBy(x => x))
        {
            var d = kids[k];
            if (!Jboy.IsJboy(d)) continue;
            Jboy m;
            try { m = Jboy.Read(d); } catch (Exception) { continue; }
            int p = m.Meshes.Sum(MeshPages);
            ordered.Add(($"0x{k:x}", p));
            total += p;
        }
        foreach (var (k, p) in ordered) parts[k] = p;
        return (total, parts);
    }

    /// <summary>[(entry name, pages, parts)] for the wrestler (kind 2) entries of a ch.pac</summary>
    public static List<(string Name, int Pages, SortedDictionary<string, int> Parts)> ModelPages(string path)
    {
        var o = new List<(string, int, SortedDictionary<string, int>)>();
        foreach (var g in Epk8.Read(File.ReadAllBytes(path)).Groups)
        {
            if (g.TypeText != "EMD ") continue;
            foreach (var e in g.Entries)
            {
                var n = e.NameText.Trim('\0', ' ');
                if (!n.EndsWith('2')) continue;
                var kids = Pach.Read(Bpe.Unpack(e.Data)).ToDictionary(x => (int)x.Id, x => Bpe.Unpack(x.Data));
                var (t, parts) = AttirePages(kids);
                o.Add((n, t, parts));
            }
        }
        return o;
    }

    public static string? Needs(string path)
    {
        try { return ModelPages(path).Select(x => x.Pages).DefaultIfEmpty(0).Max() > OldLimit ? NeedsVersion : null; }
        catch (Exception) { return null; }
    }
}
