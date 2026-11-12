using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace PortIT.Engine.Readers;

/// <summary>UE4 IoStore container (.utoc / .ucas, TOC version 2-3, UE 4.26/4.27), read only - port of iostore.py
/// (Toc) and ioextract.py (Container). Oodle (Kraken) blocks are unpacked by Kraken.cs (no DLL needed).</summary>
public sealed class IoContainer : IDisposable
{
    public readonly string Path;
    public int Version, HeaderSize, EntryCount, CblockCount, CblockSize, DirIndexSize, Flags;
    public List<(long Off, long Len)> OffLen = [];
    public List<(long Off, int Csz, int Usz, int Meth)> Blocks = [];
    public List<string> Methods = ["None"];
    byte[] dirIndex = [];
    /// <summary>(toc entry index, path) in the directory index's order (Python's dict insertion order)</summary>
    public List<(int Index, string Name)> Names = [];
    readonly FileStream ucas;

    public IoContainer(string utoc)
    {
        Path = utoc;
        var d = File.ReadAllBytes(utoc);
        if (!d.AsSpan(0, 16).SequenceEqual("-==--==--==--==-"u8)) throw new InvalidDataException("not a utoc");
        Version = d[16];
        uint U(int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
        HeaderSize = (int)U(20); EntryCount = (int)U(24); CblockCount = (int)U(28);
        int cmethCount = (int)U(36), cmethLen = (int)U(40);
        CblockSize = (int)U(44); DirIndexSize = (int)U(48);
        Flags = d[80];
        int o = HeaderSize;
        o += 12 * EntryCount;                       // (chunk ids)
        for (int i = 0; i < EntryCount; i++)
        {
            var b = d.AsSpan(o + 10 * i, 10);
            long off = 0, ln = 0;
            for (int k = 0; k < 5; k++) { off = (off << 8) | b[k]; ln = (ln << 8) | b[5 + k]; }
            OffLen.Add((off, ln));
        }
        o += 10 * EntryCount;
        for (int i = 0; i < CblockCount; i++)
        {
            var b = d.AsSpan(o + 12 * i, 12);
            long off = b[0] | ((long)b[1] << 8) | ((long)b[2] << 16) | ((long)b[3] << 24) | ((long)b[4] << 32);
            int csz = b[5] | (b[6] << 8) | (b[7] << 16), usz = b[8] | (b[9] << 8) | (b[10] << 16);
            Blocks.Add((off, csz, usz, b[11]));
        }
        o += 12 * CblockCount;
        for (int i = 0; i < cmethCount; i++)
        {
            var s = d.AsSpan(o + cmethLen * i, cmethLen);
            int z = s.IndexOf((byte)0);
            Methods.Add(Encoding.ASCII.GetString(z < 0 ? s : s[..z]));
        }
        o += cmethLen * cmethCount;
        if ((Flags & 4) != 0)       // signed
        {
            int hsz = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o));
            o += 4 + hsz + hsz + 20 * CblockCount;
        }
        if ((Flags & 8) != 0) dirIndex = d.AsSpan(o, DirIndexSize).ToArray();
        Names = ReadNames();
        ucas = File.OpenRead(utoc[..^5] + ".ucas");
    }

    List<(int, string)> ReadNames()
    {
        var outl = new List<(int, string)>();
        if (dirIndex.Length == 0) return outl;
        var b = dirIndex;
        int o = 0;
        int I32() { int v = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o)); o += 4; return v; }
        string Fstr()
        {
            int n = I32();
            if (n == 0) return "";
            if (n > 0) { var s = Encoding.Latin1.GetString(b, o, n - 1); o += n; return s; }
            n = -n;
            var w = Encoding.Unicode.GetString(b, o, 2 * (n - 1));
            o += 2 * n;
            return w;
        }
        string mount = Fstr();
        int ndir = I32();
        var dirs = new uint[ndir, 4];
        for (int i = 0; i < ndir; i++) for (int k = 0; k < 4; k++) dirs[i, k] = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o + 16 * i + 4 * k));
        o += 16 * ndir;
        int nfile = I32();
        var files = new uint[nfile, 3];
        for (int i = 0; i < nfile; i++) for (int k = 0; k < 3; k++) files[i, k] = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o + 12 * i + 4 * k));
        o += 12 * nfile;
        int nstr = I32();
        var strs = new List<string>(nstr);
        for (int i = 0; i < nstr; i++) strs.Add(Fstr());
        const uint None = 0xFFFFFFFF;
        var byIndex = new Dictionary<int, int>();       // (a later entry for the same index replaces the value in place)
        void Walk(uint di, string prefix)
        {
            uint name = dirs[di, 0], child = dirs[di, 1], f = dirs[di, 3];
            string path = prefix + (name != None ? strs[(int)name] + "/" : "");
            while (f != None)
            {
                uint fn = files[f, 0], nxt = files[f, 1];
                int user = (int)files[f, 2];
                string full = mount + path + strs[(int)fn];
                if (byIndex.TryGetValue(user, out int at)) outl[at] = (user, full);
                else { byIndex[user] = outl.Count; outl.Add((user, full)); }
                f = nxt;
            }
            uint c = child;
            while (c != None) { Walk(c, path); c = dirs[c, 2]; }
        }
        if (ndir > 0) Walk(0, "");
        return outl;
    }

    public byte[] Read(int idx)
    {
        var (off, ln) = OffLen[idx];
        long bs = CblockSize;
        var outb = new MemoryStream();
        for (long bi = off / bs; bi < (off + ln + bs - 1) / bs; bi++)
        {
            var (boff, csz, usz, meth) = Blocks[(int)bi];
            ucas.Seek(boff, SeekOrigin.Begin);
            var raw = new byte[meth != 0 ? (csz + 15) & ~15 : csz];
            ucas.ReadExactly(raw, 0, raw.Length);
            if (Methods[meth] == "None") outb.Write(raw, 0, usz);
            else
            {
                var dst = new byte[usz];
                Oodle.Unpack(raw.AsSpan(0, csz).ToArray(), dst, $"block {bi} of {System.IO.Path.GetFileName(Path)}");
                outb.Write(dst);
            }
        }
        long start = off - off / bs * bs;
        return outb.GetBuffer().AsSpan((int)start, (int)ln).ToArray();
    }

    public void Dispose() => ucas.Dispose();
}

/// <summary>Oodle blocks: Kraken (AEW, 2K19 - 2K22) is unpacked by PortIT's own Kraken.cs; anything else falls back to
/// Oodle's decompressor from the user's own WWE 2K26 install (ioextract.py), which is never part of PortIT</summary>
public static class Oodle
{
    /// <summary>src (one compressed stream) -&gt; dst (exactly the decompressed size)</summary>
    public static void Unpack(byte[] src, byte[] dst, string what, Func<byte[], long, byte[], long, long>? dll = null)
    {
        try { Kraken.Decode(src, dst); return; }
        catch (NotSupportedException) { }           // (not Kraken: the user's own DLL, when there is one)
        var big = new byte[dst.Length + 64];
        long got = (dll ?? Decompress)(src, src.Length, big, dst.Length);
        if (got != dst.Length) throw new InvalidDataException($"Oodle failed on {what} ({got} != {dst.Length})");
        Array.Copy(big, dst, dst.Length);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate long Fn(byte[] src, long srcLen, byte[] dst, long dstLen, int fuzz, int crc, int verbose,
                     IntPtr dstBase, long dstBaseSize, IntPtr cb, IntPtr cbData, IntPtr decoderMem, long decoderMemSize, int threadPhase);
    static Fn? fn;

    public static readonly string[] K26Paths =
    [
        @"C:\Program Files (x86)\Steam\steamapps\common\WWE 2K26\oo2core_9_win64.dll",
        @"C:\Program Files (x86)\Steam\steamapps\common\WWE 2K26\Meta2_Release_Build\oo2core_9_win64.dll",
        @"C:\Program Files (x86)\Steam\steamapps\common\WWE 2K26\cakeview\oo2core_9_win64.dll",
    ];

    public static long Decompress(byte[] src, long srcLen, byte[] dst, long dstLen)
    {
        if (fn == null)
        {
            var p = K26Paths.FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("WWE 2K26's oo2core_9_win64.dll was not found");
            fn = Marshal.GetDelegateForFunctionPointer<Fn>(NativeLibrary.GetExport(NativeLibrary.Load(p), "OodleLZ_Decompress"));
        }
        return fn(src, srcLen, dst, dstLen, 1, 0, 0, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, 3);
    }
}

/// <summary>UE 4.26/4.27 IoStore (zen) package: name map, export map, export bundle order (port of zenpkg.Pkg)</summary>
public sealed class ZenPackage
{
    public readonly byte[] D;
    public int NamesOff, NamesSize, ImportsOff, ExportsOff, BundlesOff, GraphOff, GraphSize;
    public List<string> Names = [];
    public sealed record Export(long SerialOff, long SerialSize, string Name, ulong Cls);
    public List<Export> Exports = [];

    public ZenPackage(byte[] data)
    {
        D = data;
        int I(int o) => BinaryPrimitives.ReadInt32LittleEndian(D.AsSpan(o));
        // ('<4I2I9i': name_i, name_n, src_i, src_n, flags, header_size, names_off, names_size, hashes_off, hashes_size,
        //  imports_off, exports_off, bundles_off, graph_off, graph_size)
        NamesOff = I(24); NamesSize = I(28); ImportsOff = I(40); ExportsOff = I(44); BundlesOff = I(48); GraphOff = I(52); GraphSize = I(56);
        int o = NamesOff, end = NamesOff + NamesSize;
        while (o < end)
        {
            int h0 = D[o], h1 = D[o + 1];
            bool wide = (h0 & 0x80) != 0;
            int n = ((h0 & 0x7F) << 8) | h1;
            o += 2;
            if (wide)
            {
                if (((o - NamesOff) & 1) != 0) o++;   // (utf-16 names are 2-byte aligned)
                Names.Add(Encoding.Unicode.GetString(D, o, 2 * n));
                o += 2 * n;
            }
            else { Names.Add(Encoding.Latin1.GetString(D, o, n)); o += n; }
        }
        o = ExportsOff;
        while (o + 72 <= BundlesOff)
        {
            long off = BinaryPrimitives.ReadInt64LittleEndian(D.AsSpan(o)), size = BinaryPrimitives.ReadInt64LittleEndian(D.AsSpan(o + 8));
            uint ni = BinaryPrimitives.ReadUInt32LittleEndian(D.AsSpan(o + 16)), nn = BinaryPrimitives.ReadUInt32LittleEndian(D.AsSpan(o + 20));
            ulong cls = BinaryPrimitives.ReadUInt64LittleEndian(D.AsSpan(o + 32));
            Exports.Add(new Export(off, size, FName(ni, nn), cls));
            o += 72;
        }
    }

    public string FName(uint i, uint n = 0)
    {
        i &= 0x3FFFFFFF;
        string s = i < Names.Count ? Names[(int)i] : "?" + i;
        return n == 0 ? s : $"{s}_{n - 1}";
    }

    /// <summary>export indices in serialization order (zenpkg.bundle_order)</summary>
    public List<int> BundleOrder()
    {
        int o = BundlesOff, end = GraphOff;
        uint U(int p) => BinaryPrimitives.ReadUInt32LittleEndian(D.AsSpan(p));
        uint first = U(o), count = U(o + 4);
        int nb = 1;
        while (true)
        {
            int entriesAt = o + 8 * nb;
            if (entriesAt + 8 * (first + count) <= end && nb < 64)
            {
                bool inside = nb * 8 + o < end;
                uint f2 = inside ? U(o + 8 * nb) : 0, c2 = inside ? U(o + 8 * nb + 4) : 0;
                if (inside && f2 == first + count && c2 > 0 && nb * 8 + o + 8 <= end)
                {
                    first = f2; count = c2; nb++;
                    continue;
                }
            }
            break;
        }
        var order = new List<int>();
        for (int k = o + 8 * nb; k + 8 <= end; k += 8)
        {
            uint idx = U(k), cmd = U(k + 4);
            if (cmd == 1 && idx < Exports.Count) order.Add((int)idx);
        }
        return order;
    }

    /// <summary>the exports' bytes, back to back after the header, in export bundle (serialize) order</summary>
    public List<(Export E, int Start, int End)> ExportData()
    {
        long o = GraphOff + GraphSize;
        var order = BundleOrder();
        if (order.Count == 0) order = Enumerable.Range(0, Exports.Count).ToList();
        var outl = new List<(Export, int, int)>();
        foreach (int i in order)
        {
            var e = Exports[i];
            outl.Add((e, (int)o, (int)(o + e.SerialSize)));
            o += e.SerialSize;
        }
        return outl;
    }
}

/// <summary>tagged-property reader over a zen package (port of zenpkg.Reader). Values: string, long, double (from
/// f32), bool, List&lt;object?&gt;, Dictionary&lt;string, object?&gt; (insertion ordered).</summary>
public sealed class ZenReader
{
    readonly ZenPackage p;
    readonly byte[] d;
    public int O;
    public readonly int End;

    public ZenReader(ZenPackage pkg, int start, int end) { p = pkg; d = pkg.D; O = start; End = end; }

    public byte[] Data => d;

    public byte U8() => d[O++];
    public int I32() { int v = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(O)); O += 4; return v; }
    public uint U32() { uint v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(O)); O += 4; return v; }
    public long I64() { long v = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(O)); O += 8; return v; }
    public float F32() { float v = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(O)); O += 4; return v; }
    public string Name() { uint i = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(O)), n = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(O + 4)); O += 8; return p.FName(i, n); }
    public void Guid() => O += 16;

    public string FString()
    {
        int n = I32();
        if (n == 0) return "";
        if (n > 0) { var s = Encoding.Latin1.GetString(d, O, n - 1); O += n; return s; }
        n = -n;
        var w = Encoding.Unicode.GetString(d, O, 2 * (n - 1));
        O += 2 * n;
        return w;
    }

    public Dictionary<string, object?> Props()
    {
        var outd = new Dictionary<string, object?>();
        while (true)
        {
            string name = Name();
            if (name == "None") return outd;
            string typ = Name();
            int size = I32(), idx = I32();
            var extra = new Dictionary<string, object?>();
            switch (typ)
            {
                case "StructProperty": extra["struct"] = Name(); Guid(); break;
                case "BoolProperty": extra["bool"] = (long)U8(); break;
                case "ByteProperty" or "EnumProperty": extra["enum"] = Name(); break;
                case "ArrayProperty" or "SetProperty": extra["inner"] = Name(); break;
                case "MapProperty": extra["inner"] = Name(); extra["value"] = Name(); break;
            }
            if (U8() != 0) Guid();
            int start = O;
            object? val;
            try { val = Value(typ, size, extra); }
            catch (Exception e)   // (as the Python reader: any failure becomes a text value, the size skips on)
            { val = $"<{typ}: {e.Message}>"; }
            O = start + size;
            outd[idx == 0 ? name : $"{name}[{idx}]"] = val;
        }
    }

    object? Value(string typ, int size, Dictionary<string, object?> extra, bool inArray = false)
    {
        switch (typ)
        {
            case "BoolProperty": return inArray ? U8() != 0 : extra.TryGetValue("bool", out var b) && (long)b! != 0;
            case "IntProperty": return (long)I32();
            case "UInt32Property": return (long)U32();
            case "Int64Property": return I64();
            case "FloatProperty": return (double)F32();
            case "NameProperty": return Name();
            case "StrProperty": return FString();
            case "TextProperty":
            {
                U32();
                int hist = O < d.Length ? d[O] : 255;
                O++;
                if (hist == 0) return new Dictionary<string, object?> { ["ns"] = FString(), ["key"] = FString(), ["src"] = FString() };
                if (hist == 11) { var tbl = Name(); return new Dictionary<string, object?> { ["table"] = tbl, ["key"] = FString() }; }
                if (hist == 255) return U32() != 0 ? FString() : "";
                return $"<text history {hist}>";
            }
            case "ByteProperty" or "EnumProperty":
                if (typ == "ByteProperty" && (!extra.TryGetValue("enum", out var en) || en is null or "None")) return (long)U8();
                return Name();
            case "SoftObjectProperty":
            {
                var soft = Name();
                return new Dictionary<string, object?> { ["soft"] = soft, ["sub"] = FString() };
            }
            case "ObjectProperty":
                return new Dictionary<string, object?> { ["obj"] = size == 8 ? I64() : I32() };
            case "StructProperty":
            {
                var st = extra.GetValueOrDefault("struct") as string;
                switch (st)
                {
                    case "Vector" or "Rotator": return new List<object?> { (double)F32(), (double)F32(), (double)F32() };
                    case "Vector2D": return new List<object?> { (double)F32(), (double)F32() };
                    case "Quat": return new List<object?> { (double)F32(), (double)F32(), (double)F32(), (double)F32() };
                    case "Guid": Guid(); return "guid";
                    default: return Props();
                }
            }
            case "ArrayProperty":
            {
                var inner = (string)extra["inner"]!;
                int n = I32();
                var l = new List<object?>();
                if (inner == "StructProperty")
                {
                    Name(); Name(); I32(); I32();
                    var st = Name();
                    Guid();
                    if (U8() != 0) Guid();
                    for (int i = 0; i < n; i++) l.Add(Value("StructProperty", 0, new() { ["struct"] = st }));
                    return l;
                }
                for (int i = 0; i < Math.Min(n, 100000); i++) l.Add(Value(inner, 0, [], true));
                return l;
            }
            case "MapProperty":
            {
                I32();
                int n = I32();
                var l = new List<object?>();
                var ki = (string)extra["inner"]!; var vi = (string)extra["value"]!;
                for (int i = 0; i < n; i++)
                {
                    object? k = ki != "StructProperty" ? Value(ki, 0, [], true) : Props();
                    object? v = vi != "StructProperty" ? Value(vi, 0, [], true) : Props();
                    l.Add(new List<object?> { k, v });
                }
                return l;
            }
        }
        return $"<{typ} {size} bytes>";
    }
}
