using System.Buffers.Binary;
using System.Text;

namespace PortIT.Engine.Formats;

/// <summary>an archive entry: its id / name and its bytes (still packed, as stored)</summary>
public sealed record Entry(uint Id, byte[] Data);

/// <summary>a named archive entry (EPAC: 4-character names, EPK8: 8)</summary>
public sealed record NamedEntry(byte[] Name, byte[] Data)
{
    public string NameText => Encoding.Latin1.GetString(Name).TrimEnd('\0');
}

/// <summary>an archive group: its 4CC type and entries</summary>
public sealed record Group(byte[] Type, List<NamedEntry> Entries)
{
    public string TypeText => Encoding.Latin1.GetString(Type);
}

/// <summary>"PACH": u32 count, count x {u32 id, u32 offset, u32 size}; offsets from the end of the table, entries 4-byte
/// aligned (port of svrfmt.pach_read / pach_write)</summary>
public static class Pach
{
    public static bool IsPach(ReadOnlySpan<byte> d) => d.Length >= 8 && d[..4].SequenceEqual("PACH"u8);

    public static List<Entry> Read(byte[] data)
    {
        if (!IsPach(data)) throw new InvalidDataException("not PACH");
        int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        int b = 8 + 12 * n;
        var list = new List<Entry>(n);
        for (int i = 0; i < n; i++)
        {
            var s = data.AsSpan(8 + 12 * i);
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(s);
            int off = (int)BinaryPrimitives.ReadUInt32LittleEndian(s[4..]);
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(s[8..]);
            list.Add(new Entry(id, data.AsSpan(b + off, size).ToArray()));
        }
        return list;
    }

    public static byte[] Write(IReadOnlyList<Entry> entries)
    {
        var table = new MemoryStream();
        var body = new MemoryStream();
        table.Write("PACH"u8);
        WriteU32(table, (uint)entries.Count);
        foreach (var e in entries)
        {
            WriteU32(table, e.Id);
            WriteU32(table, (uint)body.Length);
            WriteU32(table, (uint)e.Data.Length);
            body.Write(e.Data);
            while (body.Length % 4 != 0) body.WriteByte(0);
        }
        body.Position = 0;
        body.CopyTo(table);
        return table.ToArray();
    }

    internal static void WriteU32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        s.Write(b);
    }
}

/// <summary>"EPAC" archives (4-character entry names; port of svrfmt.epac_read / epac_write)</summary>
public static class Epac
{
    public sealed record Archive(byte[] Header, List<Group> Groups, byte[] Trailer);

    public static Archive Read(byte[] data)
    {
        if (!data.AsSpan(0, 4).SequenceEqual("EPAC"u8)) throw new InvalidDataException("not EPAC");
        var groups = new List<Group>();
        int p = 0x800;
        while (p < 0x4000)
        {
            var typ = data.AsSpan(p, 4).ToArray();
            if (typ.All(b => b == 0)) break;
            // (entries x 3 in the low 16 bits; the high half is a per-file constant / flag)
            int cnt = (int)(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 4)) & 0xFFFF) / 3;
            p += 12;
            var ents = new List<NamedEntry>(cnt);
            for (int i = 0; i < cnt; i++)
            {
                var name = data.AsSpan(p, 4).ToArray();
                int sec = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 4));
                int sz = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 8));
                ents.Add(new NamedEntry(name, Slice(data, 0x4000 + sec * 0x800, sz * 0x100)));
                p += 12;
            }
            groups.Add(new Group(typ, ents));
        }
        int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
        return new Archive(data[..0x800], groups, data[Math.Min(data.Length, 0x4000 + size)..]);
    }

    public static byte[] Write(byte[] header, IReadOnlyList<Group> groups, byte[]? trailer = null)
    {
        var toc = new MemoryStream();
        var body = new MemoryStream();
        foreach (var g in groups)
        {
            toc.Write(g.Type);
            Pach.WriteU32(toc, (uint)(g.Entries.Count * 3));
            Pach.WriteU32(toc, 0);
            foreach (var e in g.Entries)
            {
                Pad(body, 0x800);
                toc.Write(e.Name);
                Pach.WriteU32(toc, (uint)(body.Length / 0x800));
                Pach.WriteU32(toc, (uint)((e.Data.Length + 0xFF) / 0x100));
                body.Write(e.Data);
                Pad(body, 0x100);
            }
        }
        Pad(body, 0x800);
        if (toc.Length > 0x3800) throw new InvalidDataException("EPAC table too big");
        var outb = new byte[0x4000 + body.Length + (trailer is { Length: > 0 } ? trailer.Length : 0x800)];
        header.AsSpan(0, 0x800).CopyTo(outb);
        toc.ToArray().CopyTo(outb, 0x800);
        BinaryPrimitives.WriteUInt32LittleEndian(outb.AsSpan(4), (uint)toc.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(outb.AsSpan(8), (uint)body.Length);
        body.ToArray().CopyTo(outb, 0x4000);
        if (trailer is { Length: > 0 }) trailer.CopyTo(outb, 0x4000 + (int)body.Length);
        return outb;
    }

    internal static void Pad(MemoryStream s, int align)
    {
        while (s.Length % align != 0) s.WriteByte(0);
    }

    internal static byte[] Slice(byte[] d, int off, int len) =>
        off >= d.Length ? [] : d.AsSpan(off, Math.Min(len, d.Length - off)).ToArray();
}

/// <summary>"EPK8": character files (pac/ch/chNNN.pac) - as EPAC, with 8-character entry names (port of
/// ch_tool.epk8_read and w13_char.write_epk8)</summary>
public static class Epk8
{
    public sealed record Archive(byte[] Header, List<Group> Groups);

    public static bool IsEpk8(ReadOnlySpan<byte> d) => d.Length >= 4 && d[..4].SequenceEqual("EPK8"u8);

    public static Archive Read(byte[] d)
    {
        if (!IsEpk8(d)) throw new InvalidDataException("not EPK8");
        var groups = new List<Group>();
        int p = 0x800;
        while (p < 0x4000)
        {
            var typ = d.AsSpan(p, 4).ToArray();
            if (typ.All(b => b == 0)) break;
            int cnt = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p + 4));
            p += 12;
            var ents = new List<NamedEntry>(cnt);
            for (int i = 0; i < cnt; i++)
            {
                var name = d.AsSpan(p, 8).ToArray();
                int sec = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p + 8));
                int sz = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p + 12));
                ents.Add(new NamedEntry(name, Epac.Slice(d, 0x4000 + sec * 0x800, sz * 0x100)));
                p += 16;
            }
            groups.Add(new Group(typ, ents));
        }
        return new Archive(d[..0x800], groups);
    }

    /// <summary>EPK8 with one EMD group holding the entries (laid out like 2011 ch pacs); the header and the group
    /// record's flags come from a template pac</summary>
    public static byte[] Write(byte[] template, IReadOnlyList<(byte[] Name, byte[] Data)> entries)
    {
        var header = template.AsSpan(0, 0x800).ToArray();
        var index = new byte[0x3800];
        "EMD "u8.CopyTo(index);
        BinaryPrimitives.WriteUInt16LittleEndian(index.AsSpan(4), (ushort)(4 * entries.Count));   // (entries * 4)
        BinaryPrimitives.WriteUInt16LittleEndian(index.AsSpan(6), 0);
        template.AsSpan(0x806, 6).CopyTo(index.AsSpan(6));        // (the template's group record flags)
        var body = new MemoryStream();
        int p = 12;
        foreach (var (name, blob) in entries)
        {
            Epac.Pad(body, 0x800);
            var n8 = new byte[8];
            name.AsSpan(0, Math.Min(8, name.Length)).CopyTo(n8);
            n8.CopyTo(index, p);
            BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(p + 8), (uint)(body.Length / 0x800));
            BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(p + 12), (uint)((blob.Length + 0xFF) / 0x100));
            body.Write(blob);
            p += 16;
        }
        Epac.Pad(body, 0x800);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)(12 + 16 * entries.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)body.Length);
        var outb = new byte[0x800 + 0x3800 + body.Length + 0x800];
        header.CopyTo(outb, 0);
        index.CopyTo(outb, 0x800);
        body.ToArray().CopyTo(outb, 0x4000);
        return outb;
    }
}

/// <summary>a texture bundle inside a ch.pac part (ch_tool.is_bundle / bundle_names): u32 count, u32 0x100, ..., u32
/// 0x10; then count x 32-byte records {name[16], ..., u32 size @20, u32 offset @24}</summary>
public static class Bundle
{
    public static bool IsBundle(ReadOnlySpan<byte> raw) =>
        raw.Length > 16 && BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]) == 0x100
                        && BinaryPrimitives.ReadUInt32LittleEndian(raw[12..]) == 0x10;

    public static List<string> Names(ReadOnlySpan<byte> raw)
    {
        int c = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw);
        var names = new List<string>(c);
        for (int k = 0; k < c; k++)
        {
            var rec = raw.Slice(16 + 32 * k, 16);
            int z = rec.IndexOf((byte)0);
            names.Add(Encoding.Latin1.GetString(z < 0 ? rec : rec[..z]));
        }
        return names;
    }

    /// <summary>name -> the texture's bytes (a DDS-like record; size @20, offset @24 in each 32-byte record)</summary>
    public static List<(string Name, byte[] Data)> Textures(byte[] raw)
    {
        int c = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw);
        var names = Names(raw);
        var list = new List<(string, byte[])>(c);
        for (int k = 0; k < c; k++)
        {
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(16 + 32 * k + 20));
            int off = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(16 + 32 * k + 24));
            list.Add((names[k], raw.AsSpan(off, size).ToArray()));
        }
        return list;
    }
}
