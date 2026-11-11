using System.Numerics;

namespace PortIT.Engine.Util;

/// <summary>
/// NumPy's np.random.default_rng(seed).choice(..., replace=False) - PCG64 (XSL-RR 128/64), its buffered 32-bit draws,
/// Lemire's bounded integers and the tail-shuffle / Floyd selection, as numpy/random/_generator.pyx and
/// distributions.c do them. Only seed 0 is needed (k26_face.fit_face): its state is NumPy's own, taken from
/// default_rng(0).bit_generator.state, so SeedSequence's hashing is not reimplemented.
/// </summary>
public sealed class NpRandom
{
    static readonly UInt128 Mult = new(0x2360ED051FC65DA4, 0x4385DF649FCCF645);
    UInt128 state, inc;
    bool hasUint32;
    uint uinteger;

    public static NpRandom Seed0() => new()
    {
        state = UInt128.Parse("35399562948360463058890781895381311971"),
        inc = UInt128.Parse("87136372517582989555478159403783844777"),
    };

    /// <summary>default_rng(7) (brock_model.thin_cards): NumPy's own state for seed 7</summary>
    public static NpRandom Seed7() => new()
    {
        state = UInt128.Parse("208745520555909116978795849195383758904"),
        inc = UInt128.Parse("261136684632268670825940853076396136793"),
    };

    /// <summary>default_rng(1) (full_port.checks): NumPy's own state for seed 1</summary>
    public static NpRandom Seed1() => new()
    {
        state = UInt128.Parse("207833532711051698738587646355624148094"),
        inc = UInt128.Parse("194290289479364712180083596243593368443"),
    };

    ulong Next64()
    {
        state = unchecked(state * Mult + inc);
        ulong hi = (ulong)(state >> 64), lo = (ulong)state;
        int rot = (int)(state >> 122);
        return BitOperations.RotateRight(hi ^ lo, rot);
    }

    uint Next32()
    {
        if (hasUint32) { hasUint32 = false; return uinteger; }
        ulong next = Next64();
        hasUint32 = true;
        uinteger = (uint)(next >> 32);
        return (uint)(next & 0xFFFFFFFF);
    }

    /// <summary>random_bounded_uint64(off 0, rng, unmasked): a value in [0, rng]</summary>
    public ulong Bounded(ulong rng)
    {
        if (rng == 0) return 0;
        if (rng <= 0xFFFFFFFF)
        {
            if (rng == 0xFFFFFFFF) return Next32();
            uint rngExcl = (uint)rng + 1;
            ulong m = (ulong)Next32() * rngExcl;
            uint leftover = (uint)(m & 0xFFFFFFFF);
            if (leftover < rngExcl)
            {
                uint threshold = (uint.MaxValue - (uint)rng) % rngExcl;
                while (leftover < threshold)
                {
                    m = (ulong)Next32() * rngExcl;
                    leftover = (uint)(m & 0xFFFFFFFF);
                }
            }
            return m >> 32;
        }
        throw new NotSupportedException("64-bit bounded draws are not needed here");
    }

    /// <summary>random_interval(max): a value in [0, max] by masked rejection (Generator.shuffle's draw)</summary>
    public ulong Interval(ulong max)
    {
        if (max == 0) return 0;
        ulong mask = max;
        mask |= mask >> 1; mask |= mask >> 2; mask |= mask >> 4; mask |= mask >> 8; mask |= mask >> 16; mask |= mask >> 32;
        ulong value;
        if (max <= 0xFFFFFFFF) { while ((value = Next32() & mask) > max) { } }
        else { while ((value = Next64() & mask) > max) { } }
        return value;
    }

    /// <summary>Generator.shuffle(x) of a list (Fisher-Yates from the end, random_interval draws)</summary>
    public void Shuffle<T>(IList<T> x)
    {
        for (int i = x.Count - 1; i >= 1; i--)
        {
            int j = (int)Interval((ulong)i);
            (x[i], x[j]) = (x[j], x[i]);
        }
    }

    void ShuffleInt(long n, long first, long[] data, int offset)
    {
        for (long i = n - 1; i >= first; i--)
        {
            long j = (long)Bounded((ulong)i);
            (data[offset + j], data[offset + i]) = (data[offset + i], data[offset + j]);
        }
    }

    /// <summary>Generator.choice(pop, size, replace=False, shuffle=True) -&gt; the chosen indices into pop</summary>
    public long[] ChooseIndices(long popSize, long size)
    {
        if (size > popSize) throw new ArgumentException("size > population");
        const int cutoff = 50;
        long[] idx;
        // (measured against NumPy: the tail shuffle only for a big population with a big sample; Floyd otherwise)
        if (popSize > 10000 && size > popSize / cutoff)
        {
            // (tail shuffle: the last `size` places of a partly shuffled arange)
            var all = new long[popSize];
            for (long i = 0; i < popSize; i++) all[i] = i;
            ShuffleInt(popSize, Math.Max(popSize - size, 1), all, 0);
            return all[(int)(popSize - size)..];      // (already in shuffled order: no second shuffle on this path)
        }
        else
        {
            // (Floyd's algorithm with a hash set)
            idx = new long[size];
            ulong setSize = (ulong)(1.2 * size);
            ulong mask = setSize;
            mask |= mask >> 1; mask |= mask >> 2; mask |= mask >> 4; mask |= mask >> 8; mask |= mask >> 16; mask |= mask >> 32;
            var hash = new ulong[mask + 1];
            Array.Fill(hash, ulong.MaxValue);
            for (long j = popSize - size; j < popSize; j++)
            {
                ulong val = Bounded((ulong)j);
                ulong loc = val & mask;
                while (hash[loc] != ulong.MaxValue && hash[loc] != val) loc = (loc + 1) & mask;
                if (hash[loc] == ulong.MaxValue) { hash[loc] = val; idx[j - popSize + size] = (long)val; }
                else
                {
                    loc = (ulong)j & mask;
                    while (hash[loc] != ulong.MaxValue) loc = (loc + 1) & mask;
                    hash[loc] = (ulong)j;
                    idx[j - popSize + size] = j;
                }
            }
        }
        ShuffleInt(size, 1, idx, 0);       // (shuffle=True: the result's order)
        return idx;
    }
}
