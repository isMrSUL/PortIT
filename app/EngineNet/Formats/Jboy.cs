using System.Buffers.Binary;
using System.Text;

namespace PortIT.Engine.Formats;

/// <summary>
/// JBOY (Yuke's YOBJ, big-endian) models: read and write (port of svr/jboy.py). Pointers are offsets from byte 8,
/// listed in the POF0 table. "JBOY" u32 len | data[len] | "POF0" u32 len | pof bytes (4-aligned).
/// Mesh descriptor 0xB4 bytes; vertex 28 bytes (pos f32x3, normal f32x3, colour u32); weights 8 bytes per vertex per
/// block (u8 palette slots x4, f32); uv 8 bytes; strips {u32 prim, u32 count, -> u16[count]}; node 80 bytes.
/// </summary>
public sealed class Jboy
{
    const int B = 8;   // pointer base

    public sealed class Node
    {
        public string Name = "";
        // (doubles: the builder works on them at full precision, as the Python dicts hold floats; written as f32)
        public double[] T = new double[3], R = new double[3];
        public uint U40;
        public int U44;
        public uint[] U48 = [0xFFFFFFFF, 0, 0, 0];   // (its first word is the parent)
        public double[] Sphere = new double[4];

        public Node Clone() => new() { Name = Name, T = (double[])T.Clone(), R = (double[])R.Clone(), U40 = U40, U44 = U44,
                                       U48 = (uint[])U48.Clone(), Sphere = (double[])Sphere.Clone() };
        public int Parent => (int)U48[0];
    }

    public sealed class Param
    {
        public string Name = "";
        public ushort Type;
        public byte[] Value = [];
    }

    public sealed class Mesh
    {
        public byte[] Raw = new byte[0xB4];
        public int StripCount;                     // (+04)
        public List<int> Palette = [];
        public uint[] U5c = new uint[3];
        public uint U74;
        public string Shader = "";
        public uint U88, Vfmt, Ua0;
        public int Vc2;
        public float[] Sphere = new float[4];
        /// <summary>pos x, y, z, normal x, y, z (floats) and the ARGB colour, per vertex</summary>
        public List<(float[] PosNrm, uint Color)> Verts = [];
        /// <summary>per weight block: per vertex (4 palette slots, 255 = none; weight)</summary>
        public List<(byte[] Bones, float W)[]> Weights = [];
        public List<(float U, float V)> Uvs = [];
        public List<Param> Params = [];
        public List<(uint Prim, ushort[] Idx)> Strips = [];
    }

    public byte[] Header = new byte[0x40];       // file bytes 8..0x48
    public List<int> Pointers = [];
    public string Name = "";
    public byte[] Group = new byte[16];          // the 16 bytes after the object name
    public List<string> Textures = [];
    public List<Node> Nodes = [];
    public List<Mesh> Meshes = [];
    public int Size;

    static uint U32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32BigEndian(d[o..]);
    static int I32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadInt32BigEndian(d[o..]);
    static float F32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadSingleBigEndian(d[o..]);
    static ushort U16(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt16BigEndian(d[o..]);

    /// <summary>latin-1 keeps every byte (some names are Shift-JIS), so writes round-trip</summary>
    static string CStr(ReadOnlySpan<byte> b)
    {
        int z = b.IndexOf((byte)0);
        return Encoding.Latin1.GetString(z < 0 ? b : b[..z]);
    }

    static byte[] Name16(string n)
    {
        var b = new byte[16];
        var s = Encoding.Latin1.GetBytes(n);
        s.AsSpan(0, Math.Min(16, s.Length)).CopyTo(b);
        return b;
    }

    public static bool IsJboy(ReadOnlySpan<byte> d) => d.Length >= 8 && d[..4].SequenceEqual("JBOY"u8);

    // ---------------------------------------------------------------- POF0
    public static List<int> PofDecode(ReadOnlySpan<byte> b)
    {
        var offs = new List<int>();
        int cur = 0, i = 0;
        while (i < b.Length)
        {
            int t = b[i] >> 6;
            if (t == 0) break;
            int v;
            if (t == 1) { v = b[i] & 0x3F; i += 1; }
            else if (t == 2) { v = ((b[i] & 0x3F) << 8) | b[i + 1]; i += 2; }
            else { v = ((b[i] & 0x3F) << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3]; i += 4; }
            cur += v * 4;
            offs.Add(cur);
        }
        return offs;
    }

    public static byte[] PofEncode(IEnumerable<int> offsets)
    {
        var o = new MemoryStream();
        int cur = 0;
        foreach (int off in offsets.OrderBy(x => x))
        {
            int v = (off - cur) / 4;
            cur = off;
            if (v < 0x40) o.WriteByte((byte)(0x40 | v));
            else if (v < 0x4000) { o.WriteByte((byte)(0x80 | (v >> 8))); o.WriteByte((byte)(v & 0xFF)); }
            else
            {
                o.WriteByte((byte)(0xC0 | (v >> 24))); o.WriteByte((byte)((v >> 16) & 0xFF));
                o.WriteByte((byte)((v >> 8) & 0xFF)); o.WriteByte((byte)(v & 0xFF));
            }
        }
        while (o.Length % 4 != 0) o.WriteByte(0);   // (the zero pad doubles as the terminator)
        return o.ToArray();
    }

    // ---------------------------------------------------------------- read
    public static Jboy Read(byte[] data)
    {
        var d = data.AsSpan();
        if (!IsJboy(d)) throw new InvalidDataException("not JBOY");
        int ln = (int)U32(d, 4);
        var pof = d[(8 + ln)..];
        if (!pof[..4].SequenceEqual("POF0"u8)) throw new InvalidDataException("no POF0");
        var m = new Jboy { Pointers = PofDecode(pof.Slice(8, (int)U32(pof, 4))), Header = d[8..0x48].ToArray(), Size = 8 + ln };
        int nmesh = (int)U32(d, 0x18), mptr = (int)U32(d, 0x1C), nnode = (int)U32(d, 0x20), ntex = (int)U32(d, 0x24);
        int nptr = (int)U32(d, 0x28), tptr = (int)U32(d, 0x2C), optr = (int)U32(d, 0x30);
        m.Name = CStr(d.Slice(B + optr, 16));
        m.Group = d.Slice(B + optr + 16, 16).ToArray();
        for (int i = 0; i < ntex; i++) m.Textures.Add(CStr(d.Slice(B + tptr + 16 * i, 16)));
        for (int i = 0; i < nnode; i++)
        {
            int o = B + nptr + 80 * i;
            m.Nodes.Add(new Node
            {
                Name = CStr(d.Slice(o, 16)),
                T = [F32(d, o + 16), F32(d, o + 20), F32(d, o + 24)],
                R = [F32(d, o + 28), F32(d, o + 32), F32(d, o + 36)],
                U40 = U32(d, o + 40),
                U44 = I32(d, o + 44),
                U48 = [U32(d, o + 48), U32(d, o + 52), U32(d, o + 56), U32(d, o + 60)],
                Sphere = [F32(d, o + 64), F32(d, o + 68), F32(d, o + 72), F32(d, o + 76)],
            });
        }
        for (int i = 0; i < nmesh; i++)
        {
            int o = B + mptr + 0xB4 * i;
            var s = new Mesh { Raw = d.Slice(o, 0xB4).ToArray() };
            int vc = (int)U32(d, o);
            s.StripCount = (int)U32(d, o + 4);
            int npal = (int)U32(d, o + 8);
            for (int k = 0; k < Math.Min(npal, 20); k++) s.Palette.Add(I32(d, o + 0x0C + 4 * k));
            s.U5c = [U32(d, o + 0x5C), U32(d, o + 0x60), U32(d, o + 0x64)];
            int vblk = (int)U32(d, o + 0x68), wptr = (int)U32(d, o + 0x6C), uvptr = (int)U32(d, o + 0x70);
            s.U74 = U32(d, o + 0x74);
            s.Shader = CStr(d.Slice(o + 0x78, 16));
            s.U88 = U32(d, o + 0x88);
            s.Vfmt = U32(d, o + 0x8C);
            int npar = (int)U32(d, o + 0x90), parptr = (int)U32(d, o + 0x94), iptr = (int)U32(d, o + 0x98);
            s.Vc2 = (int)U32(d, o + 0x9C);
            s.Ua0 = U32(d, o + 0xA0);
            s.Sphere = [F32(d, o + 0xA4), F32(d, o + 0xA8), F32(d, o + 0xAC), F32(d, o + 0xB0)];
            int vdat = B + (int)U32(d, B + vblk);
            for (int k = 0; k < vc; k++)
            {
                int v = vdat + 28 * k;
                s.Verts.Add(([F32(d, v), F32(d, v + 4), F32(d, v + 8), F32(d, v + 12), F32(d, v + 16), F32(d, v + 20)], U32(d, v + 24)));
            }
            int nw = Math.Max(1, (int)s.U5c[0]);
            for (int j = 0; j < nw; j++)
            {
                int wb = B + wptr + 8 * vc * j;
                var blk = new (byte[], float)[vc];
                for (int k = 0; k < vc; k++) blk[k] = (d.Slice(wb + 8 * k, 4).ToArray(), F32(d, wb + 8 * k + 4));
                s.Weights.Add(blk);
            }
            for (int k = 0; k < vc; k++) s.Uvs.Add((F32(d, B + uvptr + 8 * k), F32(d, B + uvptr + 8 * k + 4)));
            for (int k = 0; k < npar; k++)
            {
                int po = B + (int)U32(d, B + parptr + 4 * k);
                ushort typ = U16(d, po + 16), size = U16(d, po + 18);
                s.Params.Add(new Param { Name = CStr(d.Slice(po, 16)), Type = typ, Value = d[(po + 20)..(po + size)].ToArray() });
            }
            for (int k = 0; k < s.StripCount; k++)
            {
                int ih = B + iptr + 12 * k;
                uint prim = U32(d, ih);
                int icount = (int)U32(d, ih + 4), idat = B + (int)U32(d, ih + 8);
                var idx = new ushort[icount];
                for (int q = 0; q < icount; q++) idx[q] = U16(d, idat + 2 * q);
                s.Strips.Add((prim, idx));
            }
            m.Meshes.Add(s);
        }
        return m;
    }

    /// <summary>a triangle strip (degenerate-joined) -> a triangle list</summary>
    public static List<(int A, int B, int C)> StripToTris(IReadOnlyList<ushort> idx)
    {
        var tris = new List<(int, int, int)>();
        for (int k = 0; k + 2 < idx.Count; k++)
        {
            int a = idx[k], b = idx[k + 1], c = idx[k + 2];
            if (a == b || b == c || a == c) continue;
            tris.Add(k % 2 == 0 ? (a, b, c) : (b, a, c));
        }
        return tris;
    }

    // ---------------------------------------------------------------- write
    /// <summary>the model -> JBOY bytes. Yuke's layout: header, mesh descriptors, then per mesh [param pointers, params,
    /// vertex header, vertices, weights, uvs, index headers, indices], a 16-byte pad, nodes, texture names, the group
    /// record. Padding is zero (the originals hold junk there).</summary>
    public byte[] Write()
    {
        var o = new Buf();
        o.Grow(0x48);
        var ptrs = new List<int>();
        void PutPtr(int at, int target) { o.U32(at, (uint)(target - B)); ptrs.Add(at); }

        int nm = Meshes.Count;
        int meshAt = o.Len;
        o.Grow(0xB4 * nm);
        for (int i = 0; i < nm; i++)
        {
            var s = Meshes[i];
            var md = (byte[])s.Raw.Clone();
            int vc = s.Verts.Count, mdAt = meshAt + 0xB4 * i;
            int parList = o.Len;
            o.Grow(4 * s.Params.Count);
            var parAt = new List<int>();
            foreach (var p in s.Params)
            {
                parAt.Add(o.Len);
                o.Add(Name16(p.Name));
                o.AddU16(p.Type); o.AddU16((ushort)(20 + p.Value.Length)); o.Add(p.Value);
            }
            int vh = o.Len;
            o.Grow(4);
            int vd = o.Len;
            foreach (var (pn, col) in s.Verts) { foreach (var f in pn) o.AddF32(f); o.AddU32(col); }
            int wd = o.Len;
            foreach (var blk in s.Weights) foreach (var (bones, w) in blk) { o.Add(bones); o.AddF32(w); }
            int ud = o.Len;
            foreach (var (u, v) in s.Uvs) { o.AddF32(u); o.AddF32(v); }
            int ih = o.Len;
            o.Grow(12 * s.Strips.Count);
            var idAt = new List<int>();
            foreach (var (_, idx) in s.Strips) { idAt.Add(o.Len); foreach (var x in idx) o.AddU16(x); }
            o.PadTo(4, 0);
            // descriptor
            BinaryPrimitives.WriteUInt32BigEndian(md.AsSpan(0), (uint)vc);
            BinaryPrimitives.WriteUInt32BigEndian(md.AsSpan(4), (uint)s.Strips.Count);
            BinaryPrimitives.WriteUInt32BigEndian(md.AsSpan(8), (uint)s.Palette.Count);
            for (int k = 0; k < 20; k++)
                BinaryPrimitives.WriteInt32BigEndian(md.AsSpan(0x0C + 4 * k), k < s.Palette.Count ? s.Palette[k] : -1);
            BinaryPrimitives.WriteUInt32BigEndian(md.AsSpan(0x5C), (uint)s.Weights.Count);
            BinaryPrimitives.WriteUInt32BigEndian(md.AsSpan(0x74), s.U74);
            Name16(s.Shader).CopyTo(md, 0x78);
            BinaryPrimitives.WriteUInt32BigEndian(md.AsSpan(0x90), (uint)s.Params.Count);
            BinaryPrimitives.WriteUInt32BigEndian(md.AsSpan(0x9C), (uint)vc);
            o.Set(mdAt, md);
            PutPtr(mdAt + 0x68, vh);
            PutPtr(mdAt + 0x6C, wd);
            PutPtr(mdAt + 0x70, ud);
            PutPtr(mdAt + 0x94, parList);
            PutPtr(mdAt + 0x98, ih);
            PutPtr(vh, vd);
            for (int k = 0; k < parAt.Count; k++) PutPtr(parList + 4 * k, parAt[k]);
            for (int k = 0; k < s.Strips.Count; k++)
            {
                o.U32(ih + 12 * k, s.Strips[k].Prim);
                o.U32(ih + 12 * k + 4, (uint)s.Strips[k].Idx.Length);
                PutPtr(ih + 12 * k + 8, idAt[k]);
            }
        }
        while ((o.Len - B) % 16 != 0) o.AddByte(0);
        int nodeAt = o.Len;
        foreach (var n in Nodes)
        {
            o.Add(Name16(n.Name));
            foreach (var f in n.T) o.AddF32((float)f);
            foreach (var f in n.R) o.AddF32((float)f);
            o.AddU32(n.U40); o.AddU32((uint)n.U44);
            foreach (var u in n.U48) o.AddU32(u);
            foreach (var f in n.Sphere) o.AddF32((float)f);
        }
        int texAt = o.Len;
        foreach (var t in Textures) o.Add(Name16(t));
        int grpAt = o.Len;
        o.Add(Name16(Name)); o.Add(Group);
        o.Set(8, Header);
        int ln = o.Len - 8;
        o.Set(0, "JBOY"u8.ToArray());
        o.U32(4, (uint)ln); o.U32(8, 0); o.U32(12, (uint)ln);
        o.U32(0x18, (uint)nm);
        PutPtr(0x1C, meshAt);
        o.U32(0x20, (uint)Nodes.Count); o.U32(0x24, (uint)Textures.Count);
        PutPtr(0x28, nodeAt);
        PutPtr(0x2C, texAt);
        PutPtr(0x30, grpAt);
        var pof = PofEncode(ptrs.Select(p => p - B));
        o.Add("POF0"u8.ToArray());
        o.AddU32((uint)pof.Length);
        o.Add(pof);
        return o.ToArray();
    }

    /// <summary>a growable big-endian byte buffer</summary>
    sealed class Buf
    {
        byte[] b = new byte[1 << 16];
        public int Len;
        void Need(int n) { if (Len + n > b.Length) Array.Resize(ref b, Math.Max(b.Length * 2, Len + n)); }
        public void Grow(int n) { Need(n); Len += n; }
        public void AddByte(byte v) { Need(1); b[Len++] = v; }
        public void Add(ReadOnlySpan<byte> v) { Need(v.Length); v.CopyTo(b.AsSpan(Len)); Len += v.Length; }
        public void AddU16(ushort v) { Need(2); BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(Len), v); Len += 2; }
        public void AddU32(uint v) { Need(4); BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(Len), v); Len += 4; }
        public void AddF32(float v) { Need(4); BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(Len), v); Len += 4; }
        public void U32(int at, uint v) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at), v);
        public void Set(int at, ReadOnlySpan<byte> v) => v.CopyTo(b.AsSpan(at));
        public void PadTo(int align, byte v) { while (Len % align != 0) AddByte(v); }
        public byte[] ToArray() => b.AsSpan(0, Len).ToArray();
    }
}
