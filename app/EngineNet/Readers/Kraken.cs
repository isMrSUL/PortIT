// A C# translation of the Kraken part of lztans 0.1.1 (https://github.com/patodois/lztans), for PortIT
// 2026-11-12: AEW Fight Forever's IoStore blocks and WWE 2K19 - 2K22 packs are Kraken, and people without a WWE 2K
// game had no oo2core DLL to unpack them. lztans was written from a prose description of the block format, not from
// any existing decoder (see its README). Mermaid / Selkie / Leviathan, LZNA, BitKnit and checksummed blocks are not
// translated: they are refused (NotSupportedException).
//
// lztans' licence (MIT OR Apache-2.0; used here under MIT):
//
// MIT License
//
// Copyright (c) 2026 patodois
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System.Numerics;

namespace PortIT.Engine.Readers;

/// <summary>Kraken block-stream decoder (no Oodle DLL): <see cref="Decode"/>.</summary>
public static class Kraken
{
    const int Block = 262_144, Subchunk = 131_072, MaxDepth = 24, MaxArray = 1 << 18;

    static InvalidDataException Corrupt(string what) => new("Kraken stream: " + what);
    static InvalidDataException Truncated() => new("Kraken stream: input ended unexpectedly");

    // ---------------------------------------------------------------- bit readers
    static ulong Load8Be(ReadOnlySpan<byte> d, long idx)
    {
        ulong v = 0;
        for (int i = 0; i < 8; i++) { long j = idx + i; v = (v << 8) | (j >= 0 && j < d.Length ? d[(int)j] : 0u); }
        return v;
    }

    static ulong Load8FromEnd(ReadOnlySpan<byte> d, long idx)
    {
        ulong v = 0;
        int len = d.Length;
        for (int i = 0; i < 8; i++) { long j = idx + i; v = (v << 8) | (j < len ? d[(int)(len - 1 - j)] : 0u); }
        return v;
    }

    /// <summary>MSB-first, forward (Fw = false) or from the end backward (Fw = true is forward)</summary>
    sealed class Msb(byte[] data, int off, int len, bool forward)
    {
        long bit;
        ReadOnlySpan<byte> D => data.AsSpan(off, len);
        ulong Window() => (forward ? Load8Be(D, bit >> 3) : Load8FromEnd(D, bit >> 3)) << (int)(bit & 7);
        public uint Peek(int k) => k == 0 ? 0u : (uint)(Window() >> (64 - k));
        public void Consume(int k) => bit += k;
        public uint Read(int k) { uint v = Peek(k); Consume(k); return v; }
        public int? LeadingZeros() { uint w = Peek(32); return w == 0 ? null : BitOperations.LeadingZeroCount(w); }
        public int BytePos => (int)((bit + 7) / 8);
        public bool Overrun => bit > (long)len * 8;
    }

    /// <summary>LSB-first with a 64-bit buffer, forward or from the end backward</summary>
    sealed class Lsb
    {
        readonly byte[] data; readonly int off, len; readonly bool forward;
        long pos; ulong buf; int cnt;
        public Lsb(byte[] data, int off, int len, bool forward) { this.data = data; this.off = off; this.len = len; this.forward = forward; Refill(); }
        public void Refill()
        {
            while (cnt <= 56)
            {
                byte b = forward ? (pos < len ? data[off + pos] : (byte)0) : (pos < len ? data[off + len - 1 - pos] : (byte)0);
                buf |= (ulong)b << cnt;
                cnt += 8;
                pos++;
            }
        }
        public ulong Bits => buf;
        public void Consume(int k) { buf >>= k; cnt = Math.Max(0, cnt - k); }
        public uint Read(int k) { Refill(); uint v = (uint)(buf & ((1UL << k) - 1)); Consume(k); return v; }
        public long BitsUsed => pos * 8 - cnt;
        public int BytePos => (int)((BitsUsed + 7) / 8);
    }

    // ---------------------------------------------------------------- the stream
    /// <summary>decode one stream into dst (its length = the decompressed size) -&gt; input bytes consumed</summary>
    public static int Decode(ReadOnlySpan<byte> srcSpan, Span<byte> dstSpan)
    {
        byte[] src = srcSpan.ToArray();
        byte[] dst = new byte[dstSpan.Length];
        int n = Decode(src, dst);
        dst.CopyTo(dstSpan);
        return n;
    }

    public static int Decode(byte[] src, byte[] dst)
    {
        int total = dst.Length, ip = 0, produced = 0, bas = 0;
        while (produced < total)
        {
            int left = Math.Min(total - produced, Block);
            if (ip + 2 > src.Length) throw Truncated();
            byte b0 = src[ip], b1 = src[ip + 1];
            ip += 2;
            if ((b0 & 0x0F) != 0x0C || (b0 & 0x30) != 0) throw Corrupt("bad block header");
            if ((b1 & 0x80) != 0) throw new NotSupportedException("Kraken stream: checksummed blocks");
            switch (b1 & 0x7F)
            {
                case 0x06: break;
                case 0x0A or 0x0C: throw new NotSupportedException("Mermaid / Selkie / Leviathan blocks (only Kraken is decoded)");
                case 0x05 or 0x0B: throw new NotSupportedException("LZNA and BitKnit blocks");
                default: throw Corrupt("unknown codec id");
            }
            if ((b0 & 0x80) != 0) bas = produced;
            if ((b0 & 0x40) != 0)
            {
                if (ip + left > src.Length) throw Truncated();
                Array.Copy(src, ip, dst, produced, left);
                ip += left; produced += left;
                continue;
            }
            if (ip + 3 > src.Length) throw Truncated();
            int v = src[ip] << 16 | src[ip + 1] << 8 | src[ip + 2];
            ip += 3;
            int size = v & 0x3FFFF;
            if (size != 0x3FFFF)
            {
                if (ip + size + 1 > src.Length) throw Truncated();
                DecodePayload(src, ip, size + 1, dst, produced, left, bas);
                ip += size + 1;
            }
            else if (v >> 18 == 1)
            {
                if (ip >= src.Length) throw Truncated();
                dst.AsSpan(produced, left).Fill(src[ip]);
                ip++;
            }
            else throw Corrupt("bad quantum header");
            produced += left;
        }
        return ip;
    }

    static void DecodePayload(byte[] src, int poff, int plen, byte[] dst, int blockStart, int left, int bas)
    {
        int q = 0, d = 0;
        while (d < left)
        {
            int subLen = Math.Min(left - d, Subchunk);
            if (plen - q < 4) throw Truncated();
            int h = src[poff + q] << 16 | src[poff + q + 1] << 8 | src[poff + q + 2];
            int start = blockStart + d;
            if ((h & 0x80_0000) == 0)
            {
                var (arr, used) = ReadArray(src, poff + q, plen - q, subLen, 0);
                if (arr.Length != subLen) throw Corrupt("entropy sub-chunk has the wrong size");
                arr.CopyTo(dst, start);
                q += used;
            }
            else
            {
                int mode = (h >> 19) & 0xF, n = h & 0x7FFFF;
                if (q + 3 + n > plen) throw Truncated();
                if (n < subLen) DecodeLz(src, poff + q + 3, n, mode, dst, bas, start, start + subLen);
                else if (n == subLen && mode == 0) Array.Copy(src, poff + q + 3, dst, start, subLen);
                else throw Corrupt("bad sub-chunk header");
                q += 3 + n;
            }
            d += subLen;
        }
        if (q != plen) throw Corrupt("quantum payload not exactly consumed");
    }

    // ---------------------------------------------------------------- entropy-coded arrays
    /// <summary>one array from src[off..off+len] -&gt; (bytes, input bytes used)</summary>
    static (byte[] Data, int Used) ReadArray(byte[] src, int off, int len, int limit, int depth)
    {
        if (depth > MaxDepth) throw Corrupt("arrays nested too deeply");
        if (len < 1) throw Truncated();
        int h0 = src[off];
        int ty = (h0 >> 4) & 7;
        if (ty == 0)
        {
            int size, hoff;
            if (h0 >= 0x80)
            {
                if (len < 2) throw Truncated();
                size = (h0 << 8 | src[off + 1]) & 0x0FFF; hoff = 2;
            }
            else
            {
                if (len < 3) throw Truncated();
                int vv = h0 << 16 | src[off + 1] << 8 | src[off + 2];
                if (vv >> 18 != 0) throw Corrupt("stored array: size field has high bits");
                size = vv; hoff = 3;
            }
            if (size > limit) throw Corrupt("array larger than its limit");
            if (hoff + size > len) throw Truncated();
            return (src.AsSpan(off + hoff, size).ToArray(), hoff + size);
        }
        if (ty > 5) throw Corrupt("invalid array type");
        int bodyLen, outLen, hdr;
        if (h0 >= 0x80)
        {
            if (len < 3) throw Truncated();
            int b = h0 << 16 | src[off + 1] << 8 | src[off + 2];
            int s = b & 0x3FF;
            bodyLen = s; outLen = s + ((b >> 10) & 0x3FF) + 1; hdr = 3;
        }
        else
        {
            if (len < 5) throw Truncated();
            long c = (long)src[off + 1] << 24 | (long)src[off + 2] << 16 | (long)src[off + 3] << 8 | src[off + 4];
            int s = (int)(c & 0x3FFFF);
            int dd = (int)((((long)(h0 & 0x0F) << 14) | (c >> 18)) + 1);
            if (s >= dd) throw Corrupt("array: source size not below decoded size");
            bodyLen = s; outLen = dd; hdr = 5;
        }
        if (outLen > limit) throw Corrupt("array larger than its limit");
        if (hdr + bodyLen > len) throw Truncated();
        int bo = off + hdr;
        byte[] data = ty switch
        {
            1 => Tans(src, bo, bodyLen, outLen),
            2 => Huffman(src, bo, bodyLen, outLen, false),
            3 => Rle(src, bo, bodyLen, outLen, depth),
            4 => Huffman(src, bo, bodyLen, outLen, true),
            _ => Concat(src, bo, bodyLen, outLen, depth),
        };
        return (data, hdr + bodyLen);
    }

    static byte[] Concat(byte[] src, int off, int len, int outLen, int depth)
    {
        if (len < 1) throw Truncated();
        int b = src[off], n = b & 0x7F;
        if (n < 2 || len < 6) throw Corrupt("concatenated array: count or size too small");
        if ((b & 0x80) == 0)
        {
            var o = new List<byte>(outLen);
            int pos = 1;
            for (int i = 0; i < n; i++)
            {
                var (a, used) = ReadArray(src, off + pos, len - pos, outLen - o.Count, depth + 1);
                o.AddRange(a);
                pos += used;
            }
            if (o.Count != outLen || pos != len) throw Corrupt("concatenated array: size mismatch");
            return [.. o];
        }
        var (outs, used2) = ReadSliced(src, off, len, 1, outLen, depth);
        var r = outs.Count > 0 ? outs[^1] : [];
        if (r.Length != outLen || used2 != len) throw Corrupt("sliced container: size mismatch");
        return r;
    }

    static byte[] Rle(byte[] src, int off, int len, int outLen, int depth)
    {
        if (len == 0) throw Corrupt("rle: empty body");
        if (len == 1) { var f = new byte[outLen]; Array.Fill(f, src[off]); return f; }
        byte[] w;
        if (src[off] != 0)
        {
            var (arr, n) = ReadArray(src, off, len, MaxArray, depth + 1);
            w = new byte[arr.Length + len - n];
            arr.CopyTo(w, 0);
            Array.Copy(src, off + n, w, arr.Length, len - n);
        }
        else w = src.AsSpan(off + 1, len - 1).ToArray();
        var o = new byte[outLen];
        int op = 0, lit = 0, end = w.Length;
        byte fill = 0;
        while (lit < end)
        {
            int cmd = w[end - 1], copy, run;
            if (cmd == 0 || cmd > 0x2F)
            {
                end -= 1;
                copy = ~cmd & 15; run = cmd >> 4;
            }
            else if (cmd >= 0x10)
            {
                if (end - lit < 2) throw Corrupt("rle: truncated command");
                int t = (cmd << 8 | w[end - 2]) - 4096;
                end -= 2;
                copy = t & 63; run = t >> 6;
            }
            else if (cmd == 1)
            {
                end -= 1;
                if (lit >= end) throw Corrupt("rle: missing literal for run byte");
                fill = w[lit++];
                continue;
            }
            else if (cmd >= 9)
            {
                if (end - lit < 2) throw Corrupt("rle: truncated command");
                int vv = cmd << 8 | w[end - 2];
                end -= 2;
                copy = 0; run = (vv - 0x8FF) * 128;
            }
            else
            {
                if (end - lit < 2) throw Corrupt("rle: truncated command");
                int vv = cmd << 8 | w[end - 2];
                end -= 2;
                copy = (vv - 511) * 64; run = 0;
            }
            if (copy > end - lit) throw Corrupt("rle: literal count beyond the data");
            if (copy + run > outLen - op) throw Corrupt("rle: output overrun");
            Array.Copy(w, lit, o, op, copy);
            op += copy; lit += copy;
            o.AsSpan(op, run).Fill(fill);
            op += run;
        }
        if (op != outLen) throw Corrupt("rle: output size mismatch");
        return o;
    }

    static (List<byte[]> Outs, int Used) ReadSliced(byte[] src, int off, int len, int nOut, int limit, int depth)
    {
        if (len < 4) throw Truncated();
        int b = src[off];
        if ((b & 0x80) == 0) throw Corrupt("sliced container: count byte without bit 7");
        int nIn = b & 0x3F, pos = 1;
        if (nIn == 0)
        {
            var outs0 = new List<byte[]>(nOut);
            int left = limit;
            for (int i = 0; i < nOut; i++)
            {
                var (a, n) = ReadArray(src, off + pos, len - pos, left, depth + 1);
                left -= a.Length; pos += n;
                outs0.Add(a);
            }
            return (outs0, pos);
        }
        var sources = new List<byte[]>(nIn);
        int total = 0;
        for (int i = 0; i < nIn; i++)
        {
            var (a, n) = ReadArray(src, off + pos, len - pos, limit - total, depth + 1);
            total += a.Length; pos += n;
            sources.Add(a);
        }
        if (len < pos + 2) throw Truncated();
        int q = src[off + pos] | src[off + pos + 1] << 8;
        pos += 2;
        int vb = q & 0x3FFF;
        bool combined = (q & 0x8000) != 0;
        byte[] index, exps;
        int nExp;
        if (combined)
        {
            var (a, n) = ReadArray(src, off + pos, len - pos, total, depth + 1);
            pos += n;
            index = a.Select(t => (byte)(t & 15)).ToArray();
            exps = a.Select(t => (byte)(t >> 4)).ToArray();
            nExp = index.Length;
        }
        else
        {
            var (a, n) = ReadArray(src, off + pos, len - pos, total, depth + 1);
            pos += n;
            if (a.Length < nOut) throw Corrupt("sliced container: fewer intervals than output arrays");
            nExp = a.Length - nOut;
            var (e, n2) = ReadArray(src, off + pos, len - pos, nExp, depth + 1);
            pos += n2;
            if (e.Length != nExp) throw Corrupt("sliced container: exponent count");
            if (e.Any(x => x > 16)) throw Corrupt("sliced container: exponent above 16");
            index = a; exps = e;
        }
        if (index.Length > total) throw Corrupt("sliced container: too many intervals");
        if (vb > len - pos) throw Truncated();
        int so = off + pos;
        pos += vb;
        if (index.Length == 0 || index[^1] != 0) throw Corrupt("sliced container: last index is not zero");
        var fwd = new Msb(src, so, vb, true);
        var bwd = new Msb(src, so, vb, false);
        var lens = new List<int>(nExp);
        for (int i = 0; i < Math.Min(nExp, exps.Length); i++)
        {
            int e = exps[i];
            uint r = i % 2 == 0 ? fwd.Read(e) : bwd.Read(e);
            lens.Add((1 << e) + (int)r);
        }
        if (fwd.Overrun || bwd.Overrun) throw Corrupt("sliced container: interval length streams overrun");
        var cursors = new int[nIn];
        int ci = 0, cl = 0;
        var outs = new List<byte[]>(nOut);
        for (int k = 0; k < nOut; k++)
        {
            var o = new List<byte>();
            while (true)
            {
                if (ci >= index.Length) throw Corrupt("sliced container: index list ended");
                int s = index[ci++];
                if (s == 0) break;
                if (s > nIn) throw Corrupt("sliced container: source index out of range");
                if (cl >= lens.Count) throw Corrupt("sliced container: length list ended");
                int ln = lens[cl++];
                int c = cursors[s - 1];
                var sa = sources[s - 1];
                if (ln > sa.Length - c) throw Corrupt("sliced container: interval beyond its source");
                o.AddRange(sa.AsSpan(c, ln).ToArray());
                cursors[s - 1] = c + ln;
            }
            if (combined) cl++;
            outs.Add([.. o]);
        }
        if (ci != index.Length || cl != nExp) throw Corrupt("sliced container: unused intervals");
        for (int i = 0; i < nIn; i++) if (cursors[i] != sources[i].Length) throw Corrupt("sliced container: source array not fully used");
        return (outs, pos);
    }

    // ---------------------------------------------------------------- Huffman
    const int MaxLen = 11, TableSize = 1 << MaxLen;

    static int Zigzag(uint v) => (int)(v >> 1) ^ -(int)(v & 1);

    static uint ReadUnary(Msb r)
    {
        uint total = 0;
        while (true)
        {
            var z = r.LeadingZeros();
            if (z is int zz)
            {
                r.Consume(zz + 1);
                total += (uint)zz;
                if (total >= 4096) throw Corrupt("unary count too large");
                return total;
            }
            r.Consume(32);
            total += 32;
            if (total >= 4096) throw Corrupt("unary count too large");
        }
    }

    static uint ReadTruncatedBinary(Msb r, uint x)
    {
        int y = 32 - BitOperations.LeadingZeroCount(x - 1);
        uint u = (1u << y) - x;
        uint v = r.Peek(y);
        if ((v >> 1) >= u) { r.Consume(y); return v - u; }
        r.Consume(y - 1);
        return v >> 1;
    }

    static List<byte> ReadSymbolRanges(Msb r, int ns, ReadOnlySpan<uint> extras)
    {
        var syms = new List<byte>(ns);
        int sym = 0, used = 0, j = 0, x = extras.Length;
        if (x % 2 == 1)
        {
            uint v = extras[0];
            j = 1;
            if (v >= 8) throw Corrupt("symbol range: initial gap exponent");
            sym = (int)r.Read((int)v + 1) + (1 << ((int)v + 1)) - 1;
        }
        for (int i = 0; i < x / 2; i++)
        {
            uint v1 = extras[j++];
            if (v1 >= 9) throw Corrupt("symbol range: count exponent");
            int count = (int)r.Read((int)v1) + (1 << (int)v1);
            uint v2 = extras[j++];
            if (v2 >= 8) throw Corrupt("symbol range: gap exponent");
            int gap = (int)r.Read((int)v2 + 1) + (1 << ((int)v2 + 1)) - 1;
            if (sym + count > 256) throw Corrupt("symbol range beyond 255");
            for (int s = sym; s < sym + count; s++) syms.Add((byte)s);
            used += count;
            sym += count + gap;
        }
        if (sym >= 256 || used >= ns || sym + (ns - used) > 256) throw Corrupt("symbol range: inconsistent");
        for (int s = sym; s < sym + (ns - used); s++) syms.Add((byte)s);
        return syms;
    }

    static int Lz(Msb r) => r.LeadingZeros() ?? throw Corrupt("too many leading zero bits");

    static uint ReadGd(Msb r)
    {
        if (r.Peek(8) == 0) throw Corrupt("doubled-gamma code starts with eight zero bits");
        int z = Lz(r);
        r.Consume(z + 1);
        uint rest = r.Read(z + 1);
        return (1u << (z + 1)) + rest - 1;
    }

    static void ReadNew(Msb r, List<byte>[] lists)
    {
        int k = (int)r.Read(2);
        int ns = (int)r.Read(8) + 1;
        int xcount = ns == 256 ? 0 : (int)ReadTruncatedBinary(r, (uint)(2 * Math.Min(257 - ns, ns)));
        var u = new uint[ns + xcount];
        for (int i = 0; i < u.Length; i++)
        {
            uint v = ReadUnary(r);
            if (v >= 256) throw Corrupt("huffman: unary count >= 256");
            u[i] = v;
        }
        var c = u.AsSpan(0, ns).ToArray();
        if (k > 0) for (int i = 0; i < c.Length; i++) c[i] = (c[i] << k) | r.Read(k);
        var lens = new int[ns];
        int run = 30;
        for (int i = 0; i < ns; i++)
        {
            int d = Zigzag(c[i]);
            int len = d + (run >> 2) + 1;
            if (len < 1 || len > MaxLen) throw Corrupt("huffman: code length out of range");
            lens[i] = len;
            run += d;
        }
        var syms = ReadSymbolRanges(r, ns, u.AsSpan(ns));
        for (int i = 0; i < Math.Min(syms.Count, ns); i++) lists[lens[i]].Add(syms[i]);
    }

    static void ReadOldSparse(Msb r, List<byte>[] lists)
    {
        int ns = (int)r.Read(8);
        if (ns == 0) throw Corrupt("huffman: empty sparse description");
        if (ns == 1) throw Corrupt("huffman: single-symbol description is not supported");
        int cb = (int)r.Read(3);
        if (cb > 4) throw Corrupt("huffman: sparse length width");
        for (int i = 0; i < ns; i++)
        {
            byte s = (byte)r.Read(8);
            int len = (int)r.Read(cb) + 1;
            if (len > MaxLen) throw Corrupt("huffman: sparse code length");
            lists[len].Add(s);
        }
    }

    static void ReadOldDense(Msb r, List<byte>[] lists)
    {
        int k = (int)r.Read(2);
        int zmax = 20 >> k;
        bool skip = r.Read(1) == 1;
        int sym = 0, coded = 0, mean4 = 32;
        while (sym < 256)
        {
            if (skip) skip = false;
            else
            {
                sym += (int)ReadGd(r);
                if (sym >= 256) break;
            }
            int n = (int)ReadGd(r);
            if (sym + n > 256) throw Corrupt("huffman: dense group beyond 255");
            coded += n;
            for (int i = 0; i < n; i++)
            {
                int z = Lz(r);
                if (z > zmax) throw Corrupt("huffman: dense code too long");
                r.Consume(z + 1);
                uint f = r.Read(k);
                uint v = ((uint)z << k) | f;
                int len = Zigzag(v) + ((mean4 + 2) >> 2);
                if (len < 1 || len > MaxLen) throw Corrupt("huffman: dense code length out of range");
                mean4 = len + ((3 * mean4 + 2) >> 2);
                lists[len].Add((byte)sym);
                sym++;
            }
        }
        if (sym != 256 || coded < 2) throw Corrupt("huffman: dense description inconsistent");
    }

    static ushort[] ReadDescription(Msb r)
    {
        var lists = new List<byte>[MaxLen + 1];
        for (int i = 0; i <= MaxLen; i++) lists[i] = [];
        if (r.Read(1) == 1)
        {
            if (r.Read(1) != 0) throw Corrupt("huffman: invalid description selector");
            ReadNew(r, lists);
        }
        else if (r.Read(1) == 1) ReadOldDense(r, lists);
        else ReadOldSparse(r, lists);
        if (r.Overrun) throw Corrupt("huffman: description runs past the array");
        var entries = new ushort[TableSize];
        int slot = 0;
        for (int len = 1; len <= MaxLen; len++)
        {
            int width = 1 << (MaxLen - len);
            foreach (byte s in lists[len])
            {
                if (slot + width > TableSize) throw Corrupt("huffman: code space overfull");
                ushort e = (ushort)(s | (len << 8));
                for (int sl = slot; sl < slot + width; sl++)
                {
                    int idx = 0;
                    for (int bb = 0; bb < MaxLen; bb++) if ((sl & (1 << bb)) != 0) idx |= 1 << (MaxLen - 1 - bb);   // (the low 11 bits, reversed)
                    entries[idx] = e;
                }
                slot += width;
            }
        }
        if (slot != TableSize) throw Corrupt("huffman: code space not full");
        return entries;
    }

    static byte Dec(Lsb r, ushort[] t)
    {
        r.Refill();
        ushort e = t[(int)(r.Bits & (TableSize - 1))];
        r.Consume(e >> 8);
        return (byte)e;
    }

    static void DecodeThree(byte[] src, int off, int len, byte[] o, int oOff, int oLen, ushort[] t)
    {
        if (len < 2) throw Corrupt("huffman: payload too short");
        int m = src[off] | src[off + 1] << 8;
        if (2 + m > len) throw Corrupt("huffman: stream A length");
        var a = new Lsb(src, off + 2, m, true);
        int bcOff = off + 2 + m, bcLen = len - 2 - m;
        var b = new Lsb(src, bcOff, bcLen, true);
        var c = new Lsb(src, bcOff, bcLen, false);
        int i = 0;
        for (; i + 3 <= oLen; i += 3)
        {
            o[oOff + i] = Dec(a, t);
            o[oOff + i + 1] = Dec(c, t);
            o[oOff + i + 2] = Dec(b, t);
        }
        int rem = oLen - i;
        if (rem > 0)
        {
            o[oOff + i] = Dec(a, t);
            if (rem > 1) o[oOff + i + 1] = Dec(c, t);
        }
        if (a.BytePos != m || b.BytePos + c.BytePos != bcLen) throw Corrupt("huffman: bit streams not exactly consumed");
    }

    static byte[] Huffman(byte[] src, int off, int len, int outLen, bool six)
    {
        var r = new Msb(src, off, len, true);
        var table = ReadDescription(r);
        int p = Math.Min(r.BytePos, len);
        int ro = off + p, rl = len - p;
        var o = new byte[outLen];
        if (!six) DecodeThree(src, ro, rl, o, 0, outLen, table);
        else
        {
            if (rl < 3) throw Corrupt("huffman: payload too short");
            int m = src[ro] | src[ro + 1] << 8 | src[ro + 2] << 16;
            ro += 3; rl -= 3;
            if (m > rl) throw Corrupt("huffman: six-stream split");
            int half = (outLen + 1) / 2;
            DecodeThree(src, ro, m, o, 0, half, table);
            DecodeThree(src, ro + m, rl - m, o, half, outLen - half, table);
        }
        return o;
    }

    // ---------------------------------------------------------------- tANS
    struct TEntry { public byte Symbol, NBits; public ushort Base; }

    static (int Tb, List<(byte S, uint W)> Weights) ReadWeights(Msb r)
    {
        if (r.Read(1) != 0) throw Corrupt("tans: reserved bit set");
        int tb = (int)r.Read(2) + 8;
        uint tableSize = 1u << tb;
        bool golomb = r.Read(1) == 1;
        var o = new List<(byte, uint)>();
        if (golomb)
        {
            int q = (int)r.Read(3);
            int ns = (int)r.Read(8) + 1;
            if (ns < 2) throw Corrupt("tans: fewer than two symbols");
            int xcount = ns == 256 ? 0 : (int)ReadTruncatedBinary(r, (uint)(2 * Math.Min(257 - ns, ns)));
            var u = new uint[ns + xcount];
            for (int i = 0; i < u.Length; i++) u[i] = ReadUnary(r);
            var syms = ReadSymbolRanges(r, ns, u.AsSpan(ns));
            long mean = 6;
            ulong total = 0;
            for (int i = 0; i < syms.Count; i++)
            {
                uint wBits = u[i] + (uint)q;
                if (wBits > 15) throw Corrupt("tans: weight width");
                long v = r.Read((int)wBits) + (1L << (int)wBits) - (1L << q);
                long a4 = mean >> 2;
                long limit = 2 * a4;
                if (v <= limit) v = a4 + Zigzag((uint)v);
                if (limit > v) limit = v;
                long w = v + 1;
                if (w < 1) throw Corrupt("tans: weight below one");
                mean += limit - a4;
                total += (ulong)w;
                if (total > tableSize) throw Corrupt("tans: weights exceed table size");
                o.Add((syms[i], (uint)w));
            }
            if (total != tableSize) throw Corrupt("tans: weights do not fill the table");
        }
        else
        {
            int count = (int)r.Read(3) + 1;
            int bps = 32 - BitOperations.LeadingZeroCount((uint)tb);
            uint mdb = r.Read(bps);
            if (mdb < 1 || mdb > tb) throw Corrupt("tans: sparse weight width");
            var seen = new bool[256];
            uint weight = 0, total = 0;
            for (int i = 0; i < count; i++)
            {
                int s = (int)r.Read(8);
                if (seen[s]) throw Corrupt("tans: repeated symbol");
                seen[s] = true;
                weight += r.Read((int)mdb);
                if (weight == 0) throw Corrupt("tans: zero weight");
                total += weight;
                if (total >= tableSize) throw Corrupt("tans: weights exceed table size");
                o.Add(((byte)s, weight));
            }
            int s2 = (int)r.Read(8);
            if (seen[s2]) throw Corrupt("tans: repeated symbol");
            uint last = tableSize - total;
            if (last < weight || last <= 1) throw Corrupt("tans: last weight");
            o.Add(((byte)s2, last));
            o = o.OrderBy(e => e.Item1).ToList();     // (stable, by symbol: the Rust sort_by_key)
        }
        if (r.Overrun) throw Corrupt("tans: table runs past the array");
        return (tb, o);
    }

    static TEntry[] BuildTans(int tb, List<(byte S, uint W)> weights)
    {
        int size = 1 << tb, mask = size - 1;
        var singles = weights.Where(e => e.W == 1).Select(e => e.S).ToList();
        int sStates = size - singles.Count;
        var table = new TEntry[size];
        for (int i = 0; i < singles.Count; i++) table[sStates + i] = new TEntry { Symbol = singles[i], NBits = (byte)tb, Base = 0 };
        int a = sStates / 4, rem = sStates % 4;
        var rs = new int[4]; var re = new int[4];
        int acc = 0;
        for (int j = 0; j < 4; j++) { rs[j] = acc; acc += a + (j < rem ? 1 : 0); re[j] = acc; }
        var cursor = (int[])rs.Clone();
        int p = 0;
        foreach (var (sym, w0) in weights.Where(e => e.W > 1))
        {
            int w = (int)w0, k = 0;
            for (int j = 0; j < 4; j++)
            {
                int off = (j + 4 - (p % 4)) % 4;
                int cj = w > off ? (w - off + 3) / 4 : 0;
                for (int c = 0; c < cj; c++)
                {
                    if (cursor[j] >= re[j]) throw Corrupt("tans: region overflow");
                    int wv = w + k;
                    int nbits = tb - BitOperations.Log2((uint)wv);
                    table[cursor[j]] = new TEntry { Symbol = sym, NBits = (byte)nbits, Base = (ushort)((wv << nbits) & mask) };
                    cursor[j]++;
                    k++;
                }
            }
            p += w;
        }
        return table;
    }

    static byte[] Tans(byte[] src, int off, int len, int outLen)
    {
        if (outLen < 5 || len < 8) throw Corrupt("tans: array too small");
        var r = new Msb(src, off, len, true);
        var (tb, weights) = ReadWeights(r);
        var t = BuildTans(tb, weights);
        int p = r.BytePos;
        if (p >= len) throw Corrupt("tans: no payload");
        var f = new Lsb(src, off + p, len - p, true);
        var b = new Lsb(src, off + p, len - p, false);
        var st = new int[5];
        st[0] = (int)f.Read(tb); st[1] = (int)b.Read(tb); st[2] = (int)f.Read(tb); st[3] = (int)b.Read(tb); st[4] = (int)f.Read(tb);
        var o = new byte[outLen];
        int main = outLen - 5, i = 0;
        while (i < main)
        {
            bool fromF = (i / 5) % 2 == 0;
            int end = Math.Min(i - i % 5 + 5, main);
            var rd = fromF ? f : b;
            while (i < end)
            {
                int j = i % 5;
                var e = t[st[j]];
                o[i] = e.Symbol;
                rd.Refill();
                int bits = (int)(rd.Bits & ((1UL << e.NBits) - 1));
                rd.Consume(e.NBits);
                st[j] = e.Base + bits;
                i++;
            }
        }
        for (int k = 0; k < 5; k++)
        {
            if (st[k] > 255) throw Corrupt("tans: final state is not a byte");
            o[main + k] = (byte)st[k];
        }
        if (p + f.BytePos != len - b.BytePos) throw Corrupt("tans: bit streams not exactly consumed");
        return o;
    }

    // ---------------------------------------------------------------- LZ (Kraken sub-chunks)
    static (long[] Offsets, uint[] Lens) ReadSideStreams(byte[] src, int off, int len, byte[] oc, byte[]? ox, uint scale, byte[] lc)
    {
        if (len < 1) throw Truncated();
        var fwd = new Msb(src, off, len, true);
        var bwd = new Msb(src, off, len, false);
        int z0 = bwd.LeadingZeros() ?? throw Corrupt("long-length count");
        if (z0 > 10) throw Corrupt("too many long lengths");
        bwd.Consume(z0);
        int nLong = (int)bwd.Read(z0 + 1) - 1;
        if (nLong > 512) throw Corrupt("too many long lengths");
        var offsets = new long[oc.Length];
        for (int i = 0; i < oc.Length; i++)
        {
            var rr = i % 2 == 0 ? fwd : bwd;
            byte c = oc[i];
            ulong dist;
            if (scale == 0)
            {
                if (c < 0xF0)
                {
                    int n = (c >> 4) + 4;
                    ulong m = (1UL << n) + rr.Read(n);
                    dist = (m << 4) + (ulong)(c & 15) - 248;
                }
                else
                {
                    int n = c - 0xF0 + 4;
                    ulong m = (1UL << n) + rr.Read(n);
                    ulong low = rr.Read(12);
                    dist = 8_322_816 + (m << 12) + low;
                }
            }
            else
            {
                int nb = c >> 3;
                if (nb > 26) throw Corrupt("scaled offset: too many bits");
                ulong o = ((ulong)(8 + (c & 7)) << nb) | rr.Read(nb);
                ulong od = o - 8;
                ulong d;
                if (scale == 1) d = od;
                else
                {
                    if (ox == null || i >= ox.Length) throw Corrupt("offset extras");
                    d = od * scale + ox[i];
                }
                if (d == 0) throw Corrupt("scaled offset: zero distance");
                dist = d;
            }
            if (dist > int.MaxValue) throw Corrupt("offset too large");
            offsets[i] = (long)dist;
        }
        var longs = new uint[nLong];
        for (int i = 0; i < nLong; i++)
        {
            var rr = i % 2 == 0 ? fwd : bwd;
            int z = rr.LeadingZeros() ?? throw Corrupt("long length code");
            if (z > 12) throw Corrupt("long length code too long");
            rr.Consume(z);
            longs[i] = rr.Read(z + 7) - 64;
        }
        if (fwd.Overrun || bwd.Overrun || fwd.BytePos + bwd.BytePos != len) throw Corrupt("offset/length bit streams not exactly consumed");
        var lens = new uint[lc.Length];
        int li = 0;
        for (int i = 0; i < lc.Length; i++)
        {
            uint v;
            if (lc[i] == 255)
            {
                if (li >= longs.Length) throw Corrupt("long lengths exhausted");
                v = longs[li++] + 255;
            }
            else v = lc[i];
            lens[i] = v + 3;
        }
        if (li != longs.Length) throw Corrupt("unused long lengths");
        return (offsets, lens);
    }

    static void CopyMatch(byte[] o, int p, long dist, int len, int bas, int limit)
    {
        if (p < bas || dist == 0 || dist > p - bas) throw Corrupt("match reaches before the window");
        if (len > limit - p) throw Corrupt("match runs past the end of its sub-chunk");
        if (dist < 8 && len > dist) throw Corrupt("overlapping match with distance below 8");
        int s = p - (int)dist;
        if (dist >= len) Array.Copy(o, s, o, p, len);
        else
        {
            int done = 0;
            while (done < len)
            {
                int n = (int)Math.Min(dist, len - done);
                Array.Copy(o, s + done, o, p + done, n);
                done += n;
            }
        }
    }

    static void CopyRaw(byte[] o, int p, byte[] lit, ref int cur, int n, int limit)
    {
        if (n > limit - p || n > lit.Length - cur) throw Corrupt("literal run beyond its array or sub-chunk");
        Array.Copy(lit, cur, o, p, n);
        cur += n;
    }

    static void CopyDelta(byte[] o, int p, byte[] lit, ref int cur, int n, long dist, int bas, int limit)
    {
        if (n == 0) return;
        if (n > limit - p || n > lit.Length - cur) throw Corrupt("literal run beyond its array or sub-chunk");
        if (dist < 8 && n > dist) throw Corrupt("delta literal run longer than a last distance below 8");
        if (dist == 0 || p < bas || dist > p - bas) throw Corrupt("delta literal reaches before the window");
        int d = (int)dist;
        for (int i = 0; i < n; i++) o[p + i] = (byte)(lit[cur + i] + o[p + i - d]);
        cur += n;
    }

    static void DecodeLz(byte[] src, int off, int len, int mode, byte[] o, int bas, int start, int end)
    {
        if (mode > 1) throw Corrupt("kraken: invalid mode");
        int subLen = end - start, pos = 0;
        if (start == bas)
        {
            if (subLen < 8) throw Corrupt("sub-chunk too short for its eight raw bytes");
            if (len < 8) throw Truncated();
            Array.Copy(src, off, o, start, 8);
            pos = 8;
        }
        int p = start + pos;
        if (pos >= len) throw Truncated();
        if ((src[off + pos] & 0x80) != 0) throw Corrupt("kraken: literal array with bit 7 set");
        var (lit, n1) = ReadArray(src, off + pos, len - pos, subLen, 0);
        pos += n1;
        var (cmd, n2) = ReadArray(src, off + pos, len - pos, subLen, 0);
        pos += n2;
        if (len - pos < 3) throw Truncated();
        int x = src[off + pos];
        uint scale = 0;
        if ((x & 0x80) != 0) { pos++; scale = (uint)x - 127; }
        var (oc, n3) = ReadArray(src, off + pos, len - pos, cmd.Length, 0);
        pos += n3;
        byte[]? ox = null;
        if (scale >= 2)
        {
            var (a, n4) = ReadArray(src, off + pos, len - pos, oc.Length, 0);
            pos += n4;
            if (a.Length != oc.Length) throw Corrupt("kraken: offset extras length");
            ox = a;
        }
        var (lc, n5) = ReadArray(src, off + pos, len - pos, subLen >> 2, 0);
        pos += n5;
        var (offsets, lens) = ReadSideStreams(src, off + pos, len - pos, oc, ox, scale, lc);
        bool delta = mode == 0;
        long r0 = 8, r1 = 8, r2 = 8, lo = 8;
        int litCur = 0, offCur = 0, lenCur = 0;
        foreach (byte f in cmd)
        {
            int litN = f & 3, mlen = (f >> 2) & 15, oidx = f >> 6;
            if (litN == 3)
            {
                if (lenCur >= lens.Length) throw Corrupt("kraken: lengths exhausted");
                litN = (int)lens[lenCur++];
            }
            if (delta) CopyDelta(o, p, lit, ref litCur, litN, lo, bas, end);
            else CopyRaw(o, p, lit, ref litCur, litN, end);
            p += litN;
            long dist;
            if (oidx == 3)
            {
                if (offCur >= offsets.Length) throw Corrupt("kraken: offsets exhausted");
                dist = offsets[offCur++];
                (r0, r1, r2) = (dist, r0, r1);
            }
            else if (oidx == 0) dist = r0;
            else if (oidx == 1) { dist = r1; (r0, r1) = (r1, r0); }
            else { dist = r2; (r0, r1, r2) = (r2, r0, r1); }
            lo = dist;
            if (mlen == 15)
            {
                if (lenCur >= lens.Length) throw Corrupt("kraken: lengths exhausted");
                mlen = 14 + (int)lens[lenCur++];
            }
            else mlen += 2;
            CopyMatch(o, p, dist, mlen, bas, end);
            p += mlen;
        }
        int rest = end - p;
        if (delta) CopyDelta(o, p, lit, ref litCur, rest, lo, bas, end);
        else CopyRaw(o, p, lit, ref litCur, rest, end);
        if (litCur != lit.Length) throw Corrupt("kraken: unused literals");
        if (offCur != offsets.Length) throw Corrupt("kraken: unused offsets");
        if (lenCur != lens.Length) throw Corrupt("kraken: unused lengths");
    }
}
