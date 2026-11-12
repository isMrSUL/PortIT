using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PortIT.Engine.Util;

namespace PortIT.Engine.Readers;

/// <summary>
/// WWE 2K19 - 2K22 character packs (chNNNNN.pac, Yuke's engine) -&gt; models read like a 2K23+ .mdl (port of
/// readers/jboy_2k.py - its notes have the layout). Members are "ZLIB", "OODL" (Kraken, with the user's own
/// oo2core DLL from a 2K game) or stored "JBOY" models; one member is the texture table.
/// </summary>
public static class YukesPack
{
    static readonly HashSet<string> BaseNames = ["M_Head", "M_Body", "M_EYE", "M_transB_eyeshadow", "M_MouthBag", "M_Teeth", "M_Tongue",
                                                 "M_Head_Caruncle", "M_transB_eyelash", "M_LowHead", "M_LowEyeSh"];
    static readonly Dictionary<string, string> Full = new() { ["M_transB_eyesha"] = "M_transB_eyeshadow", ["M_transB_eyelas"] = "M_transB_eyelash" };

    public static string CacheRoot => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortIT", "pac_cache");

    public static bool IsPac(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var b = new byte[4];
            return f.Read(b) == 4 && (b.AsSpan().SequenceEqual("EPK8"u8) || b.AsSpan().SequenceEqual("EPAC"u8));
        }
        catch (IOException) { return false; }
    }

    // ---------------------------------------------------------------- Oodle: Kraken.cs; the user's own DLL only for other codecs (never part of PortIT)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate long OodleDecompress(byte[] src, long srcLen, byte[] dst, long dstLen, int fuzz, int crc, int verbose,
                                  IntPtr dstBase, long dstBaseSize, IntPtr cb, IntPtr cbData, IntPtr decoderMem, long decoderMemSize, int threadPhase);
    static OodleDecompress? oodle;

    static OodleDecompress Oodle()
    {
        if (oodle != null) return oodle;
        var cands = new List<string> { Environment.GetEnvironmentVariable("PORTIT_OODLE_DLL") ?? "" };
        string[] games = ["WWE 2K26", "WWE 2K25", "WWE 2K24", "WWE 2K20", "WWE 2K19"];
        foreach (var steam in new[] { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam" })
            foreach (var g in games) cands.AddRange(Glob(System.IO.Path.Combine(steam, "steamapps", "common", g)));
        foreach (char drive in "CDEFGHIJ")
            foreach (var g in games) cands.AddRange(Glob($@"{drive}:\SteamLibrary\steamapps\common\{g}"));
        foreach (var p in cands)
            if (p.Length > 0 && File.Exists(p))
            {
                var h = NativeLibrary.Load(p);
                return oodle = Marshal.GetDelegateForFunctionPointer<OodleDecompress>(NativeLibrary.GetExport(h, "OodleLZ_Decompress"));
            }
        throw new InvalidOperationException("This pack is packed with Oodle and no WWE 2K game with its oo2core file was found on this PC " +
                                            "(PortIT uses your own copy; set PORTIT_OODLE_DLL to its path)");
    }

    static IEnumerable<string> Glob(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "oo2core_*_win64.dll") : [];

    /// <summary>every member of a .pac, unpacked (jboy_2k.members): zlib members, Oodle members, stored models</summary>
    public static List<byte[]> Members(string path)
    {
        var b = File.ReadAllBytes(path);
        var outl = new List<byte[]>();
        var spans = new List<(int, int)>();
        foreach (int o in Find(b, "ZLIB"u8))
        {
            if (o + 16 > b.Length) continue;
            uint mark = U32le(b, o + 4), c = U32le(b, o + 8);
            if (mark != 4656 || o + 16 + c > b.Length) continue;
            try
            {
                using var z = new ZLibStream(new MemoryStream(b, o + 16, (int)c), CompressionMode.Decompress);
                var ms = new MemoryStream();
                z.CopyTo(ms);
                outl.Add(ms.ToArray());
                spans.Add((o, o + 16 + (int)c));
            }
            catch (InvalidDataException) { }
        }
        foreach (int o in Find(b, "OODL"u8))
        {
            if (o + 24 > b.Length || !b.AsSpan(o + 12, 4).SequenceEqual("KRKN"u8)) continue;
            int usize = (int)U32le(b, o + 16), csize = (int)U32le(b, o + 20);
            var src = b.AsSpan(o + 24, csize).ToArray();
            var dst = new byte[usize];
            try
            {
                PortIT.Engine.Readers.Oodle.Unpack(src, dst, "pack member",
                    (s, sl, d, dl) => Oodle()(s, sl, d, dl, 1, 0, 0, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, 3));
            }
            catch (InvalidDataException) { throw new InvalidDataException($"an Oodle member of {System.IO.Path.GetFileName(path)} is damaged"); }
            outl.Add(dst);
            spans.Add((o, o + 24 + csize));
        }
        foreach (int o in Find(b, "JBOY"u8))      // (models stored as they are)
        {
            if (spans.Any(s => s.Item1 <= o && o < s.Item2) || o + 12 > b.Length) continue;
            uint L = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o + 4));
            if (BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o + 8)) == 0x10 && o + 8 + L <= b.Length) outl.Add(b[o..(o + 8 + (int)L)]);
        }
        return outl;
    }

    /// <summary>non-overlapping matches, left to right (Python's re.finditer on a literal)</summary>
    static IEnumerable<int> Find(byte[] b, ReadOnlySpan<byte> tag)
    {
        var res = new List<int>();
        int p = 0;
        while (true)
        {
            int i = b.AsSpan(p).IndexOf(tag);
            if (i < 0) break;
            res.Add(p + i);
            p += i + tag.Length;
        }
        return res;
    }

    static uint U32le(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));

    /// <summary>the texture table member -&gt; {name: dds bytes}, or null if the blob is not one</summary>
    public static Dictionary<string, byte[]>? TexturesOf(byte[] blob)
    {
        if (blob.Length < 48 || !blob.AsSpan(32, 4).SequenceEqual("dds\0"u8)) return null;
        int n = (int)U32le(blob, 0);
        var o = new Dictionary<string, byte[]>();
        for (int k = 0; k < n; k++)
        {
            int r = 16 + 32 * k;
            string name = Ascii(blob.AsSpan(r, 16));
            int size = (int)U32le(blob, r + 20), off = (int)U32le(blob, r + 24);
            if (off + 4 <= blob.Length && blob.AsSpan(off, 4).SequenceEqual("DDS "u8)) o[name.ToLowerInvariant()] = blob.AsSpan(off, Math.Min(size, blob.Length - off)).ToArray();
        }
        return o;
    }

    /// <summary>bytes up to the first NUL, as Python's decode('ascii', 'replace')</summary>
    static string Ascii(ReadOnlySpan<byte> b)
    {
        int z = b.IndexOf((byte)0);
        var s = z < 0 ? b : b[..z];
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(c < 128 ? (char)c : '\uFFFD');
        return sb.ToString();
    }

    // ---------------------------------------------------------------- one JBOY model
    public sealed class RawBone { public string Name = ""; public double[] T = new double[3], Euler = new double[3]; public int Parent; }

    public sealed class Piece
    {
        public int N;
        public string Group = "", Shader = "", Name = "", Diffuse = "";
        public float[] Pos = [], Nrm = [];      // n x 3
        public byte[] Rgba = [];                // n x 4
        public double[] Uv = [];                // n x 2
        public long[] Bone = [];                // n x Slots
        public double[] Weight = [];            // n x Slots
        public int Slots;
        public int[] Tris = [];                 // n x 3
        public Dictionary<string, string> Textures = [];
        public List<Mcd.Shape> Shapes = [];
    }

    public sealed class Model { public List<RawBone> Bones = []; public List<Piece> Meshes = []; public List<string> Groups = []; }

    static uint U(byte[] x, int o) => BinaryPrimitives.ReadUInt32BigEndian(x.AsSpan(o));
    static float F(byte[] x, int o) => BinaryPrimitives.ReadSingleBigEndian(x.AsSpan(o));

    public static Model ReadJboy(byte[] x)
    {
        string Nm(int o) => Ascii(x.AsSpan(o, 16));
        int count = (int)U(x, 24), bas = (int)U(x, 28) + 8, nbones = (int)U(x, 32), ntex = (int)U(x, 36);
        bool older = bas < 88;
        var m = new Model();
        int bp = (int)U(x, 40) + 8;
        for (int i = 0; i < nbones; i++)
        {
            int o = bp + 80 * i;
            m.Bones.Add(new RawBone
            {
                Name = Nm(o), T = [F(x, o + 16), F(x, o + 20), F(x, o + 24)], Euler = [F(x, o + 32), F(x, o + 36), F(x, o + 40)],
                Parent = BinaryPrimitives.ReadInt32BigEndian(x.AsSpan(o + 48)),
            });
        }
        int tp = (int)U(x, 44) + 8;
        var tex = Enumerable.Range(0, ntex).Select(k => Nm(tp + 16 * k).ToLowerInvariant()).ToList();
        int gp = (int)U(x, 48) + 8;
        for (int k = 0; k < (int)U(x, 52); k++) { string g = Nm(gp + 32 * k); m.Groups.Add(Full.GetValueOrDefault(g, g)); }
        var shapeNames = new List<string>();
        if (!older && U(x, 72) < 1000)
        {
            int sp = (int)U(x, 76) + 8;
            for (int k = 0; k < (int)U(x, 72); k++) shapeNames.Add(Nm(sp + 16 * k));
        }
        long[]? palette = null;
        if (!older)
        {
            int sp = (int)U(x, 84) + 8;
            int pn = (int)U(x, sp), po = (int)U(x, sp + 4) + 8;
            palette = Enumerable.Range(0, pn).Select(i => (long)U(x, po + 4 * i)).ToArray();
        }
        for (int i = 0; i < count; i++)
        {
            int o = bas + 184 * i;
            var w = Enumerable.Range(0, 46).Select(k => U(x, o + 4 * k)).ToArray();
            int n = (int)w[0], slots = (int)w[23];
            int vp = (int)w[26] + 8, wp = (int)w[27] + 8, uvp = (int)w[28] + 8, ip = (int)w[39] + 8;
            int real = (int)U(x, vp) + 8;
            int nblk = real >= vp + 4 ? Math.Max(1, (real - vp) / 4) : 1;
            var blocks = Enumerable.Range(0, nblk).Select(k => (int)U(x, vp + 4 * k) + 8).ToArray();
            var p = new Piece { N = n, Slots = slots, Pos = new float[3 * n], Nrm = new float[3 * n], Rgba = new byte[4 * n], Uv = new double[2 * n] };
            for (int v = 0; v < n; v++)
            {
                int vo = blocks[0] + 28 * v;
                for (int c = 0; c < 3; c++) { p.Pos[3 * v + c] = F(x, vo + 4 * c); p.Nrm[3 * v + c] = -F(x, vo + 12 + 4 * c); }
                // (ARGB -> RGBA)
                p.Rgba[4 * v] = x[vo + 25]; p.Rgba[4 * v + 1] = x[vo + 26]; p.Rgba[4 * v + 2] = x[vo + 27]; p.Rgba[4 * v + 3] = x[vo + 24];
            }
            long[] skin = older ? Enumerable.Range(0, (int)w[2]).Select(k => (long)w[3 + k] - 1).ToArray() : palette!;
            bool two = (w[41] & 0x80) != 0;
            p.Bone = new long[n * slots];
            p.Weight = new double[n * slots];
            for (int v = 0; v < n; v++)
                for (int s = 0; s < slots; s++)
                {
                    int ro = wp + 8 * (v * slots + s);
                    long idx = two ? x[ro] * 256L + x[ro + 1] : x[ro];
                    bool empty = idx == (two ? 65535 : 255);
                    double val = F(x, ro + 4);
                    bool ok = !empty && idx < skin.Length && val > 0;
                    p.Bone[v * slots + s] = ok ? skin[Math.Min(idx, skin.Length - 1)] : 0;
                    p.Weight[v * slots + s] = ok ? val : 0.0;
                }
            for (int v = 0; v < n; v++) { p.Uv[2 * v] = F(x, uvp + 8 * v); p.Uv[2 * v + 1] = F(x, uvp + 8 * v + 4); }
            // triangle strips (12-byte records up to the first index list)
            var tris = new List<int>();
            int first = (int)U(x, ip + 8) + 8;
            for (int j = 0; j < Math.Max(1, (first - ip) / 12); j++)
            {
                int q = ip + 12 * j;
                int cnt = (int)U(x, q + 4), ptr = (int)U(x, q + 8) + 8;
                if (cnt == 0 || ptr + 2 * cnt > x.Length) continue;
                var ix = Enumerable.Range(0, cnt).Select(k => (int)BinaryPrimitives.ReadUInt16BigEndian(x.AsSpan(ptr + 2 * k))).ToArray();
                if (x[q] == 6)
                {
                    for (int k = 0; k + 2 < ix.Length; k++)
                    {
                        int a = ix[k], b = ix[k + 1], c = ix[k + 2];
                        if (a == b || b == c || a == c) continue;
                        if (k % 2 == 0) tris.AddRange([a, b, c]); else tris.AddRange([b, a, c]);
                    }
                }
                else tris.AddRange(ix.Take(ix.Length / 3 * 3));
            }
            p.Tris = tris.ToArray();
            for (int k = 0; k < (int)w[37]; k++)
            {
                int pp = (int)U(x, (int)w[38] + 8 + 4 * k) + 8;
                if (BinaryPrimitives.ReadUInt16BigEndian(x.AsSpan(pp + 16)) != 15) continue;
                int ti = BinaryPrimitives.ReadInt32BigEndian(x.AsSpan(pp + 20));
                if (ti >= 0 && ti < ntex) p.Textures[Nm(pp)] = tex[ti];
            }
            if (blocks.Length - 1 == shapeNames.Count && shapeNames.Count > 0)
                for (int k = 0; k < shapeNames.Count; k++)
                {
                    var idx = new List<long>();
                    var delta = new List<double>();
                    for (int v = 0; v < n; v++)
                    {
                        int so = blocks[k + 1] + 28 * v;
                        double dx = F(x, so), dy = F(x, so + 4), dz = F(x, so + 8);
                        if (Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz))) > 1e-5) { idx.Add(v); delta.AddRange([dx, dy, dz]); }
                    }
                    p.Shapes.Add(new Mcd.Shape { Name = shapeNames[k], Index = idx.ToArray(), Delta = delta.ToArray(), Word1 = new uint[idx.Count] });
                }
            p.Group = w[24] < m.Groups.Count ? m.Groups[(int)w[24]] : "M_Object";
            p.Shader = Nm(o + 124);
            m.Meshes.Add(p);
        }
        return m;
    }

    // ---------------------------------------------------------------- all models of a pack -> one 2K-style model
    static string Title(string stem) => "M_" + string.Join("_", stem.Split('_').Where(s => s.Length > 0).Select(s => s[..1].ToUpperInvariant() + s[1..]));
    static string Stem(string tex) => tex.EndsWith("_color") ? tex[..^"_color".Length] : tex;

    /// <summary>the texture name a merged mesh goes by: the body is 'body', the rest their colour map's name</summary>
    public static string StemOf(Piece me) => me.Name == "M_Body" ? "body" : me.Diffuse.Length > 0 ? Stem(me.Diffuse) : "";

    public sealed class Combined { public List<Mcd.Bone> Bones = []; public List<Piece> Meshes = []; }

    public static Combined Combine(List<Model> models)
    {
        var withHead = models.Where(m => m.Meshes.Any(me => me.Group == "M_Head")).ToList();
        var main = withHead.Count > 0 ? withHead.Aggregate((a, b) => b.Meshes.Count > a.Meshes.Count ? b : a) : models[0];
        var order = new List<Model> { main };
        order.AddRange(models.Where(m => !ReferenceEquals(m, main)));
        var bones = new List<Mcd.Bone>();
        var byName = new Dictionary<string, int>();
        var maps = new List<long[]>();
        foreach (var m in order)
        {
            var mp = new List<long>();
            foreach (var b in m.Bones)
            {
                // (older hair models hang on 'Bip Head' - it is the head)
                string same = b.Name == "Bip Head" && byName.ContainsKey("J_Head") ? "J_Head" : b.Name;
                if (byName.TryGetValue(same, out int at)) { mp.Add(at); continue; }
                string? par = b.Parent >= 0 && b.Parent < m.Bones.Count ? m.Bones[b.Parent].Name : null;
                int pk = par != null && par.Length > 0 ? byName.GetValueOrDefault(par, -1) : -1;
                byName[b.Name] = bones.Count;
                mp.Add(bones.Count);
                bones.Add(new Mcd.Bone { Name = b.Name, Parent = pk, T = (double[])b.T.Clone(), Euler = (double[])b.Euler.Clone() });
            }
            maps.Add(mp.ToArray());
        }
        // (the finger / toe end bones 2K23+ has: 1.1 x the last joint's own length further along its X, unrotated)
        for (int i = 0; i < bones.Count; i++)
        {
            string n = bones[i].Name;
            if (Regex.IsMatch(n, @"^(?:J_\w+F3_[LR]|J_Toe_[LR]|H_Toe_[LR])$") && !byName.ContainsKey(n + "_stub"))
            {
                double ln = Math.Sqrt(bones[i].T.Sum(t => t * t));
                if (ln == 0) ln = 2.5;
                byName[n + "_stub"] = bones.Count;
                bones.Add(new Mcd.Bone { Name = n + "_stub", Parent = i, T = [1.1 * ln, 0.0, 0.0], Euler = [0.0, 0.0, 0.0] });
            }
        }
        foreach (var bn in bones)
        {
            var R = Mcd.EulerMatrix(bn.Euler);
            var M = new double[4, 4];
            for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) M[r, c] = R[r, c];
            for (int c = 0; c < 3; c++) M[3, c] = bn.T[c];
            M[3, 3] = 1;
            bn.Local = M;
            bn.World = bn.Parent < 0 ? M : Mcd.MatMul(M, bones[bn.Parent].World);
        }
        // name every piece, then put pieces of one name and one texture together
        var pieces = new List<Piece>();
        for (int mi = 0; mi < order.Count; mi++)
            foreach (var me in order[mi].Meshes)
            {
                string diffuse = me.Textures.GetValueOrDefault("g_mDiffusePrm") is { Length: > 0 } dd ? dd
                    : me.Textures.GetValueOrDefault("g_mEyeOvlPrm") ?? "";
                string name = me.Group;
                if (name == "M_Body" && diffuse.Length > 0 && diffuse != "all_color" && !me.Shader.StartsWith("yCS_skin"))
                    name = Title(Stem(diffuse));            // (the clothes model calls every piece M_Body)
                if (name.StartsWith("M_Object")) name = diffuse.Length > 0 ? Title(Stem(diffuse)) : name;
                var mp = maps[mi];
                var p = Clone(me);
                p.Name = name; p.Diffuse = diffuse;
                p.Bone = me.Bone.Select(b => mp[b]).ToArray();
                pieces.Add(p);
            }
        ParentHelpers(bones, pieces);
        var merged = new List<((string, string) Key, List<Piece> Ps)>();
        foreach (var p in pieces)
        {
            int at = merged.FindIndex(g => g.Key == (p.Name, p.Diffuse));
            if (at < 0) merged.Add(((p.Name, p.Diffuse), [p])); else merged[at].Ps.Add(p);
        }
        var meshes = new List<Piece>();
        var seen = new Dictionary<string, int>();
        foreach (var ((name, diffuse), ps) in merged)
        {
            int k = seen.GetValueOrDefault(name, 0);
            seen[name] = k + 1;
            // (a second piece on one name with its own texture is named by it: Hogan's beard in M_Hair -> M_Beard)
            string alt = diffuse.Length > 0 ? Title(Stem(diffuse)) : "";
            string final = k == 0 ? name : alt.Length > 0 && !seen.ContainsKey(alt) ? alt : $"{name}_{k + 1}";
            seen.TryAdd(final, 1);
            int slots = ps.Max(p => p.Slots), total = ps.Sum(p => p.N);
            var o = new Piece
            {
                Name = final, Diffuse = diffuse, Shader = ps[0].Shader, N = total, Slots = slots,
                Textures = ps[0].Textures, Group = ps[0].Group,
                Pos = ps.SelectMany(p => p.Pos).ToArray(), Nrm = ps.SelectMany(p => p.Nrm).ToArray(),
                Rgba = ps.SelectMany(p => p.Rgba).ToArray(), Uv = ps.SelectMany(p => p.Uv).ToArray(),
                Bone = new long[total * slots], Weight = new double[total * slots],
                Shapes = ps.Count == 1 ? ps[0].Shapes : [],
            };
            int off = 0;
            var tri = new List<int>();
            foreach (var p in ps)
            {
                for (int v = 0; v < p.N; v++)
                    for (int s = 0; s < p.Slots; s++)
                    {
                        o.Bone[(off + v) * slots + s] = p.Bone[v * p.Slots + s];
                        o.Weight[(off + v) * slots + s] = p.Weight[v * p.Slots + s];
                    }
                tri.AddRange(p.Tris.Select(t => t + off));
                off += p.N;
            }
            o.Tris = tri.ToArray();
            meshes.Add(o);
        }
        return new Combined { Bones = bones, Meshes = meshes };
    }

    static Piece Clone(Piece me) => new()
    {
        N = me.N, Group = me.Group, Shader = me.Shader, Pos = me.Pos, Nrm = me.Nrm, Rgba = me.Rgba, Uv = me.Uv, Bone = me.Bone,
        Weight = me.Weight, Slots = me.Slots, Tris = me.Tris, Textures = me.Textures, Shapes = me.Shapes,
    };

    /// <summary>2K22's helper bones with no parent get the real bone (J_ / H_) they share vertices with most
    /// (their world place kept), else the nearest real bone - jboy_2k._parent_helpers</summary>
    static void ParentHelpers(List<Mcd.Bone> bones, List<Piece> pieces)
    {
        var real = bones.Select(b => b.Name.StartsWith("J_") || b.Name.StartsWith("H_")).ToArray();
        var helper = Enumerable.Range(0, bones.Count).Where(i => bones[i].Parent < 0 && !real[i] && bones[i].Name != "vector").ToList();
        if (helper.Count == 0) return;
        int nb = bones.Count;
        var share = new double[nb, nb];
        // (np.add.at per slot pair, vertices in order - the same summing order)
        foreach (var p in pieces)
            for (int a = 0; a < p.Slots; a++)
                for (int c = 0; c < p.Slots; c++)
                    if (a != c)
                        for (int v = 0; v < p.N; v++)
                            share[p.Bone[v * p.Slots + a], p.Bone[v * p.Slots + c]] += Math.Min(p.Weight[v * p.Slots + a], p.Weight[v * p.Slots + c]);
        foreach (int i in helper)
        {
            int k = 0;
            double best = real[0] ? share[i, 0] : 0.0;
            for (int j = 1; j < nb; j++) { double s = real[j] ? share[i, j] : 0.0; if (s > best) { best = s; k = j; } }   // (argmax: the first)
            if (best <= 0)
            {
                double bd = double.PositiveInfinity;
                k = 0;
                for (int j = 0; j < nb; j++)
                {
                    double d = double.PositiveInfinity;
                    if (real[j])
                    {
                        double dx = bones[j].World[3, 0] - bones[i].World[3, 0], dy = bones[j].World[3, 1] - bones[i].World[3, 1], dz = bones[j].World[3, 2] - bones[i].World[3, 2];
                        d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    }
                    if (d < bd) { bd = d; k = j; }
                }
            }
            bones[i].Parent = k;
            bones[i].Local = Mcd.MatMul(bones[i].World, Invert4(bones[k].World));
        }
    }

    static double[,] Invert4(double[,] m)
    {
        var a = new double[4, 8];
        for (int r = 0; r < 4; r++) { for (int c = 0; c < 4; c++) a[r, c] = m[r, c]; a[r, 4 + r] = 1; }
        for (int c = 0; c < 4; c++)
        {
            int piv = c;
            for (int r = c + 1; r < 4; r++) if (Math.Abs(a[r, c]) > Math.Abs(a[piv, c])) piv = r;
            for (int k = 0; k < 8; k++) (a[c, k], a[piv, k]) = (a[piv, k], a[c, k]);
            double d = a[c, c];
            for (int k = 0; k < 8; k++) a[c, k] /= d;
            for (int r = 0; r < 4; r++)
                if (r != c) { double f = a[r, c]; for (int k = 0; k < 8; k++) a[r, k] -= f * a[c, k]; }
        }
        var o = new double[4, 4];
        for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) o[r, c] = a[r, 4 + c];
        return o;
    }

    // ---------------------------------------------------------------- a pack part as a 2K model (jboy_2k.fill)
    static (string Path, Combined Model)? lastModel;

    static Combined ModelFile(string path)
    {
        if (lastModel is { } lm && lm.Path == path) return lm.Model;
        var data = File.ReadAllBytes(path);
        var models = new List<Model>();
        int q = 0;
        while (q < data.Length)
        {
            long L = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(q));
            models.Add(ReadJboy(data[(q + 8)..(q + 8 + (int)L)]));
            q += 8 + (int)L;
        }
        var c = Combine(models);
        lastModel = (path, c);
        return c;
    }

    /// <summary>fills an Mcd from a "PJBY" pointer file: the base (body, face) or attire part of a pack</summary>
    public static void Fill(Mcd m, string path, byte[] raw)
    {
        using var info = JsonDocument.Parse(raw.AsMemory(4));
        var whole = ModelFile(System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, info.RootElement.GetProperty("model").GetString()!)));
        bool wantBase = info.RootElement.GetProperty("part").GetString() == "base";
        m.Version = 0; m.Game = "2K22 (Yuke's)";
        m.Bones = whole.Bones;
        m.Lods = [[]];
        foreach (var me in whole.Meshes)
        {
            if (BaseNames.Contains(Regex.Replace(me.Name, @"_\d+$", "")) != wantBase) continue;   // (M_EYE_2: a second eye texture)
            int n = me.N, slots = me.Slots;
            var w = (double[])me.Weight.Clone();
            for (int v = 0; v < n; v++)
            {
                double s = Np.Sum(w.AsSpan(v * slots, slots));
                for (int c = 0; c < slots; c++) w[v * slots + c] = s > 0 ? w[v * slots + c] / s : 0.0;
            }
            int k = m.Meshes.Count;
            string stem = StemOf(me);
            if (stem.Length == 0) stem = me.Name[2..].ToLowerInvariant();
            var bbox = new double[6];
            for (int c = 0; c < 3; c++)
            {
                bbox[c] = Enumerable.Range(0, n).Min(v => (double)me.Pos[3 * v + c]);
                bbox[3 + c] = Enumerable.Range(0, n).Max(v => (double)me.Pos[3 * v + c]);
            }
            var mb = new Mcd.MeshBuf
            {
                Material = me.Name, Bbox = bbox, Vertices = n, Object = stem, Shader = me.Shader, Influences = slots,
                // (no COLOR stream: the packs' vertex colours are plain white, and 2K23+ reads COLOR as the hair's colour)
                Streams =
                [
                    new Mcd.Stream { Sem = "POSITION", Format = "R32_G32_B32", Type = "float", Dtype = "<f4", C = 3, V = me.Pos.Select(x => (double)x).ToArray() },
                    new Mcd.Stream { Sem = "NORMAL", Format = "R32_G32_B32", Type = "float", Dtype = "<f4", C = 3, V = me.Nrm.Select(x => (double)x).ToArray() },
                    // (stored the way the 2K .mcd stores V already)
                    new Mcd.Stream { Sem = "TEXCOORD", Format = "R32_G32", Type = "float", Dtype = "<f4", C = 2, V = me.Uv.Select(x => (double)(float)x).ToArray() },
                ],
                SkinBones = me.Bone.Select(b => (ushort)b).ToArray(), SkinWeights = w,
                Remap = Enumerable.Range(0, n).Select(i => (uint)i).ToArray(), Shapes = me.Shapes, Stats = [],
            };
            m.Meshes.Add(mb);
            // (wound like the .mcd's stored order: the (b, a, c) swap the .mcd reader makes)
            var tri = new int[me.Tris.Length];
            for (int i = 0; i < me.Tris.Length / 3; i++) { tri[3 * i] = me.Tris[3 * i + 1]; tri[3 * i + 1] = me.Tris[3 * i]; tri[3 * i + 2] = me.Tris[3 * i + 2]; }
            m.Lods[0].Add(new Mcd.LodMesh { Mesh = k, Triangles = tri, Subsets = [[(uint)k, 0, (uint)me.Tris.Length, 0]] });
            m.Objects.Add((stem + ":skinned", 0));
        }
        m.Bbox = m.Meshes.Count == 0 ? new double[6] :
            [m.Meshes.Min(b => b.Bbox[0]), m.Meshes.Min(b => b.Bbox[1]), m.Meshes.Min(b => b.Bbox[2]),
             m.Meshes.Max(b => b.Bbox[3]), m.Meshes.Max(b => b.Bbox[4]), m.Meshes.Max(b => b.Bbox[5])];
    }

    // ---------------------------------------------------------------- a pack -> a 2K-style wrestler folder (cached)
    public static bool IsCharacterPack(string name) => !Regex.IsMatch(System.IO.Path.GetFileName(name), @"^tx\d", RegexOptions.IgnoreCase);

    public static List<string> TexturePacks(string pac)
    {
        var m = Regex.Match(System.IO.Path.GetFileName(pac), @"^ch(\d+)", RegexOptions.IgnoreCase);
        if (!m.Success) return [];
        var d = System.IO.Path.GetDirectoryName(pac)!;
        return Directory.EnumerateFiles(d).Select(System.IO.Path.GetFileName).OfType<string>()
            .Where(f => Regex.IsMatch(f, $@"^tx{m.Groups[1].Value}(\D|$)", RegexOptions.IgnoreCase) && f.EndsWith(".pac", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal).Select(f => System.IO.Path.Combine(d, f)).ToList();
    }

    /// <summary>the cache key: sha1 of "abs path (lower) | size | mtime (whole seconds)" -&gt; 12 hex digits, as jboy_2k._key
    /// (so the C# and the Python engine share one cache)</summary>
    public static string Key(string pac)
    {
        var fi = new FileInfo(pac);
        long mtime = (long)Math.Floor((fi.LastWriteTimeUtc - DateTime.UnixEpoch).TotalSeconds);
        string s = $"{System.IO.Path.GetFullPath(pac).ToLowerInvariant()}|{fi.Length}|{mtime}";
        return Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(s)))[..12];
    }

    public static string? Cached(string pac)
    {
        try
        {
            var root = System.IO.Path.Combine(CacheRoot, Key(pac));
            if (!File.Exists(System.IO.Path.Combine(root, ".done"))) return null;
            return Directory.EnumerateDirectories(root).FirstOrDefault();
        }
        catch (IOException) { return null; }
    }

    /// <summary>chNNNNN.pac -&gt; &lt;cache&gt;/&lt;key&gt;/&lt;name&gt;/ {BaseModel/baseModel.mdl, Attire/Attire.mdl, Textures} -&gt; that folder</summary>
    public static string Extract(string pac, string? name = null, Action<string>? log = null, string? cacheRoot = null)
    {
        log ??= Console.WriteLine;
        string key = Key(pac);
        name = Regex.Replace(name ?? System.IO.Path.GetFileNameWithoutExtension(pac), @"[^A-Za-z0-9_ .()-]+", "_").Trim();
        if (name.Length == 0) name = "wrestler";
        string root = System.IO.Path.Combine(cacheRoot ?? CacheRoot, key), chr = System.IO.Path.Combine(root, name), done = System.IO.Path.Combine(root, ".done");
        if (File.Exists(done) && Directory.Exists(chr)) return chr;
        log($"unpacking {System.IO.Path.GetFileName(pac)} (WWE 2K19 - 2K22 pack)...");
        var blobs = Members(pac);
        foreach (var tx in TexturePacks(pac))       // (2K19 / 2K20: the textures can sit in txNNNN.pac)
        {
            log($"  + textures from {System.IO.Path.GetFileName(tx)}");
            blobs.AddRange(Members(tx));
        }
        var models = blobs.Where(b => b.Length >= 4 && (b.AsSpan(0, 4).SequenceEqual("JBOY"u8) || b.AsSpan(0, 4).SequenceEqual("YOBJ"u8))).ToList();
        var tex = new Dictionary<string, byte[]>();
        foreach (var b in blobs) if (TexturesOf(b) is { } t) foreach (var kv in t) tex[kv.Key] = kv.Value;
        if (models.Count == 0) throw new InvalidDataException($"{System.IO.Path.GetFileName(pac)}: no model inside (not a WWE 2K19 - 2K22 character pack?)");
        Directory.CreateDirectory(root);
        string modelFile = System.IO.Path.Combine(root, "models.bin");
        using (var f = File.Create(modelFile))
            foreach (var b in models) { f.Write(BitConverter.GetBytes((long)b.Length)); f.Write(b); }
        var whole = ModelFile(System.IO.Path.GetFullPath(modelFile));
        foreach (var (part, folder, fname) in new[] { ("base", "BaseModel", "baseModel.mdl"), ("attire", "Attire", "Attire.mdl") })
        {
            string d = System.IO.Path.Combine(chr, folder);
            Directory.CreateDirectory(System.IO.Path.Combine(d, "Textures"));
            var json = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["model"] = System.IO.Path.GetRelativePath(d, modelFile), ["part"] = part, ["pac"] = System.IO.Path.GetFullPath(pac),
            });
            File.WriteAllBytes(System.IO.Path.Combine(d, fname), [.. "PJBY"u8, .. Encoding.UTF8.GetBytes(json)]);
            foreach (var me in whole.Meshes)
            {
                string stem = StemOf(me);
                foreach (var (src, role) in new[] { (me.Diffuse, "color"), (me.Textures.GetValueOrDefault("g_mTNmlMapPrm", ""), "nrm") })
                    if (stem.Length > 0 && src.Length > 0 && tex.TryGetValue(src.ToLowerInvariant(), out var dds))
                    {
                        string o = System.IO.Path.Combine(d, "Textures", $"{stem}_{role}.dds");
                        if (!File.Exists(o)) File.WriteAllBytes(o, dds);
                    }
            }
        }
        File.WriteAllText(done, pac);
        return chr;
    }

    /// <summary>a woman's model: the women's 12699-vertex body</summary>
    public static bool LooksFemale(string modelPath)
    {
        try
        {
            var raw = File.ReadAllBytes(modelPath);
            using var info = JsonDocument.Parse(raw.AsMemory(4));
            var whole = ModelFile(System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(modelPath)!, info.RootElement.GetProperty("model").GetString()!)));
            var body = whole.Meshes.FirstOrDefault(me => me.Name == "M_Body");
            return body != null && body.N == 12699;
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or ArgumentException) { return false; }
    }
}
