using System.Buffers.Binary;

namespace PortIT.Engine.Readers;

/// <summary>a 2K wrestler folder's .dds files by .jmtl hash (FNV-1a 64 of the lower-case '.tex' path, every tail of it,
/// the 2K26 archive's Characters/... path and the shared Characters/_Shared textures) - port of
/// portit/source_2k.py: Textures, jmtl_info, role_of, pick_by_role</summary>
public sealed class Source2kTextures
{
    public readonly List<string> Files;
    public readonly Dictionary<ulong, string> ByHash = [];
    public readonly List<string> Shared = [];

    public static ulong Fnv(string s)
    {
        ulong h = 0xCBF29CE484222325;
        foreach (char ch in s.ToLowerInvariant()) unchecked { h = (h ^ (ulong)(ch & 0xFF)) * 0x100000001B3; }
        return h;
    }

    public Source2kTextures(string folder)
    {
        Files = Mtls.GlobOrder(folder, ".dds").ToList();
        foreach (var p in Files)
        {
            var parts = Path.GetRelativePath(folder, p).Replace('\\', '/').Split('/');
            string stem = Path.GetFileNameWithoutExtension(parts[^1]);
            var full = new List<string> { Path.GetFileName(folder) };
            full.AddRange(parts[..^1]);
            full.Add(stem + ".tex");
            for (int i = 0; i < full.Count; i++) ByHash.TryAdd(Fnv(string.Join("/", full.Skip(i))), p);
            ByHash.TryAdd(Fnv(string.Join("/", new[] { "Characters" }.Concat(full))), p);
        }
        var shared = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)))!, "_Shared", "Textures");
        if (Directory.Exists(shared))
            foreach (var f in Directory.EnumerateFiles(shared))
                if (f.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                {
                    ByHash.TryAdd(Fnv("Characters/_Shared/Textures/" + Path.GetFileNameWithoutExtension(f) + ".tex"), f);
                    Shared.Add(f);
                }
    }

    /// <summary>-&gt; (shader byte or null, the u64 values of the file at every 4-byte offset, in file order, no repeats)</summary>
    public static (int? Shader, List<ulong> Vals) JmtlInfo(string path)
    {
        var d = File.ReadAllBytes(path);
        int? shader = null;
        try
        {
            int root = (int)BinaryPrimitives.ReadUInt32LittleEndian(d);
            int vt = root - BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(root));
            int vlen = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(vt));
            if (vlen >= 8)
            {
                int off = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(vt + 6));
                if (off != 0) shader = d[root + off];
            }
        }
        catch (Exception) { }
        var seen = new HashSet<ulong>();
        var vals = new List<ulong>();
        for (int o = 0; o + 8 <= d.Length; o += 4)
        {
            ulong v = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(o));
            if (seen.Add(v)) vals.Add(v);
        }
        return (shader, vals);
    }

    public static string RoleOf(string path)
    {
        var s = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        foreach (var (suf, role) in new[] { ("_color", "color"), ("_nrm", "nrm"), ("_rai", "rai"), ("_flow", "flow"), ("_mask", "mask"), ("_flush", "flush"), ("_normal", "normal2") })
            if (s.EndsWith(suf)) return role;
        return "other";
    }

    /// <summary>role -&gt; texture among paths (the material file's order): the one named after the material when several</summary>
    public static void PickByRole(Dictionary<string, string> outd, IEnumerable<string> paths, string jmtlPath)
    {
        var mat = Path.GetFileNameWithoutExtension(jmtlPath).ToLowerInvariant();
        bool Named(string p) => Path.GetFileNameWithoutExtension(p).ToLowerInvariant().StartsWith(mat + "_");
        foreach (var p in paths)
        {
            var r = RoleOf(p);
            if (!outd.TryGetValue(r, out var cur)) outd[r] = p;
            else if (Named(p) && !Named(cur)) outd[r] = p;
        }
    }

    /// <summary>role -&gt; dds path from a material's own hashes</summary>
    /// <summary>role -&gt; dds path for one material (alt: a second name to try, e.g. 'eye' for eye_l): its own hashes,
    /// else &lt;stem&gt;_&lt;role&gt;.dds in a folder beside the model</summary>
    public Dictionary<string, string> ForMaterial(string? jmtlPath, string stem, string near, string? alt = null)
    {
        var o = MaterialTextures(jmtlPath);
        if (!o.ContainsKey("color"))
            foreach (var p in Files)
            {
                if (Path.GetDirectoryName(Path.GetDirectoryName(p)) != near) continue;
                string s = Path.GetFileNameWithoutExtension(p).ToLowerInvariant();
                // (topsx_color, headx_color...: mods that name their maps with an extra x - the preview showed them white)
                if (new[] { stem, alt }.Any(n => !string.IsNullOrEmpty(n) && (s.StartsWith(n.ToLowerInvariant() + "_") || s.StartsWith(n.ToLowerInvariant() + "x_")))) o.TryAdd(RoleOf(p), p);
            }
        return o;
    }

    public Dictionary<string, string> MaterialTextures(string? jmtlPath)
    {
        var o = new Dictionary<string, string>();
        if (jmtlPath != null && File.Exists(jmtlPath))
            PickByRole(o, JmtlInfo(jmtlPath).Vals.Where(ByHash.ContainsKey).Select(v => ByHash[v]), jmtlPath);
        return o;
    }
}
