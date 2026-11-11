using System.Buffers.Binary;
using PortIT.Engine.Imaging;

namespace PortIT.Engine.Readers;

/// <summary>AEW Fight Forever (UE 4.27, PS4) cooked USkeletalMesh, LOD 0 (port of readers/aew_mesh.py)</summary>
public sealed class AewMesh
{
    public sealed class Material { public int Index; public string Slot = ""; public string? Imported; public float[] UvDensity = new float[4]; }

    public sealed class Bone { public string Name = ""; public int Parent; public float[] Q = new float[4], T = new float[3], S = new float[3]; }

    public sealed class Section
    {
        public int Flag, Material, BaseIndex, Triangles, RecomputeTangent, TangentMask, CastShadow, BaseVertex;
        public int Vertices, MaxInfluences, ClothAsset, ClothLod, Disabled;
        public byte[] ClothMapping = [];
        public ushort[] BoneMap = [];
        public uint[] DupVertData = [], DupVertIndex = [];
    }

    public sealed class Lod
    {
        public int CookedOut, Inlined;
        public ushort[] RequiredBones = [], ActiveBones = [];
        public List<Section> Sections = [];
        public uint BuffersSize;
        public bool HasData;
        public long[] Indices = [];
        public float[] Positions = [];      // n x 3
        public float[] TangentX = [], TangentZ = [];   // n x 4 (float32 / 127 or / 32767)
        public float[] Uvs = [];            // n x ntc x 2
        public int UvChannels;
        public int[] SkinSlots = [];        // n x maxinf
        public float[] SkinWeights = [];    // n x maxinf (float32 / 255)
        public int MaxInfluences, Variable, Use16;
    }

    public Dictionary<string, object?> Props = [];
    public float[] Bounds = new float[7];
    public List<Material> Materials = [];
    public List<Bone> Bones = [];
    public List<Lod> Lods = [];

    public static AewMesh Read(string path)
    {
        var pkg = new ZenPackage(File.ReadAllBytes(path));
        string bas = System.IO.Path.GetFileNameWithoutExtension(path);
        var ed = pkg.ExportData();
        var hit = ed.FirstOrDefault(x => x.E.Name == bas);
        if (hit.E == null) hit = ed[^1];                         // (Python's loop leaves the last export)
        var pr = new ZenReader(pkg, hit.Start, hit.End);
        var m = new AewMesh { Props = pr.Props() };
        var r = pr;
        if (r.I32() != 0) r.O += 16;
        r.U8(); r.U8();
        for (int i = 0; i < 7; i++) m.Bounds[i] = r.F32();
        int nm = r.I32();
        for (int i = 0; i < nm; i++)
        {
            var mt = new Material { Index = r.I32(), Slot = r.Name() };
            int serImp = r.I32();
            mt.Imported = serImp != 0 ? r.Name() : null;
            r.I32(); r.I32();
            for (int k = 0; k < 4; k++) mt.UvDensity[k] = r.F32();
            m.Materials.Add(mt);
        }
        int nb = r.I32();
        for (int i = 0; i < nb; i++) m.Bones.Add(new Bone { Name = r.Name(), Parent = r.I32() });
        int np = r.I32();
        for (int i = 0; i < np; i++)
        {
            var q = new[] { r.F32(), r.F32(), r.F32(), r.F32() };
            var t = new[] { r.F32(), r.F32(), r.F32() };
            var s = new[] { r.F32(), r.F32(), r.F32() };
            if (i < m.Bones.Count) { m.Bones[i].Q = q; m.Bones[i].T = t; m.Bones[i].S = s; }
        }
        int nmap = r.I32();
        r.O += nmap * 12;
        r.I32();                                                // (cooked)
        int nlod = r.I32();
        for (int li = 0; li < nlod; li++)
        {
            var lod = new Lod();
            r.U8(); r.U8();
            lod.CookedOut = r.I32(); lod.Inlined = r.I32();
            lod.RequiredBones = U16Arr(r, r.I32());
            if (lod.CookedOut != 0) { m.Lods.Add(lod); continue; }
            int ns = r.I32();
            for (int si = 0; si < ns; si++)
            {
                var s = new Section();
                s.Flag = BinaryPrimitives.ReadUInt16LittleEndian(pkg.D.AsSpan(r.O)); s.Material = BinaryPrimitives.ReadUInt16LittleEndian(pkg.D.AsSpan(r.O + 2)); r.O += 4;
                s.BaseIndex = (int)r.U32(); s.Triangles = (int)r.U32();
                s.RecomputeTangent = r.I32();
                s.TangentMask = r.U8();
                s.CastShadow = r.I32(); s.BaseVertex = r.I32();
                int ncm = r.I32();
                s.ClothMapping = pkg.D.AsSpan(r.O, ncm * 64).ToArray(); r.O += ncm * 64;
                s.BoneMap = U16Arr(r, r.I32());
                s.Vertices = r.I32(); s.MaxInfluences = r.I32();
                s.ClothAsset = BinaryPrimitives.ReadInt16LittleEndian(pkg.D.AsSpan(r.O)); r.O += 2;
                r.O += 16;
                s.ClothLod = r.I32();
                s.DupVertData = U32Arr(r, r.I32());
                s.DupVertIndex = U32Arr(r, r.I32() * 2);
                s.Disabled = r.I32();
                lod.Sections.Add(s);
            }
            lod.ActiveBones = U16Arr(r, r.I32());
            lod.BuffersSize = r.U32();
            if (lod.Inlined != 0) Streamables(r, pkg.D, lod);
            m.Lods.Add(lod);
            break;   // (LOD 0 is all a port needs)
        }
        return m;
    }

    static ushort[] U16Arr(ZenReader r, int n)
    {
        var o = new ushort[n];
        for (int i = 0; i < n; i++) o[i] = BinaryPrimitives.ReadUInt16LittleEndian(r.Data.AsSpan(r.O + 2 * i));
        r.O += 2 * n;
        return o;
    }

    static uint[] U32Arr(ZenReader r, int n)
    {
        var o = new uint[n];
        for (int i = 0; i < n; i++) o[i] = r.U32();
        return o;
    }

    static (int Es, int N, int At) Bulk(ZenReader r)
    {
        int es = r.I32(), n = r.I32();
        int at = r.O;
        r.O += es * n;
        return (es, n, at);
    }

    static void Streamables(ZenReader r, byte[] d, Lod lod)
    {
        r.U8(); r.U8();
        int isz = r.U8();
        var (es, n, at) = Bulk(r);
        int cnt = es * n / isz;
        lod.Indices = new long[cnt];
        for (int i = 0; i < cnt; i++) lod.Indices[i] = isz == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at + 4 * i)) : isz == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at + 2 * i)) : d[at + i];
        r.U32(); r.U32();
        (es, n, at) = Bulk(r);
        lod.Positions = new float[es * n / 4];
        for (int i = 0; i < lod.Positions.Length; i++) lod.Positions[i] = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(at + 4 * i));
        r.U8(); r.U8();
        int ntc = (int)r.U32(); r.U32();
        int fullUv = r.I32(), highTan = r.I32();
        (es, n, at) = Bulk(r);
        int comp = highTan != 0 ? es / 2 : es;
        var t = new float[n * comp];
        for (int i = 0; i < n * comp; i++)
            t[i] = highTan != 0 ? BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(at + 2 * i)) / 32767.0f : (sbyte)d[at + i] / 127.0f;
        lod.TangentX = new float[n * 4]; lod.TangentZ = new float[n * 4];
        for (int v = 0; v < n; v++) for (int c = 0; c < 4; c++) { lod.TangentX[4 * v + c] = t[v * comp + c]; lod.TangentZ[4 * v + c] = t[v * comp + 4 + c]; }
        (es, n, at) = Bulk(r);
        lod.UvChannels = ntc;
        int nf = es * n / (fullUv != 0 ? 4 : 2);
        lod.Uvs = new float[nf];
        for (int i = 0; i < nf; i++) lod.Uvs[i] = fullUv != 0 ? BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(at + 4 * i)) : (float)BinaryPrimitives.ReadHalfLittleEndian(d.AsSpan(at + 2 * i));
        r.U8(); r.U8();
        lod.Variable = r.I32(); int maxinf = r.I32(); r.U32(); int nverts = (int)r.U32();
        lod.MaxInfluences = maxinf;
        lod.Use16 = r.I32();
        (es, n, at) = Bulk(r);
        int bsz = lod.Use16 != 0 ? 2 : 1, rec = maxinf * (bsz + 1);
        lod.SkinSlots = new int[nverts * maxinf]; lod.SkinWeights = new float[nverts * maxinf];
        for (int v = 0; v < nverts; v++)
            for (int k = 0; k < maxinf; k++)
            {
                int o = at + v * rec;
                lod.SkinSlots[v * maxinf + k] = bsz == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o + 2 * k)) : d[o + k];
                lod.SkinWeights[v * maxinf + k] = d[o + maxinf * bsz + k] / 255.0f;
            }
        lod.HasData = true;
    }

    /// <summary>per-vertex skin slots -&gt; skeleton bone indices through each section's bone map (aew_mesh.global_bones)</summary>
    public static int[] GlobalBones(Lod lod)
    {
        int nv = lod.Positions.Length / 3, mi = lod.MaxInfluences;
        var o = new int[lod.SkinSlots.Length];
        foreach (var s in lod.Sections)
            for (int v = s.BaseVertex; v < s.BaseVertex + s.Vertices && v < nv; v++)
                for (int k = 0; k < mi; k++)
                    o[v * mi + k] = s.BoneMap.Length == 0 ? 0 : s.BoneMap[Math.Min(lod.SkinSlots[v * mi + k], s.BoneMap.Length - 1)];
        return o;
    }
}

/// <summary>AEW (UE 4.27, PS4) Texture2D: header and mips from .uasset / .ubulk, PS4 un-tiling (port of aew_tex.py)</summary>
public sealed class AewTexture
{
    public static readonly Dictionary<string, (int Bpb, int Bd)> Block = new()
    {
        ["PF_DXT1"] = (8, 4), ["PF_DXT5"] = (16, 4), ["PF_BC5"] = (16, 4), ["PF_BC4"] = (8, 4), ["PF_BC7"] = (16, 4),
        ["PF_B8G8R8A8"] = (4, 1), ["PF_G8"] = (1, 1), ["PF_BC6H"] = (16, 4),
    };

    public sealed class Mip { public uint Flags; public int Count, Size, W, H; public long Offset; public string Src = ""; public long SrcOff; public int SrcSize; }

    public Dictionary<string, object?> Props = [];
    public string Fmt = "", Ubulk = "";
    public int W, H;
    public List<Mip> Mips = [];
    ZenPackage pkg = null!;

    public static AewTexture Read(string path)
    {
        var pkg = new ZenPackage(File.ReadAllBytes(path));
        string bas = System.IO.Path.GetFileNameWithoutExtension(path);
        var ed = pkg.ExportData();
        var hit = ed.FirstOrDefault(x => x.E.Name == bas);
        if (hit.E == null) hit = ed[^1];
        var r = new ZenReader(pkg, hit.Start, hit.End);
        var t = new AewTexture { Props = r.Props(), pkg = pkg, Ubulk = System.IO.Path.ChangeExtension(path, ".ubulk") };
        var d = pkg.D;
        int o = r.O + 4 + 2 + 2 + 4;
        o += 8;                                                           // (pixel format FName)
        o += 8;                                                           // (SkipOffset)
        t.W = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o)); t.H = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o + 4));
        o += 12;
        int n = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o));
        t.Fmt = System.Text.Encoding.Latin1.GetString(d, o + 4, n - 1);
        o += 4 + n;
        int nmips = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o + 4));
        o += 8;
        for (int i = 0; i < nmips; i++)
        {
            o += 4;
            var m = new Mip { Flags = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o)), Count = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o + 4)) };
            o += 8;
            m.Size = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o)); m.Offset = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(o + 4));
            o += 12;
            if ((m.Flags & 0x20) != 0) { m.Src = "none"; }
            else if ((m.Flags & 0x100) != 0) { m.Src = "ubulk"; m.SrcOff = m.Offset; m.SrcSize = m.Size; }
            else { m.Src = "inline"; m.SrcOff = o; m.SrcSize = m.Size; o += m.Size; }
            m.W = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o)); m.H = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o + 4));
            o += 12;
            t.Mips.Add(m);
        }
        return t;
    }

    public byte[] MipBytes(int i)
    {
        var m = Mips[i];
        if (m.Src == "inline") return pkg.D.AsSpan((int)m.SrcOff, m.SrcSize).ToArray();
        using var f = File.OpenRead(Ubulk);
        f.Seek(m.SrcOff, SeekOrigin.Begin);
        var b = new byte[m.SrcSize];
        int got = 0;
        while (got < b.Length) { int k = f.Read(b, got, b.Length - got); if (k == 0) break; got += k; }
        return got == b.Length ? b : b[..got];
    }

    /// <summary>a DDS file around block data (aew_tex.dds)</summary>
    public static byte[] Dds(string fmt, int w, int h, byte[] data)
    {
        var four = fmt switch { "PF_DXT1" => "DXT1", "PF_DXT5" => "DXT5", "PF_BC5" => "ATI2", "PF_BC4" => "ATI1", _ => null };
        var hdr = new byte[128];
        "DDS "u8.CopyTo(hdr);
        uint[] f = [124, 0x1007 | 0x80000, (uint)h, (uint)w, (uint)data.Length, 0, 1];
        for (int i = 0; i < 7; i++) BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(4 + 4 * i), f[i]);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(76), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(80), 4);
        if (four != null) { System.Text.Encoding.ASCII.GetBytes(four).CopyTo(hdr, 84); return [.. hdr, .. data]; }
        "DX10"u8.CopyTo(hdr.AsSpan(84));
        var dx10 = new byte[20];
        uint[] x = [98, 3, 0, 1, 0];
        for (int i = 0; i < 5; i++) BinaryPrimitives.WriteUInt32LittleEndian(dx10.AsSpan(4 * i), x[i]);
        return [.. hdr, .. dx10, .. data];
    }

    public static Img ToImage(string fmt, int w, int h, byte[] data) => Imaging.Dds.Decode(Dds(fmt, w, h, data));

    /// <summary>PS4 GNM tiling: 8x8-block tiles (Morton order inside a tile), tiles row by row; sizes padded to 8 blocks</summary>
    public static byte[] UntilePs4(string fmt, int w, int h, byte[] data)
    {
        var (bpb, bd) = Block[fmt];
        int bw = Math.Max(1, (w + bd - 1) / bd), bh = Math.Max(1, (h + bd - 1) / bd);
        int tw = (bw + 7) / 8, th = (bh + 7) / 8;
        int need = tw * th * 64 * bpb;
        var src = new byte[need];
        data.AsSpan(0, Math.Min(need, data.Length)).CopyTo(src);
        var outb = new byte[th * 8 * tw * 8 * bpb];
        for (int ty = 0; ty < th; ty++)
            for (int tx = 0; tx < tw; tx++)
                for (int t = 0; t < 64; t++)
                {
                    int x = 0, y = 0;
                    for (int b = 0; b < 3; b++) { x |= ((t >> (2 * b)) & 1) << b; y |= ((t >> (2 * b + 1)) & 1) << b; }
                    int si = ((ty * tw + tx) * 64 + t) * bpb, di = ((ty * 8 + y) * tw * 8 + tx * 8 + x) * bpb;
                    Array.Copy(src, si, outb, di, bpb);
                }
        var cropped = new byte[bh * bw * bpb];
        for (int y = 0; y < bh; y++) Array.Copy(outb, y * tw * 8 * bpb, cropped, y * bw * bpb, bw * bpb);
        return cropped;
    }
}
