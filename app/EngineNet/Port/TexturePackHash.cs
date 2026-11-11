using System.Buffers.Binary;
using System.IO.Hashing;
using PortIT.Engine.Formats;

namespace PortIT.Engine.Port;

/// <summary>the SvR 2011 PC port's texture packs name a file by XXH3-64 (seed 0) of the texture's top level: port of
/// k26/xxh3.py and full_port.pac_texture_hashes</summary>
public static class TexturePackHash
{
    public static ulong Xxh3(ReadOnlySpan<byte> data) => XxHash3.HashToUInt64(data, 0);

    public static string Hex(ReadOnlySpan<byte> data) => Xxh3(data).ToString("x16");

    /// <summary>{"color": hash, "normal": hash} of the pac's first attire: XXH3 of each DDS's top level (128-byte
    /// header skipped; the top level's size from its width / height, 16 bytes a 4x4 block)</summary>
    public static Dictionary<string, string> PacTextureHashes(byte[] pac)
    {
        var outd = new Dictionary<string, string>();
        var arc = Epk8.Read(pac);
        foreach (var e in Pach.Read(Bpe.Unpack(arc.Groups[0].Entries[0].Data)))
        {
            var raw = Bpe.Unpack(e.Data);
            if (!Bundle.IsBundle(raw)) continue;
            foreach (var (name, d) in Bundle.Textures(raw))
            {
                if (name is not ("color" or "normal")) continue;
                int hh = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(12)), w = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(16));
                int top = (w + 3) / 4 * ((hh + 3) / 4) * 16;
                outd[name] = Hex(d.AsSpan(128, Math.Min(top, d.Length - 128)));
            }
        }
        return outd;
    }
}
