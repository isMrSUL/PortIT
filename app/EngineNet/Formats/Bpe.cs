using System.Buffers.Binary;

namespace PortIT.Engine.Formats;

/// <summary>
/// Yuke's "BPE " entries: Philip Gage byte-pair blocks (port of svrfmt.py's bpe_decode / bpe_encode and bpe.zig).
/// Header: "BPE ", u32 0x100, u32 packed size, u32 unpacked size; then blocks of (pair table, u16 LE length, data).
/// Each block unpacks to at most 4000 bytes (the game's buffer).
/// </summary>
public static class Bpe
{
    const int Step = 4000;

    public static bool IsBpe(ReadOnlySpan<byte> data) =>
        data.Length >= 16 && data[0] == (byte)'B' && data[1] == (byte)'P' && data[2] == (byte)'E' && data[3] == (byte)' ';

    /// <summary>a "BPE " entry unpacked; anything else comes back as it is (svrfmt.unbpe)</summary>
    public static byte[] Unpack(byte[] data) => IsBpe(data) ? Decode(data) : data;

    public static byte[] Decode(ReadOnlySpan<byte> src)
    {
        if (!IsBpe(src)) throw new InvalidDataException("not BPE");
        int packedLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(src[8..]);
        int unpacked = (int)BinaryPrimitives.ReadUInt32LittleEndian(src[12..]);
        var out_ = new byte[unpacked];
        int o = 0, pos = 16, end = 16 + packedLen;
        Span<byte> left = stackalloc byte[256], right = stackalloc byte[256];
        Span<byte> stack = stackalloc byte[512];
        while (pos < end)
        {
            for (int i = 0; i < 256; i++) { left[i] = (byte)i; right[i] = 0; }
            int c = 0, count = src[pos++];
            while (true)
            {
                if (count > 127) { c += count - 127; count = 0; }
                if (c == 256) break;
                for (int k = 0; k < count + 1; k++)
                {
                    left[c] = src[pos++];
                    if (c != left[c]) right[c] = src[pos++];
                    c++;
                }
                if (c == 256) break;
                count = src[pos++];
            }
            int size = src[pos] | (src[pos + 1] << 8);
            pos += 2;
            for (int k = 0; k < size; k++)
            {
                int top = 1;
                stack[0] = src[pos + k];
                while (top > 0)
                {
                    byte s = stack[--top];
                    if (left[s] == s)
                    {
                        if (o >= unpacked) throw new InvalidDataException($"BPE size over {unpacked}");
                        out_[o++] = s;
                    }
                    else { stack[top++] = right[s]; stack[top++] = left[s]; }
                }
            }
            pos += size;
        }
        if (o != unpacked) throw new InvalidDataException($"BPE size {o} != {unpacked}");
        return out_;
    }

    /// <summary>
    /// raw bytes as a "BPE " entry. compress=false: "stored" blocks (an empty pair table and the raw bytes);
    /// compress=true: Gage's pair replacement, the same bytes as svrfmt.bpe_encode(compress=True) / bpe.exe
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> raw, bool compress)
    {
        var body = new MemoryStream(raw.Length + raw.Length / 8 + 64);
        if (compress)
        {
            var counts = new int[65536];
            var first = new int[65536];
            var seen = new ushort[65536];
            for (int s = 0; s < raw.Length; s += Step)
                Block(body, raw.Slice(s, Math.Min(Step, raw.Length - s)), counts, first, seen);
        }
        else
        {
            for (int s = 0; s < raw.Length; s += Step)
            {
                int n = Math.Min(Step, raw.Length - s);
                body.WriteByte(255); body.WriteByte(128); body.WriteByte(254);
                body.WriteByte((byte)(n & 0xFF)); body.WriteByte((byte)(n >> 8));
                body.Write(raw.Slice(s, n));
            }
        }
        var outb = new byte[16 + body.Length];
        "BPE "u8.CopyTo(outb);
        BinaryPrimitives.WriteUInt32LittleEndian(outb.AsSpan(4), 0x100);
        BinaryPrimitives.WriteUInt32LittleEndian(outb.AsSpan(8), (uint)body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(outb.AsSpan(12), (uint)raw.Length);
        body.Position = 0;
        body.ReadExactly(outb, 16, (int)body.Length);
        return outb;
    }

    static void Block(MemoryStream outs, ReadOnlySpan<byte> src, int[] counts, int[] first, ushort[] seen)
    {
        Span<byte> buf = stackalloc byte[Step];
        Span<byte> tmp = stackalloc byte[Step];
        int n = src.Length;
        src.CopyTo(buf);
        Span<byte> left = stackalloc byte[256], right = stackalloc byte[256];
        Span<bool> used = stackalloc bool[256];
        for (int i = 0; i < 256; i++) { left[i] = (byte)i; right[i] = 0; }
        for (int i = 0; i < n; i++) used[buf[i]] = true;
        Span<byte> free = stackalloc byte[256];
        int nfree = 0, fpos = 0;
        for (int i = 0; i < 256; i++) if (!used[i]) free[nfree++] = (byte)i;
        while (fpos < nfree)
        {
            if (n < 2) break;
            int nseen = 0;
            for (int i = 0; i + 1 < n; i++)
            {
                int p = buf[i] | (buf[i + 1] << 8);
                if (counts[p] == 0) { first[p] = i; seen[nseen++] = (ushort)p; }
                counts[p]++;
            }
            // the most frequent pair; a tie goes to the one seen first (Python's max over a dict in insertion order)
            int best = 0, pair = 0, bfirst = int.MaxValue;
            for (int k = 0; k < nseen; k++)
            {
                int p = seen[k], cnt = counts[p];
                if (cnt > best || (cnt == best && first[p] < bfirst)) { best = cnt; pair = p; bfirst = first[p]; }
            }
            for (int k = 0; k < nseen; k++) counts[seen[k]] = 0;
            if (best < 4) break;
            byte code = free[fpos++];
            byte a = (byte)(pair & 0xFF), b = (byte)(pair >> 8);
            int m = 0, j = 0;
            while (j < n)
            {
                if (j + 1 < n && buf[j] == a && buf[j + 1] == b) { tmp[m++] = code; j += 2; }
                else tmp[m++] = buf[j++];
            }
            tmp[..m].CopyTo(buf);
            n = m;
            left[code] = a;
            right[code] = b;
        }
        Table(outs, left, right);
        outs.WriteByte((byte)(n & 0xFF));
        outs.WriteByte((byte)(n >> 8));
        outs.Write(buf[..n]);
    }

    /// <summary>Gage pair table: a byte &gt;127 skips (byte-127) identity codes and is followed by one entry; a byte
    /// &lt;=127 is followed by byte+1 entries. An entry is left[c], plus right[c] when left[c] != c.</summary>
    static void Table(MemoryStream o, ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        int c = 0;
        while (c < 256)
        {
            int skip = 0;
            while (c + skip < 256 && left[c + skip] == c + skip && skip < 128) skip++;
            if (skip > 0)
            {
                o.WriteByte((byte)(127 + skip));
                c += skip;
                if (c == 256) break;
                o.WriteByte(left[c]);
                if (left[c] != c) o.WriteByte(right[c]);
                c++;
                continue;
            }
            int run = 0;
            while (c + run < 256 && left[c + run] != c + run && run < 128) run++;
            o.WriteByte((byte)(run - 1));
            for (int k = c; k < c + run; k++) { o.WriteByte(left[k]); o.WriteByte(right[k]); }
            c += run;
        }
    }
}
