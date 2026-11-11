namespace PortIT.Engine.Util;

/// <summary>NumPy's arithmetic where its order matters for the exact result</summary>
public static class Np
{
    /// <summary>numpy's float64 sum over a contiguous run (pairwise_sum in loops_utils.h): under 8 values one by one from
    /// 0; up to 128 eight running sums combined ((r0+r1)+(r2+r3))+((r4+r5)+(r6+r7)) plus the rest; longer halves (the
    /// first a multiple of 8)</summary>
    public static double Sum(ReadOnlySpan<double> a)
    {
        int n = a.Length;
        if (n < 8)
        {
            double res = 0.0;
            for (int i = 0; i < n; i++) res += a[i];
            return res;
        }
        if (n <= 128)
        {
            double r0 = a[0], r1 = a[1], r2 = a[2], r3 = a[3], r4 = a[4], r5 = a[5], r6 = a[6], r7 = a[7];
            int i;
            for (i = 8; i < n - (n % 8); i += 8)
            {
                r0 += a[i]; r1 += a[i + 1]; r2 += a[i + 2]; r3 += a[i + 3];
                r4 += a[i + 4]; r5 += a[i + 5]; r6 += a[i + 6]; r7 += a[i + 7];
            }
            double res = ((r0 + r1) + (r2 + r3)) + ((r4 + r5) + (r6 + r7));
            for (; i < n; i++) res += a[i];
            return res;
        }
        int n2 = n / 2;
        n2 -= n2 % 8;
        return Sum(a[..n2]) + Sum(a[n2..]);
    }

    static readonly double[] Pow10 = [1, 10, 100, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8];

    /// <summary>np.round(x, d) for float64: x * 10^d, rint (half to even), / 10^d</summary>
    public static double Round(double x, int d) => Math.Round(x * Pow10[d], MidpointRounding.ToEven) / Pow10[d];

    /// <summary>Python's round(x, d): the exact binary value rounded half-to-even at d decimals, then the nearest double
    /// to that decimal (not np.round: 0.015625 -&gt; 0.01562 here, and no x * 10^d rounding on the way)</summary>
    public static double PyRound(double x, int d)
    {
        if (double.IsNaN(x) || double.IsInfinity(x) || x == 0) return x;
        long bits = BitConverter.DoubleToInt64Bits(x);
        bool neg = bits < 0;
        int exp = (int)((bits >> 52) & 0x7FF);
        long man = bits & 0xFFFFFFFFFFFFFL;
        if (exp == 0) exp = 1; else man |= 1L << 52;
        int e2 = exp - 1075;                         // |x| = man * 2^e2
        var num = new System.Numerics.BigInteger(man) * System.Numerics.BigInteger.Pow(10, d);
        System.Numerics.BigInteger q;
        if (e2 >= 0) q = num << e2;
        else
        {
            var den = System.Numerics.BigInteger.One << -e2;
            q = System.Numerics.BigInteger.DivRem(num, den, out var rem);
            var twice = rem * 2;
            if (twice > den || (twice == den && !q.IsEven)) q += 1;
        }
        double r = double.Parse(q.ToString() + "E-" + d, System.Globalization.CultureInfo.InvariantCulture);
        return neg ? -r : r;
    }
}

/// <summary>NumPy / OpenBLAS small matrix products as they come out on this PC (measured against NumPy's results:
/// fused multiply-add chains from 0 in these orders)</summary>
public static class Blas
{
    /// <summary>a @ b, n x n (n = 3 or 4; row-major): c_ij = fma over k = 0 .. n-1</summary>
    public static double[] MatMul(double[] a, double[] b, int n)
    {
        var c = new double[n * n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double s = 0.0;
                for (int k = 0; k < n; k++) s = Math.FusedMultiplyAdd(a[i * n + k], b[k * n + j], s);
                c[i * n + j] = s;
            }
        return c;
    }

    /// <summary>A @ v for a 3 x 3 A (row-major, a slice of a bigger matrix allowed by stride): fma order 1, 0, 2</summary>
    public static double[] MatVec3(double[] a, int stride, double[] v)
    {
        var o = new double[3];
        for (int i = 0; i < 3; i++)
        {
            double s = 0.0;
            s = Math.FusedMultiplyAdd(a[i * stride + 1], v[1], s);
            s = Math.FusedMultiplyAdd(a[i * stride + 0], v[0], s);
            s = Math.FusedMultiplyAdd(a[i * stride + 2], v[2], s);
            o[i] = s;
        }
        return o;
    }

    /// <summary>A.T @ v for a 3 x 3 A (row-major, by stride): fma order 0, 1, 2</summary>
    public static double[] MatTVec3(double[] a, int stride, double[] v)
    {
        var o = new double[3];
        for (int i = 0; i < 3; i++)
        {
            double s = 0.0;
            for (int k = 0; k < 3; k++) s = Math.FusedMultiplyAdd(a[k * stride + i], v[k], s);
            o[i] = s;
        }
        return o;
    }

    /// <summary>1-D dot (np.dot / @ of two vectors): fma chain from 0</summary>
    public static double Dot(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        double s = 0.0;
        for (int i = 0; i < a.Length; i++) s = Math.FusedMultiplyAdd(a[i], b[i], s);
        return s;
    }

    /// <summary>np.linalg.norm of a vector: sqrt of the fma dot</summary>
    public static double Norm(ReadOnlySpan<double> a) => Math.Sqrt(Dot(a, a));
}

/// <summary>row-wise products on n x 3 arrays, as NumPy / OpenBLAS do them here (measured for 5 - 1000 rows)</summary>
public static class Rows
{
    /// <summary>A @ x for an n x 3 A (row-major) and a 3-vector: per row fma order 1, 0, 2 from 0</summary>
    public static double[] TimesVec(double[] A, double[] x)
    {
        int n = A.Length / 3;
        var o = new double[n];
        for (int i = 0; i < n; i++)
            o[i] = Math.FusedMultiplyAdd(A[3 * i + 2], x[2], Math.FusedMultiplyAdd(A[3 * i], x[0], Math.FusedMultiplyAdd(A[3 * i + 1], x[1], 0.0)));
        return o;
    }

    /// <summary>A @ B for an n x 3 A and a 3 x 3 B (row-major): fma over k = 0, 1, 2 from 0</summary>
    public static double[] TimesMat(double[] A, double[] B)
    {
        int n = A.Length / 3;
        var o = new double[3 * n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < 3; j++)
            {
                double s = 0.0;
                for (int k = 0; k < 3; k++) s = Math.FusedMultiplyAdd(A[3 * i + k], B[3 * k + j], s);
                o[3 * i + j] = s;
            }
        return o;
    }

    /// <summary>A @ B.T for an n x 3 A and a 3 x 3 B</summary>
    public static double[] TimesMatT(double[] A, double[] B) =>
        TimesMat(A, [B[0], B[3], B[6], B[1], B[4], B[7], B[2], B[5], B[8]]);

    /// <summary>np.einsum('ij,ij->i') over n x 3 arrays: (p0 + p2) + p1</summary>
    public static double[] Dot(double[] A, double[] B)
    {
        int n = A.Length / 3;
        var o = new double[n];
        for (int i = 0; i < n; i++) o[i] = (A[3 * i] * B[3 * i] + A[3 * i + 2] * B[3 * i + 2]) + A[3 * i + 1] * B[3 * i + 1];
        return o;
    }
}
