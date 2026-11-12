using System.Buffers.Binary;
using System.Text;

namespace PortIT.Engine.Readers;

/// <summary>2K's hidden-polygon list (&lt;attire&gt;.hpl beside the attire .mcd): per mesh, the vertices of the base model that
/// the game hides under this attire (the feet in the shoes, the skin under the trunks). Layout, measured on WWE 2K25 files
/// (2026-11-12): "HPL!", then per level of detail "LOD!" u32, per state "DATA" u32, per mesh "MESH" u32 name length,
/// the name (padded to 4 bytes), u32 count, count x u32 vertex index; "END!" u32 at the end.</summary>
public static class Hpl
{
    /// <summary>the first level of detail's first state: mesh name -&gt; vertex indices (null: no usable file)</summary>
    public static Dictionary<string, int[]>? Read(string path)
    {
        if (!File.Exists(path)) return null;
        var b = File.ReadAllBytes(path);
        if (b.Length < 8 || Encoding.ASCII.GetString(b, 0, 4) != "HPL!") return null;
        var o = 4;
        int lod = -1, data = -1;
        var outd = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        uint U() { if (o + 4 > b.Length) throw new InvalidDataException("hpl ends early"); uint v = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o)); o += 4; return v; }
        try
        {
            while (o + 4 <= b.Length)
            {
                string tag = Encoding.ASCII.GetString(b, o, 4);
                o += 4;
                switch (tag)
                {
                    case "END!": return outd;
                    case "LOD!": lod++; data = -1; U(); break;
                    case "DATA": data++; U(); break;
                    case "MESH":
                        int nl = (int)U();
                        if (nl < 0 || o + nl > b.Length) return null;
                        string name = Encoding.ASCII.GetString(b, o, nl).TrimEnd('\0');
                        o += (nl + 3) & ~3;
                        int cnt = (int)U();
                        if (cnt < 0 || o + 4L * cnt > b.Length) return null;
                        var idx = new int[cnt];
                        for (int i = 0; i < cnt; i++) idx[i] = (int)U();
                        if (lod == 0 && data == 0) outd[name] = idx;
                        break;
                    default: return null;     // (an unknown layout: not used)
                }
            }
        }
        catch (InvalidDataException) { return null; }
        return outd;
    }
}
