using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using PortIT.Engine.Util;

namespace PortIT.Engine.Readers;

/// <summary>
/// WWE 2K .mcd / .mdl models, every Visual Concepts MDL version (port of readers/mcd_parse.py - see its notes for the
/// layout): 0x1D 2K20, 0x20 2K22, 0x25 2K23, 0x28 2K24 (.mdl), 0x29 2K25, 0x2F 2K26 (.mcd). A "PJBY" pointer file (a
/// 2K19 - 2K22 pack's part) is filled by the pack reader instead.
/// Stream values are kept as numbers with their NumPy type (Dtype) - the same values as the Python reader's arrays.
/// </summary>
public sealed class Mcd
{
    public static readonly Dictionary<int, string> Versions = new() { [0x1D] = "2K20", [0x20] = "2K22", [0x25] = "2K23", [0x28] = "2K24", [0x29] = "2K25", [0x2F] = "2K26" };

    static readonly Dictionary<(string, string), (string Dt, int C)> Fmt = new()
    {
        [("R32_G32_B32", "float")] = ("<f4", 3), [("R32_G32", "float")] = ("<f4", 2), [("R32", "uint")] = ("<u4", 1),
        [("R8", "snorm")] = ("i1", 1), [("R8_G8_B8_A8", "unorm")] = ("u1", 4), [("R32_G32_B32_A32", "float")] = ("<f4", 4),
        [("R16_G16", "float")] = ("<f2", 2), [("R16_G16_B16_A16", "float")] = ("<f2", 4),
    };
    static readonly Dictionary<(string, string), (string Dt, int C)> Int = new()
    {
        [("R8_G8_B8_A8", "snorm")] = ("i1", 4), [("R8_G8_B8_A8", "uint")] = ("u1", 4), [("R8_G8_B8_A8", "sint")] = ("i1", 4),
        [("R16_G16_B16_A16", "uint")] = ("<u2", 4), [("R16_G16_B16_A16", "snorm")] = ("<i2", 4),
        [("R16_G16_B16_A16", "unorm")] = ("<u2", 4), [("R16_G16", "snorm")] = ("<i2", 2), [("R16_G16", "unorm")] = ("<u2", 2),
        [("R8", "unorm")] = ("u1", 1), [("R8", "uint")] = ("u1", 1),
    };
    static readonly Dictionary<string, int> Bytes = new()
    {
        ["R32_G32_B32_A32"] = 16, ["R32_G32_B32"] = 12, ["R32_G32"] = 8, ["R16_G16_B16_A16"] = 8, ["R32"] = 4, ["R8_G8_B8_A8"] = 4,
        ["R16_G16"] = 4, ["R10_G10_B10_A2"] = 4, ["R11_G11_B10"] = 4, ["R8_G8"] = 2, ["R16"] = 2, ["R8"] = 1, ["A8"] = 1,
    };
    static readonly string[] Vector = ["POSITION", "NORMAL", "TANGENT"];

    public sealed class Stream
    {
        public string Sem = "", Format = "", Type = "", Dtype = "";
        public int C;
        /// <summary>n x C values (exact for every stored type)</summary>
        public double[] V = [];
        public int N => C == 0 ? 0 : V.Length / C;
    }

    public sealed class Bone
    {
        public string Name = "";
        public int Parent, Flag, Jiggle = -1;
        public double[] T = new double[3], Euler = new double[3];
        public float[]? JiggleData;
        public double[,] Local = new double[4, 4], World = new double[4, 4];
    }

    public sealed class Shape
    {
        public string Name = "";
        public long[] Index = [];
        public double[] Delta = [];      // n x 3
        public uint[] Word1 = [];
    }

    public sealed class MeshBuf
    {
        public string Material = "", Object = "", Shader = "";
        public double[] Bbox = new double[6];
        public int Vertices, Influences;
        public List<Stream> Streams = [];
        public ushort[] SkinBones = [];   // n x Influences
        public double[] SkinWeights = []; // n x Influences
        public uint[] Remap = [];
        public List<Shape> Shapes = [];
        public byte[] Stats = [];
    }

    public sealed class LodMesh
    {
        public int Mesh;
        public int[] Triangles = [];      // n x 3, (b, a, c) swapped to (a, b, c) as the Python reader
        public List<uint[]> Subsets = []; // (material, first index, index count, vertex count)
    }

    public string Path = "";
    public byte[] Raw = [];
    public int Version;
    public string Game = "";
    public double[] Bbox = new double[6];
    public Dictionary<string, int> Chunks = [];
    public List<string> Strings = [];
    public List<Bone> Bones = [];
    public double BoneF0, BoneF1;
    public List<(string Name, uint Hash)> Objects = [];
    public List<ushort[]> Gtpt = [];
    public List<MeshBuf> Meshes = [];
    public List<List<LodMesh>> Lods = [];
    public int MeshEnd;
    public byte[] Trailer = [];

    static int A4(int x) => (x + 3) & ~3;
    uint U32(int o) => BinaryPrimitives.ReadUInt32LittleEndian(Raw.AsSpan(o));
    ushort U16(int o) => BinaryPrimitives.ReadUInt16LittleEndian(Raw.AsSpan(o));
    float F32(int o) => BinaryPrimitives.ReadSingleLittleEndian(Raw.AsSpan(o));

    public string S(int i) => i >= 0 && i < Strings.Count ? Strings[i] : "#" + i;

    public static Mcd Load(string path)
    {
        var raw = File.ReadAllBytes(path);
        var m = new Mcd { Path = path, Raw = raw };
        if (raw.AsSpan(0, 4).SequenceEqual("PJBY"u8))      // (a WWE 2K19 - 2K22 pack's body or attire)
        {
            YukesPack.Fill(m, path, raw);
            return m;
        }
        int mdl = raw.AsSpan().IndexOf("MDL!MDL!"u8);
        int p = mdl >= 0 ? mdl + 4 : raw.AsSpan(0, 4).SequenceEqual("MDL!"u8) ? 0 : -1;
        if (p < 0) throw new InvalidDataException("no MDL! block");
        m.Version = (int)m.U32(p + 4);
        if (!Versions.TryGetValue(m.Version, out var game)) throw new InvalidDataException($"model version 0x{m.Version:X} is not known (2K20 0x1D ... 2K26 0x2F)");
        m.Game = game;
        bool old = m.Version is 0x1D or 0x20;
        for (int i = 0; i < 6; i++) m.Bbox[i] = m.F32(p + (old ? 8 : 12) + 4 * i);
        int q = p + (old ? 32 : 36);
        while (q + 8 <= raw.Length)
        {
            string tag = Encoding.Latin1.GetString(raw, q, 4);
            uint size = m.U32(q + 4);
            if (tag == "END!") break;
            m.Chunks.TryAdd(tag, q);
            q += 8 + (int)size;
        }
        int t = m.Chunks["TEXT"];
        int count = (int)m.U32(t + 8), b0 = t + 12 + 4 * count;
        for (int i = 0; i < count; i++)
        {
            int s = b0 + (int)m.U32(t + 12 + 4 * i);
            int e = Array.IndexOf(raw, (byte)0, s);
            m.Strings.Add(Encoding.Latin1.GetString(raw, s, e - s));
        }
        m.ReadBones(m.Chunks.GetValueOrDefault("BONE", -1));
        m.ReadObjects(m.Chunks.GetValueOrDefault("MTL!", -1));
        m.ReadGtpt(m.Chunks.GetValueOrDefault("GtPt", -1));
        m.ReadMeshes(m.Chunks["MBfD"]);
        m.ReadLods(m.Chunks.GetValueOrDefault("LODs", -1));
        if (m.Version < 0x2F) m.Older();
        else if (m.Lods.Count > 0) m.SetObjects();
        return m;
    }

    /// <summary>each mesh's own material from its triangles (the subset with the most indices)</summary>
    void SetObjects()
    {
        for (int k = 0; k < Meshes.Count; k++)
        {
            var subs = (k < Lods[0].Count ? Lods[0][k].Subsets : []).Where(s => s[0] < Objects.Count).ToList();
            string obj = "";
            if (subs.Count > 0)
            {
                var best = subs[0];
                foreach (var s in subs) if (s[2] > best[2]) best = s;    // (max: the first of equals)
                obj = Objects[(int)best[0]].Name;
            }
            Meshes[k].Object = Regex.Replace(obj, "(:skinned)?$", "", RegexOptions.IgnoreCase);
        }
    }

    // ---- skeleton
    void ReadBones(int p)
    {
        if (p < 0) return;
        int size = (int)U32(p + 4), n, q, rec;
        if (Version >= 0x28)
        {
            BoneF0 = F32(p + 8); BoneF1 = F32(p + 12);
            n = (int)U32(p + 16); q = p + 24; rec = 33;
        }
        else if (Version == 0x25) { n = (size - 12) / 32; q = p + 20; rec = 32; }
        else { n = (int)U32(p + 8); q = p + 12; rec = 28; }
        for (int i = 0; i < n; i++)
        {
            var b = new Bone { Name = S(U16(q)), Parent = (short)U16(q + 2) };
            for (int c = 0; c < 3; c++) { b.T[c] = F32(q + 4 + 4 * c); b.Euler[c] = F32(q + 16 + 4 * c); }
            if (rec == 33) { b.Flag = Raw[q + 28]; b.Jiggle = BinaryPrimitives.ReadInt32LittleEndian(Raw.AsSpan(q + 29)); }
            q += rec;
            if (b.Jiggle != -1)
            {
                b.JiggleData = new float[8];
                for (int c = 0; c < 8; c++) b.JiggleData[c] = F32(q + 4 * c);
                q += 32;
            }
            Bones.Add(b);
        }
        if (q > p + 8 + size) throw new InvalidDataException("BONE overrun");
        foreach (var b in Bones)
        {
            var R = EulerMatrix(b.Euler);
            var M = new double[4, 4];
            for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) M[r, c] = R[r, c];
            for (int c = 0; c < 3; c++) M[3, c] = b.T[c];
            M[3, 3] = 1;
            b.Local = M;
            b.World = b.Parent < 0 ? M : MatMul(M, Bones[b.Parent].World);
        }
    }

    /// <summary>Rx(e0) Ry(e1) Rz(e2), row vectors (mcd_parse.euler_matrix)</summary>
    public static double[,] EulerMatrix(double[] e)
    {
        var o = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        for (int ax = 0; ax < 3; ax++)
        {
            double c = Math.Cos(e[ax]), s = Math.Sin(e[ax]);
            double[,] R = ax switch
            {
                0 => new double[,] { { 1, 0, 0 }, { 0, c, s }, { 0, -s, c } },
                1 => new double[,] { { c, 0, -s }, { 0, 1, 0 }, { s, 0, c } },
                _ => new double[,] { { c, s, 0 }, { -s, c, 0 }, { 0, 0, 1 } },
            };
            o = MatMul(o, R);
        }
        return o;
    }

    /// <summary>a @ b as NumPy / OpenBLAS do it here: fused multiply-add over k in order, from 0 (Util/Blas)</summary>
    public static double[,] MatMul(double[,] a, double[,] b)
    {
        int n = a.GetLength(0), m = b.GetLength(1), k = a.GetLength(1);
        var o = new double[n, m];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < m; j++)
            {
                double s = 0.0;
                for (int x = 0; x < k; x++) s = Math.FusedMultiplyAdd(a[i, x], b[x, j], s);
                o[i, j] = s;
            }
        return o;
    }

    void ReadObjects(int p)
    {
        if (p < 0) return;
        int n = (int)U32(p + 8);
        for (int i = 0; i < n; i++)
        {
            if (Version >= 0x2F) Objects.Add((S(U16(p + 12 + 6 * i)), U32(p + 14 + 6 * i)));
            else Objects.Add((S((int)U32(p + 12 + 4 * i)), 0));
        }
    }

    void ReadGtpt(int p)
    {
        if (p < 0) return;
        int n = (int)U32(p + 8);
        for (int i = 0; i < n; i++)
            Gtpt.Add([U16(p + 12 + 8 * i), U16(p + 14 + 8 * i), U16(p + 16 + 8 * i), U16(p + 18 + 8 * i)]);
    }

    // ---- mesh buffers
    (Stream, int) ReadStream(int q, int n, int k)
    {
        string sem = S(U16(q + 12)), fmt = S(U16(q + 14)), typ = S(U16(q + 16));
        if (!Bytes.TryGetValue(fmt, out int bytes)) throw new InvalidDataException($"mesh {k}: unknown stream format ({fmt}, {typ})");
        int d = A4(q + 20);
        var st = new Stream { Sem = sem, Format = fmt, Type = typ };
        if (Fmt.TryGetValue((fmt, typ), out var f))
        {
            st.Dtype = f.Dt; st.C = f.C;
            st.V = ReadValues(d, n * f.C, f.Dt);
        }
        else if (Int.TryGetValue((fmt, typ), out var it))
        {
            st.Dtype = it.Dt; st.C = it.C;
            st.V = ReadValues(d, n * it.C, it.Dt);
            if (Vector.Contains(sem) || sem == "TEXCOORD")
            {
                // (integer data as floats: float32 division, as NumPy's a.astype(float32) / scale)
                float scale = it.Dt switch { "i1" => 127f, "<i2" => 32767f, "u1" => 255f, _ => 65535f };
                int keep = Vector.Contains(sem) ? 3 : 2;
                var v = new double[n * keep];
                for (int i = 0; i < n; i++)
                    for (int c = 0; c < keep; c++) v[i * keep + c] = (float)st.V[i * it.C + c] / scale;
                st.V = v; st.C = keep; st.Dtype = "<f4";
                (st.Format, st.Type) = Vector.Contains(sem) ? ("R32_G32_B32", "float") : ("R32_G32", "float");
            }
        }
        else
        {
            st.Dtype = "u1"; st.C = bytes;
            st.V = ReadValues(d, n * bytes, "u1");
        }
        return (st, A4(d + n * bytes));
    }

    double[] ReadValues(int o, int count, string dt)
    {
        var v = new double[count];
        var s = Raw.AsSpan(o);
        switch (dt)
        {
            case "<f4": for (int i = 0; i < count; i++) v[i] = BinaryPrimitives.ReadSingleLittleEndian(s[(4 * i)..]); break;
            case "<f2": for (int i = 0; i < count; i++) v[i] = (double)BinaryPrimitives.ReadHalfLittleEndian(s[(2 * i)..]); break;
            case "<u4": for (int i = 0; i < count; i++) v[i] = BinaryPrimitives.ReadUInt32LittleEndian(s[(4 * i)..]); break;
            case "<u2": for (int i = 0; i < count; i++) v[i] = BinaryPrimitives.ReadUInt16LittleEndian(s[(2 * i)..]); break;
            case "<i2": for (int i = 0; i < count; i++) v[i] = BinaryPrimitives.ReadInt16LittleEndian(s[(2 * i)..]); break;
            case "i1": for (int i = 0; i < count; i++) v[i] = (sbyte)s[i]; break;
            default: for (int i = 0; i < count; i++) v[i] = s[i]; break;
        }
        return v;
    }

    void ReadMeshes(int p)
    {
        int nmesh = (int)U32(p + 8), q = p + 12;
        int head = Version >= 0x29 ? 48 : Version == 0x1D ? 44 : 46;
        for (int k = 0; k < nmesh; k++)
        {
            var mb = new MeshBuf { Material = S((int)U32(q)) };
            for (int i = 0; i < 6; i++) mb.Bbox[i] = F32(q + head - 32 + 4 * i);
            int n = (int)U32(q + head - 8), ns = (int)U32(q + head - 4);
            mb.Vertices = n;
            q += head;
            for (int j = 0; j < ns; j++)
            {
                var (st, nq) = ReadStream(q, n, k);
                mb.Streams.Add(st);
                q = nq;
            }
            if (Version >= 0x25)
            {
                int inf = U16(q + 2);
                mb.Influences = inf;
                mb.SkinBones = new ushort[n * inf];
                for (int i = 0; i < n * inf; i++) mb.SkinBones[i] = U16(q + 4 + 2 * i);
                q = A4(q + 4 + 2 * n * inf);
                mb.SkinWeights = new double[n * inf];
                for (int i = 0; i < n * inf; i++) mb.SkinWeights[i] = Raw[q + i] / 255.0;
                q = A4(q + n * inf);
            }
            else
            {
                // (2K20 / 2K22: the skin is two of the streams, 4 bones per vertex)
                var bi = mb.Streams.LastOrDefault(s => s.Sem == "BLENDINDICES");
                var bw = mb.Streams.LastOrDefault(s => s.Sem == "BLENDWEIGHTS");
                if (bi == null || bw == null)
                {
                    mb.Influences = 1;
                    mb.SkinBones = new ushort[n];
                    mb.SkinWeights = Enumerable.Repeat(1.0, n).ToArray();
                }
                else
                {
                    mb.Influences = bi.C;
                    mb.SkinBones = bi.V.Select(x => (ushort)x).ToArray();
                    mb.SkinWeights = bw.Dtype == "u1" ? bw.V.Select(x => x / 255.0).ToArray() : (double[])bw.V.Clone();
                }
            }
            mb.Remap = new uint[n];
            for (int i = 0; i < n; i++) mb.Remap[i] = U32(q + 4 * i);
            q += 4 * n;
            if (Version >= 0x20)
            {
                int nsh = (int)U32(q);
                var names = Enumerable.Range(0, nsh).Select(i => S(U16(q + 4 + 2 * i))).ToList();
                q = A4(q + 4 + 2 * nsh);
                if (nsh > 0) q = ReadShapes(mb, names, q);
            }
            int e = Raw.AsSpan(q).IndexOf("ENDM"u8) + q;
            mb.Stats = Raw[q..e];
            q = e + 8;
            Meshes.Add(mb);
        }
        MeshEnd = q;
    }

    int ReadShapes(MeshBuf mb, List<string> names, int q)
    {
        int nv = (int)U32(q);
        double[] scale = [F32(q + 4), F32(q + 8), F32(q + 12)], mn = [F32(q + 16), F32(q + 20), F32(q + 24)];
        int dsize = (int)U32(q + 28), w0 = q + 32;
        uint W(int i) => U32(w0 + 4 * i);
        int nblk = (nv + 31) / 32;
        for (int j = 0; j < names.Count; j++)
        {
            var idx = new List<long>();
            var words = new List<(uint, uint)>();
            for (int b = 0; b < nblk; b++)
            {
                uint mask = W(2 * nblk * j + 2 * b), off = W(2 * nblk * j + 2 * b + 1);
                int cnt = 0;
                for (int i = 0; i < 32; i++) if (((mask >> i) & 1) != 0) { idx.Add(b * 32 + i); cnt++; }
                for (int i = 0; i < cnt; i++) words.Add((W((int)off + 2 * i), W((int)off + 2 * i + 1)));
            }
            var sh = new Shape { Name = names[j], Index = idx.ToArray(), Delta = new double[3 * words.Count], Word1 = new uint[words.Count] };
            for (int i = 0; i < words.Count; i++)
            {
                long a = words[i].Item1;
                long[] qz = [(a >> 21) & 2047, (a >> 10) & 2047, a & 1023];
                for (int c = 0; c < 3; c++) sh.Delta[3 * i + c] = mn[c] + qz[c] * scale[c];
                sh.Word1[i] = words[i].Item2;
            }
            mb.Shapes.Add(sh);
        }
        return q + 32 + dsize;
    }

    void ReadLods(int p)
    {
        if (p < 0) return;
        int nl = (int)U32(p + 8), q = p + 12;
        for (int lod = 0; lod < nl; lod++)
        {
            int nm = (int)U32(q);
            q += 4;
            var row = new List<LodMesh>();
            for (int k = 0; k < nm; k++)
            {
                int mesh = U16(q), n = (int)U32(q + 2);
                // (indices u16, or u32 when the mesh has more than 65535 vertices)
                int isz = mesh < Meshes.Count && Meshes[mesh].Vertices > 65535 ? 4 : 2;
                var tri = new int[n / 3 * 3];
                for (int i = 0; i < n / 3; i++)
                {
                    int o = q + 6 + isz * 3 * i;
                    int a = isz == 4 ? (int)U32(o) : U16(o), b = isz == 4 ? (int)U32(o + 4) : U16(o + 2), c = isz == 4 ? (int)U32(o + 8) : U16(o + 4);
                    tri[3 * i] = b; tri[3 * i + 1] = a; tri[3 * i + 2] = c;
                }
                q = A4(q + 6 + isz * n);
                int nsub = (int)U32(q);
                var subs = new List<uint[]>();
                if (Version >= 0x2F)
                {
                    for (int i = 0; i < nsub; i++) subs.Add([U32(q + 4 + 16 * i), U32(q + 8 + 16 * i), U32(q + 12 + 16 * i), U32(q + 16 + 16 * i)]);
                    q += 4 + 16 * nsub + 4;
                }
                else
                {
                    for (int i = 0; i < nsub; i++) subs.Add([U32(q + 4 + 12 * i), U32(q + 8 + 12 * i), U32(q + 12 + 12 * i), 0]);
                    q += 4 + 12 * nsub;
                }
                if (!Raw.AsSpan(q, 4).SequenceEqual("ENDM"u8)) throw new InvalidDataException("LOD record");
                q += 8;
                row.Add(new LodMesh { Mesh = mesh, Triangles = tri, Subsets = subs });
            }
            Lods.Add(row);
        }
        Trailer = Raw[q..];
    }

    // ---- 2K20 - 2K25 files made right for the 2K26 pipeline (mcd_parse._older)
    void Older()
    {
        foreach (var mb in Meshes)
        {
            int inf = mb.Influences;
            for (int v = 0; v < mb.Vertices; v++)
            {
                double s = Np.Sum(mb.SkinWeights.AsSpan(v * inf, inf));
                for (int c = 0; c < inf; c++) mb.SkinWeights[v * inf + c] = s > 0 ? mb.SkinWeights[v * inf + c] / s : 0.0;
            }
            // (2K19-era models in 2K23 files store V one whole tile up: a mesh inside one tile moved back by whole
            //  tiles; float32 arithmetic as NumPy's)
            foreach (var st in mb.Streams)
            {
                if (st.Sem != "TEXCOORD" || st.N == 0 || st.Dtype is not ("<f4" or "<f2")) continue;
                int c2 = st.C;
                var shift = new float[c2];
                bool any = false;
                bool half = st.Dtype == "<f2";
                // (NumPy computes in the array's own type: float16 arrays round every result to float16)
                float R(float x) => half ? (float)(Half)x : x;
                for (int c = 0; c < c2; c++)
                {
                    float mn = float.MaxValue, mx = float.MinValue;
                    for (int i = 0; i < st.N; i++) { float x = (float)st.V[i * c2 + c]; mn = Math.Min(mn, x); mx = Math.Max(mx, x); }
                    float eps = half ? (float)(Half)0.01 : 0.01f;   // (the Python float 0.01 cast to the array type)
                    float lo = MathF.Floor(R(mn + eps)), hi = MathF.Floor(R(mx - eps));
                    shift[c] = lo == hi ? lo : 0f;
                    any |= shift[c] != 0;
                }
                if (!any) continue;
                for (int i = 0; i < st.N; i++)
                    for (int c = 0; c < c2; c++)
                        st.V[i * c2 + c] = R((float)st.V[i * c2 + c] - shift[c]);
            }
        }
        if (Lods.Count == 0) return;
        SetObjects();
        // (2K25 base models carry a second, 112-vertex M_Head: the smaller ones become M_Head_Seam)
        var heads = Meshes.Where(mb => mb.Material == "M_Head").OrderBy(mb => -mb.Vertices).ToList();
        foreach (var mb in heads.Skip(1)) mb.Material = "M_Head_Seam";
        var names = new Dictionary<string, string>
        {
            ["head"] = "M_Head", ["body"] = "M_Body", ["eye"] = "M_EYE", ["eye_caruncle"] = "M_Head_Caruncle",
            ["eyelash"] = "M_transB_eyelash", ["eye_overlay"] = "M_transB_eyeshadow",
        };
        var generic = Enumerable.Range(0, Meshes.Count).Where(k => Regex.IsMatch(Meshes[k].Material, @"^Object\d*$")).ToList();
        var head = heads.FirstOrDefault();
        foreach (int k in generic)
        {
            string obj = Meshes[k].Object;
            string nw = names.TryGetValue(obj.ToLowerInvariant(), out var nn) ? nn
                : obj.Length > 0 ? "M_" + obj[..1].ToUpperInvariant() + obj[1..] : "M_Object" + k;
            Meshes[k].Material = nw;
            if (nw == "M_Head" && head == null) head = Meshes[k];
        }
        // (several meshes on the body material: the biggest is the body; tongue (its shapes) and teeth (in the head))
        var body = generic.Where(k => Meshes[k].Material == "M_Body").ToList();
        if (body.Count > 1)
        {
            body = body.OrderBy(k => -Meshes[k].Vertices).ToList();   // (stable, as Python's sort)
            foreach (int k in body.Skip(1))
            {
                var mb = Meshes[k];
                if (mb.Shapes.Any(s => s.Name.ToLowerInvariant().StartsWith("tongue"))) mb.Material = "M_Tongue";
                else if (head != null && Inside(Centre(Box(mb)), Box(head))) mb.Material = mb.Shapes.Count > 0 ? "M_MouthBag" : "M_Teeth";
                else mb.Material = "M_Body_" + k;
            }
        }
    }

    static double[] Box(MeshBuf mb)
    {
        var P = mb.Streams.FirstOrDefault(s => s.Sem == "POSITION");
        if (P == null || P.N == 0) return mb.Bbox;
        var b = new double[] { double.MaxValue, double.MaxValue, double.MaxValue, double.MinValue, double.MinValue, double.MinValue };
        for (int i = 0; i < P.N; i++)
            for (int c = 0; c < 3; c++) { double x = P.V[i * P.C + c]; b[c] = Math.Min(b[c], x); b[3 + c] = Math.Max(b[3 + c], x); }
        return b;
    }

    static double[] Centre(double[] b)
    {
        var c = new double[6];
        for (int i = 0; i < 3; i++) c[i] = c[i + 3] = (b[i] + b[i + 3]) / 2;
        return c;
    }

    static bool Inside(double[] a, double[] b)
    {
        for (int i = 0; i < 3; i++) if (a[i] < b[i] - 1e-3 || a[i + 3] > b[i + 3] + 1e-3) return false;
        return true;
    }

    // ---- helpers
    public Stream? StreamOf(int k, string sem) => Meshes[k].Streams.FirstOrDefault(s => s.Sem == sem);

    /// <summary>texture coordinates with V flipped back (1 - v, float32 as the Python reader)</summary>
    public float[]? Uvs(int k)
    {
        var a = StreamOf(k, "TEXCOORD");
        if (a == null) return null;
        var o = new float[a.N * 2];
        for (int i = 0; i < a.N; i++) { o[2 * i] = (float)a.V[i * a.C]; o[2 * i + 1] = 1.0f - (float)a.V[i * a.C + 1]; }
        return o;
    }

    public const int FemaleBodyVerts = 12699;   // (2K's women's body template; the men's are 12725 - 12763)

    /// <summary>a woman's base model by 2K's own data: its biggest mesh is the women's body template
    /// (mcd_parse.looks_female; a reworked body is not caught - the app's Male / Female choice stays the user's)</summary>
    public static bool LooksFemale(string path)
    {
        try
        {
            if (File.ReadAllBytes(path).AsSpan(0, 4).SequenceEqual("PJBY"u8)) return YukesPack.LooksFemale(path);
            var m = Load(path);
            return m.Meshes.Count > 0 && m.Meshes.Max(mb => mb.Vertices) == FemaleBodyVerts;
        }
        catch (Exception e) when (e is IOException or KeyNotFoundException or IndexOutOfRangeException or ArgumentException
                                       or InvalidDataException or NotSupportedException)
        {
            return false;
        }
    }
}
