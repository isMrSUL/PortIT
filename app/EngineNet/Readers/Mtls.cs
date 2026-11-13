using System.Text;
using System.Text.RegularExpressions;

namespace PortIT.Engine.Readers;

/// <summary>WWE 2K .mtls material files (2K20 - 2K25 mods) -&gt; which texture each material uses (port of readers/mtls.py:
/// v6 one "MTL!" block per material after the names; v5 one string table, a material's name then its shader)</summary>
public static class Mtls
{
    static readonly string[] TexParams = ["colorMap", "g_nrmMap", "normalMap", "flowMap", "g_comboMap", "roughnessMap", "metalMap", "flushMap"];

    /// <summary>printable runs of 2+ characters (Python: re.findall(rb'[\x20-\x7e]{2,}'))</summary>
    static List<string> Strings(ReadOnlySpan<byte> b)
    {
        var o = new List<string>();
        int i = 0;
        while (i < b.Length)
        {
            if (b[i] < 0x20 || b[i] > 0x7E) { i++; continue; }
            int j = i;
            while (j < b.Length && b[j] >= 0x20 && b[j] <= 0x7E) j++;
            if (j - i >= 2) o.Add(Encoding.Latin1.GetString(b[i..j]));
            i = j;
        }
        return o;
    }

    static Dictionary<string, string> Params(List<string> s)
    {
        var o = new Dictionary<string, string>();
        for (int i = 0; i + 1 < s.Count; i++)
            if (TexParams.Contains(s[i]) && !s[i + 1].EndsWith("@ref") && !TexParams.Contains(s[i + 1]))
                o.TryAdd(s[i], s[i + 1]);
        return o;
    }

    /// <summary>-&gt; {material name (lower case, no ':skinned'): {parameter: texture name}} (insertion ordered)</summary>
    public static List<(string Material, Dictionary<string, string> Params)> Read(string path)
    {
        var d = File.ReadAllBytes(path);
        var outl = new List<(string, Dictionary<string, string>)>();
        void Set(string k, Dictionary<string, string> v)
        {
            int at = outl.FindIndex(e => e.Item1 == k);
            if (at >= 0) outl[at] = (k, v); else outl.Add((k, v));
        }
        if (d.Length < 4 || !d.AsSpan(0, 4).SequenceEqual("MTLs"u8)) return outl;
        var blocks = Split(d, "MTL!"u8);
        if (blocks.Count > 1)                                    // v6
        {
            var names = Strings(blocks[0]).Skip(1).Where(s => s.ToLowerInvariant().EndsWith(":skinned"))
                .Select(s => Regex.Replace(s, "(?i):skinned$", "")).ToList();
            for (int i = 0; i < Math.Min(names.Count, blocks.Count - 1); i++) Set(names[i].ToLowerInvariant(), Params(Strings(blocks[i + 1])));
            return outl;
        }
        var st = Strings(d.AsSpan(Math.Min(12, d.Length)));      // v5
        var starts = Enumerable.Range(0, st.Count).Where(i => (st[i].StartsWith("yC") || st[i].StartsWith("yc")) && i > 0).Select(i => i - 1).ToList();
        for (int k = 0; k < starts.Count; k++)
        {
            int i = starts[k], end = k + 1 < starts.Count ? starts[k + 1] : st.Count;
            Set(Regex.Replace(st[i], "(?i):skinned$", "").ToLowerInvariant(), Params(st.GetRange(i + 1, Math.Max(0, end - i - 1))));
        }
        return outl;
    }

    static List<byte[]> Split(byte[] d, ReadOnlySpan<byte> sep)
    {
        var o = new List<byte[]>();
        int p = 0;
        while (true)
        {
            int i = d.AsSpan(p).IndexOf(sep);
            if (i < 0) { o.Add(d[p..]); break; }
            o.Add(d[p..(p + i)]);
            p += i + sep.Length;
        }
        return o;
    }

    /// <summary>every .mtls under folder -&gt; {material: colour texture name} (default_* = the game's own, left out); files
    /// in Python's recursive glob order (a folder's files, then its sub folders in order), the first one wins</summary>
    public static Dictionary<string, string> ColorMaps(string folder)
    {
        var o = new Dictionary<string, string>();
        foreach (var p in GlobOrder(folder, ".mtls"))
        {
            try
            {
                foreach (var (name, prm) in Read(p))
                    if (prm.TryGetValue("colorMap", out var c) && !c.ToLowerInvariant().StartsWith("default")) o.TryAdd(name, c);
            }
            catch (IOException) { }
        }
        return o;
    }

    /// <summary>every material name any .mtls under folder defines (lower case, no ':skinned'); empty when it has none</summary>
    public static HashSet<string> Defined(string folder)
    {
        var o = new HashSet<string>();
        foreach (var p in GlobOrder(folder, ".mtls"))
            try { foreach (var (name, _) in Read(p)) o.Add(name); }
            catch (IOException) { }
        return o;
    }

    /// <summary>glob('**/*ext', recursive=True)'s order: the folder itself, then every sub folder depth first (pre-order,
    /// directory order); each folder's matching files in directory order</summary>
    public static IEnumerable<string> GlobOrder(string folder, string ext)
    {
        if (!Directory.Exists(folder)) yield break;
        var dirs = new List<string> { folder };
        void Walk(string d)
        {
            foreach (var s in Directory.EnumerateDirectories(d))
                if (!System.IO.Path.GetFileName(s).StartsWith('.')) { dirs.Add(s); Walk(s); }   // (glob skips hidden folders)
        }
        Walk(folder);
        foreach (var d in dirs)
            foreach (var f in Directory.EnumerateFiles(d))
                if (f.EndsWith(ext, StringComparison.OrdinalIgnoreCase) && !System.IO.Path.GetFileName(f).StartsWith('.')) yield return f;
    }
}

/// <summary>CityHash64 (Google CityHash v1.1, as Unreal uses it) and UE 4.27 IoStore import ids (port of cityhash.py)</summary>
public static class CityHash
{
    const ulong K0 = 0xc3a5c85c97cb3127, K1 = 0xb492b66fbe98f273, K2 = 0x9ae16a3b2f90404f, Kmul = 0x9ddfea08eb382d69;

    static ulong F64(ReadOnlySpan<byte> s, int i) => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(s[i..]);
    static ulong F32(ReadOnlySpan<byte> s, int i) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(s[i..]);
    static ulong Rot(ulong v, int s) => s == 0 ? v : (v >> s) | (v << (64 - s));
    static ulong Smix(ulong v) => v ^ (v >> 47);

    static ulong Len16(ulong u, ulong v, ulong mul = Kmul)
    {
        unchecked
        {
            ulong a = (u ^ v) * mul;
            a ^= a >> 47;
            ulong b = (v ^ a) * mul;
            b ^= b >> 47;
            return b * mul;
        }
    }

    static ulong Len0To16(ReadOnlySpan<byte> s)
    {
        unchecked
        {
            int n = s.Length;
            if (n >= 8)
            {
                ulong mul = K2 + (ulong)n * 2, a = F64(s, 0) + K2, b = F64(s, n - 8);
                ulong c = Rot(b, 37) * mul + a, d = (Rot(a, 25) + b) * mul;
                return Len16(c, d, mul);
            }
            if (n >= 4)
            {
                ulong mul = K2 + (ulong)n * 2, a = F32(s, 0);
                return Len16((ulong)n + (a << 3), F32(s, n - 4), mul);
            }
            if (n > 0)
            {
                uint a = s[0], b = s[n >> 1], c = s[n - 1];
                uint y = a + (b << 8), z = (uint)n + (c << 2);
                return Smix(((ulong)y * K2) ^ ((ulong)z * K0)) * K2;
            }
            return K2;
        }
    }

    static ulong Len17To32(ReadOnlySpan<byte> s)
    {
        unchecked
        {
            int n = s.Length;
            ulong mul = K2 + (ulong)n * 2, a = F64(s, 0) * K1, b = F64(s, 8), c = F64(s, n - 8) * mul, d = F64(s, n - 16) * K2;
            return Len16(Rot(a + b, 43) + Rot(c, 30) + d, a + Rot(b + K2, 18) + c, mul);
        }
    }

    static (ulong, ulong) Weak32(ulong w, ulong x, ulong y, ulong z, ulong a, ulong b)
    {
        unchecked
        {
            a += w;
            b = Rot(b + a + z, 21);
            ulong c = a;
            a += x + y;
            b += Rot(a, 44);
            return (a + z, b + c);
        }
    }

    static (ulong, ulong) Weak32S(ReadOnlySpan<byte> s, int i, ulong a, ulong b) => Weak32(F64(s, i), F64(s, i + 8), F64(s, i + 16), F64(s, i + 24), a, b);

    static ulong Bswap(ulong v) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(v);

    static ulong Len33To64(ReadOnlySpan<byte> s)
    {
        unchecked
        {
            int n = s.Length;
            ulong mul = K2 + (ulong)n * 2, a = F64(s, 0) * K2, b = F64(s, 8), c = F64(s, n - 24), d = F64(s, n - 32);
            ulong e = F64(s, 16) * K2, f = F64(s, 24) * 9, g = F64(s, n - 8), h = F64(s, n - 16) * mul;
            ulong u = Rot(a + g, 43) + (Rot(b, 30) + c) * 9;
            ulong v = ((a + g) ^ d) + f + 1;
            ulong w = Bswap((u + v) * mul) + h;
            ulong x = Rot(e + f, 42) + c;
            ulong y = (Bswap((v + w) * mul) + g) * mul;
            ulong z = e + f + c;
            a = Bswap((x + z) * mul + y) + b;
            b = Smix((z + a) * mul + d + h) * mul;
            return b + x;
        }
    }

    public static ulong Hash64(ReadOnlySpan<byte> s)
    {
        unchecked
        {
            int n = s.Length;
            if (n <= 32) return n <= 16 ? Len0To16(s) : Len17To32(s);
            if (n <= 64) return Len33To64(s);
            ulong x = F64(s, n - 40), y = F64(s, n - 16) + F64(s, n - 56), z = Len16(F64(s, n - 48) + (ulong)n, F64(s, n - 24));
            var v = Weak32S(s, n - 64, (ulong)n, z);
            var w = Weak32S(s, n - 32, y + K1, x);
            x = x * K1 + F64(s, 0);
            int end = (n - 1) / 64 * 64, i = 0;
            while (true)
            {
                x = Rot(x + y + v.Item1 + F64(s, i + 8), 37) * K1;
                y = Rot(y + v.Item2 + F64(s, i + 48), 42) * K1;
                x ^= w.Item2;
                y += v.Item1 + F64(s, i + 40);
                z = Rot(z + w.Item1, 33) * K1;
                v = Weak32S(s, i, v.Item2 * K1, x + w.Item1);
                w = Weak32S(s, i + 32, z + w.Item2, y + F64(s, i + 16));
                (z, x) = (x, z);
                i += 64;
                if (i == end) break;
            }
            return Len16(Len16(v.Item1, w.Item1) + Smix(y) * K1 + z, Len16(v.Item2, w.Item2) + x);
        }
    }

    /// <summary>UE 4.27 import hash of '/Game/.../Package.Object' (or '/Game/.../Package/Object')</summary>
    public static ulong ImportId(string objectPath)
    {
        string p = objectPath.Replace('.', '/').Replace(':', '/').ToLowerInvariant();
        return Hash64(Encoding.Unicode.GetBytes(p)) & ~(3UL << 62);
    }
}
