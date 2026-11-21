using System.Numerics;

namespace NovaSpice.Core;

public sealed class Spectrum
{
    public double[] Freq = Array.Empty<double>();
    public double[] Db = Array.Empty<double>();
    public double Fundamental, FundamentalAmp, Dc, ThdPercent;
    public int N;
}

public static class Dsp
{
    public static int LowerBound(double[] x, double v)
    {
        int lo = 0, hi = x.Length;
        while (lo < hi) { int m = (lo + hi) >> 1; if (x[m] < v) lo = m + 1; else hi = m; }
        return lo;
    }

    public static double Interp(double[] x, double[] y, double v)
    {
        int n = x.Length;
        if (n == 0) return double.NaN;
        if (v <= x[0]) return y[0];
        if (v >= x[n - 1]) return y[n - 1];
        int i = LowerBound(x, v);
        double dx = x[i] - x[i - 1];
        return dx == 0 ? y[i] : y[i - 1] + (y[i] - y[i - 1]) * (v - x[i - 1]) / dx;
    }

    /// <summary>Min, max, time-weighted average and RMS over [lo, hi].</summary>
    public static (double Min, double Max, double Avg, double Rms) Stats(double[] x, double[] y, double lo, double hi)
    {
        int a = LowerBound(x, lo), b = LowerBound(x, hi);
        if (b >= x.Length || x[b] > hi) b--;
        if (b - a < 1) { double v = Interp(x, y, lo); return (v, v, v, Math.Abs(v)); }
        double mn = double.MaxValue, mx = double.MinValue, s1 = 0, s2 = 0;
        for (int i = a; i <= b; i++)
        {
            if (y[i] < mn) mn = y[i];
            if (y[i] > mx) mx = y[i];
            if (i > a)
            {
                double dx = x[i] - x[i - 1];
                s1 += 0.5 * (y[i] + y[i - 1]) * dx;
                s2 += 0.5 * (y[i] * y[i] + y[i - 1] * y[i - 1]) * dx;
            }
        }
        double span = x[b] - x[a];
        return span > 0 ? (mn, mx, s1 / span, Math.Sqrt(Math.Max(0, s2 / span))) : (mn, mx, y[a], Math.Abs(y[a]));
    }

    public static double[] Unwrap(double[] deg)
    {
        var r = (double[])deg.Clone();
        double off = 0;
        for (int i = 1; i < r.Length; i++)
        {
            double d = deg[i] - deg[i - 1];
            if (d > 180) off -= 360; else if (d < -180) off += 360;
            r[i] = deg[i] + off;
        }
        return r;
    }

    public static void Fft(Complex[] a)
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            var wl = new Complex(Math.Cos(ang), Math.Sin(ang));
            for (int i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (int k = 0; k < len / 2; k++)
                {
                    var u = a[i + k]; var v = a[i + k + len / 2] * w;
                    a[i + k] = u + v; a[i + k + len / 2] = u - v;
                    w *= wl;
                }
            }
        }
    }

    /// <summary>Hann-windowed spectrum of a non-uniformly sampled signal (resampled to 2^k points).</summary>
    public static Spectrum Analyze(double[] x, double[] y, double lo, double hi)
    {
        int a = LowerBound(x, lo), b = Math.Min(x.Length, LowerBound(x, hi) + 1);
        int count = Math.Max(2, b - a);
        int n = 1 << Math.Clamp((int)Math.Ceiling(Math.Log2(count)), 10, 18);
        double t0 = Math.Max(lo, x[0]), t1 = Math.Min(hi, x[^1]);
        double dt = (t1 - t0) / n;
        var buf = new Complex[n];
        double wsum = 0;
        for (int i = 0; i < n; i++)
        {
            double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n);
            wsum += w;
            buf[i] = new Complex(Interp(x, y, t0 + i * dt) * w, 0);
        }
        Fft(buf);
        int m = n / 2 + 1;
        var amp = new double[m];
        for (int i = 0; i < m; i++) amp[i] = buf[i].Magnitude * 2 / wsum;
        amp[0] /= 2;

        var s = new Spectrum { N = n, Dc = amp[0] };
        s.Freq = new double[m - 1];
        s.Db = new double[m - 1];
        for (int i = 1; i < m; i++)
        {
            s.Freq[i - 1] = i / (n * dt);
            s.Db[i - 1] = 20 * Math.Log10(Math.Max(amp[i], 1e-15));
        }
        int k1 = 1;
        for (int i = 2; i < m; i++) if (amp[i] > amp[k1]) k1 = i;
        double Peak(int k) { double p = 0; for (int i = Math.Max(1, k - 2); i <= Math.Min(m - 1, k + 2); i++) p = Math.Max(p, amp[i]); return p; }
        s.Fundamental = k1 / (n * dt);
        s.FundamentalAmp = Peak(k1);
        double h2 = 0;
        for (int h = 2; h <= 10 && h * k1 + 2 < m; h++) { double p = Peak(h * k1); h2 += p * p; }
        s.ThdPercent = s.FundamentalAmp > 0 ? 100 * Math.Sqrt(h2) / s.FundamentalAmp : 0;
        return s;
    }
}
