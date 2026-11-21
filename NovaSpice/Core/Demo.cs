using System.Numerics;
using System.Text;

namespace NovaSpice.Core;

/// <summary>
/// Generates demo projects (circuit + LTspice-format .raw/.log results) so the app can be explored
/// without LTspice installed. With LTspice installed, pressing Run re-simulates them for real.
/// </summary>
public static class Demo
{
    public static string DemoDir => Path.Combine(AppSettings.Dir, "demos");

    public static string Ensure(string name)
    {
        Directory.CreateDirectory(DemoDir);
        return name == "buck" ? Buck() : Rc();
    }

    const string BuckNet = """
* Buck converter 12 V -> 5 V, 500 kHz (NovaSpice demo)
V1 in 0 12
S1 in sw ctl 0 SW
D1 0 sw DS
L1 sw out 10u
C1 out 0 47u
R1 out 0 2.5
Vctl ctl 0 PULSE(0 1 0 10n 10n 0.82u 2u)
.model SW SW(Ron=10m Roff=1Meg Vt=0.5)
.model DS D(Ron=20m Vfwd=0.4)
.tran 0 1m 0 20n
.meas TRAN vout_avg AVG V(out) FROM 0.8m TO 1m
.meas TRAN vout_pp PP V(out) FROM 0.8m TO 1m
.meas TRAN il_max MAX I(L1) FROM 0.8m TO 1m
.backanno
.end
""";

    static string Buck()
    {
        string net = Path.Combine(DemoDir, "buck_converter.net");
        string raw = Path.ChangeExtension(net, ".raw");
        if (File.Exists(net) && File.Exists(raw)) return net;
        File.WriteAllText(net, BuckNet.Replace("\n", "\r\n"));

        const double T = 2e-6, D = 5.0 / 12, tstop = 1e-3, L = 10e-6, C = 47e-6, R = 2.5, Esr = 0.01, Vf = 4.97;
        int n = (int)(tstop / T * 48);
        var rng = new Random(7);
        var t = new double[n + 1];
        for (int i = 0; i <= n; i++)
            t[i] = tstop * i / n + (i == 0 || i == n ? 0 : (rng.NextDouble() - 0.5) * 0.35 * tstop / n);
        Array.Sort(t);

        double w0 = 1 / Math.Sqrt(L * C), zeta = 1 / (2 * R) * Math.Sqrt(L / C) + 0.04, wd = w0 * Math.Sqrt(1 - zeta * zeta);
        double dI = (12 - Vf) * D * T / L;
        int m = t.Length;
        double[] vin = new double[m], vsw = new double[m], vctl = new double[m], v = new double[m], irip = new double[m];
        for (int i = 0; i < m; i++)
        {
            double a = t[i] % T / T;
            double on = 0.25 * (1 + Math.Tanh((a - 0.005) / 0.002)) * (1 + Math.Tanh((D - a) / 0.002));
            vin[i] = 12; vctl[i] = on; vsw[i] = -0.45 + 12.43 * on;
            v[i] = Vf * (1 - Math.Exp(-zeta * w0 * t[i]) * (Math.Cos(wd * t[i]) + zeta / Math.Sqrt(1 - zeta * zeta) * Math.Sin(wd * t[i])));
            double tri = a < D ? -1 + 2 * a / D : 1 - 2 * (a - D) / (1 - D);
            irip[i] = dI / 2 * tri;
        }
        var vr = new double[m];
        for (int i = 1; i < m; i++) vr[i] = vr[i - 1] + 0.5 * (irip[i] + irip[i - 1]) * (t[i] - t[i - 1]) / C;
        double mean = vr.Average();
        double[] vout = new double[m], il = new double[m], ic = new double[m], ir = new double[m], id = new double[m], iv = new double[m];
        for (int i = 0; i < m; i++)
        {
            int i0 = Math.Max(0, i - 1), i1 = Math.Min(m - 1, i + 1);
            double dv = (v[i1] - v[i0]) / (t[i1] - t[i0]);
            vout[i] = v[i] + vr[i] - mean + Esr * irip[i];
            il[i] = C * dv + v[i] / R + irip[i];
            ir[i] = vout[i] / R;
            ic[i] = il[i] - ir[i];
            id[i] = il[i] * (1 - vctl[i]);
            iv[i] = -il[i] * vctl[i];
        }
        var names = new[] { "time", "V(in)", "V(sw)", "V(out)", "V(ctl)", "I(C1)", "I(D1)", "I(L1)", "I(R1)", "I(V1)" };
        var kinds = new[] { "time", "voltage", "voltage", "voltage", "voltage", "device_current", "device_current", "device_current", "device_current", "device_current" };
        RawFile.Write(raw, "Transient Analysis", names, kinds, new[] { t, vin, vsw, vout, vctl, ic, id, il, ir, iv }, null, false, false, net);

        var (mn, mx, avg, _) = Dsp.Stats(t, vout, 0.8e-3, 1e-3);
        var (_, ilmax, _, _) = Dsp.Stats(t, il, 0.8e-3, 1e-3);
        var log = new StringBuilder();
        log.AppendLine($"Circuit: * {net}");
        log.AppendLine();
        log.AppendLine("Direct Newton iteration for .op point succeeded.");
        log.AppendLine();
        log.AppendLine(FormattableString.Invariant($"vout_avg: AVG(v(out))={avg:G6} FROM 0.0008 TO 0.001"));
        log.AppendLine(FormattableString.Invariant($"vout_pp: PP(v(out))={mx - mn:G6} FROM 0.0008 TO 0.001"));
        log.AppendLine(FormattableString.Invariant($"il_max: MAX(i(l1))={ilmax:G6} FROM 0.0008 TO 0.001"));
        log.AppendLine();
        log.AppendLine($"Date: {DateTime.Now.ToString("ddd MMM dd HH:mm:ss yyyy", Si.Inv)}");
        log.AppendLine("Total elapsed time: 0.412 seconds.");
        log.AppendLine();
        log.AppendLine("tnom = 27\ntemp = 27\nmethod = modified trap\ntotiter = 51234\ntraniter = 51012\ntranpoints = 24001");
        File.WriteAllText(Path.ChangeExtension(net, ".log"), log.ToString());
        return net;
    }

    const string RcAsc = """
Version 4
SHEET 1 880 680
WIRE 144 112 64 112
WIRE 272 112 224 112
WIRE 304 112 272 112
WIRE 432 112 384 112
WIRE 512 112 432 112
FLAG 64 192 0
FLAG 272 176 0
FLAG 432 176 0
FLAG 64 112 in
FLAG 272 112 mid
FLAG 512 112 out
SYMBOL voltage 64 96 R0
SYMATTR InstName V1
SYMATTR Value AC 1
SYMBOL res 240 96 R90
SYMATTR InstName R1
SYMATTR Value 1k
SYMBOL cap 256 112 R0
SYMATTR InstName C1
SYMATTR Value {C}
SYMBOL res 400 96 R90
SYMATTR InstName R2
SYMATTR Value 1k
SYMBOL cap 416 112 R0
SYMATTR InstName C2
SYMATTR Value {C}
TEXT 48 248 Left 2 ;2nd-order RC ladder low-pass  (NovaSpice demo)
TEXT 48 280 Left 2 !.ac dec 40 10 10Meg
TEXT 48 312 Left 2 !.step param C list 10n 47n 100n
TEXT 48 344 Left 2 !.meas AC f3db WHEN mag(V(out))=1/sqrt(2)
""";

    static string Rc()
    {
        string asc = Path.Combine(DemoDir, "rc_filter.asc");
        string raw = Path.ChangeExtension(asc, ".raw");
        if (File.Exists(asc) && File.Exists(raw)) return asc;
        File.WriteAllText(asc, RcAsc.Replace("\n", "\r\n"), Encoding.Latin1);

        double[] caps = { 10e-9, 47e-9, 100e-9 };
        const double R = 1000;
        int per = 241;
        var cols = Enumerable.Range(0, 9).Select(_ => new List<Complex>()).ToArray();
        var f3 = new List<double>();
        var g1k = new List<Complex>();
        foreach (var C in caps)
        {
            Complex? prev = null; double prevF = 0; double fc = double.NaN;
            for (int i = 0; i < per; i++)
            {
                double f = Math.Pow(10, 1 + 6.0 * i / (per - 1));
                var s = new Complex(0, 2 * Math.PI * f);
                var y = s * C + s * C / (1 + s * R * C);
                var vin = Complex.One;
                var vmid = vin / (1 + R * y);
                var vout = vmid / (1 + s * R * C);
                var iR1 = (vin - vmid) / R;
                Complex[] row = { f, vin, vmid, vout, vmid * s * C, vout * s * C, iR1, (vmid - vout) / R, -iR1 };
                for (int k = 0; k < 9; k++) cols[k].Add(row[k]);
                if (prev is Complex p && double.IsNaN(fc) && p.Magnitude >= 1 / Math.Sqrt(2) && vout.Magnitude < 1 / Math.Sqrt(2))
                {
                    double a = (p.Magnitude - 1 / Math.Sqrt(2)) / (p.Magnitude - vout.Magnitude);
                    fc = Math.Pow(10, Math.Log10(prevF) + a * (Math.Log10(f) - Math.Log10(prevF)));
                }
                prev = vout; prevF = f;
            }
            f3.Add(fc);
            var s1k = new Complex(0, 2 * Math.PI * 1000);
            var y1 = s1k * C + s1k * C / (1 + s1k * R * C);
            g1k.Add(1 / (1 + R * y1) / (1 + s1k * R * C));
        }
        var names = new[] { "frequency", "V(in)", "V(mid)", "V(out)", "I(C1)", "I(C2)", "I(R1)", "I(R2)", "I(V1)" };
        var kinds = new[] { "frequency", "voltage", "voltage", "voltage", "device_current", "device_current", "device_current", "device_current", "device_current" };
        RawFile.Write(raw, "AC Analysis", names, kinds,
            cols.Select(c => c.Select(z => z.Real).ToArray()).ToList(),
            cols.Select(c => c.Select(z => z.Imaginary).ToArray()).ToList(), true, true, asc);

        var log = new StringBuilder();
        log.AppendLine($"Circuit: * {asc}");
        log.AppendLine();
        foreach (var c in caps) log.AppendLine(FormattableString.Invariant($".step c={c:G3}"));
        log.AppendLine();
        log.AppendLine("Measurement: f3db");
        log.AppendLine("  step\tf3db");
        for (int k = 0; k < caps.Length; k++) log.AppendLine(FormattableString.Invariant($"     {k + 1}\t{f3[k]:G6}"));
        log.AppendLine();
        log.AppendLine("Measurement: gain_1k");
        log.AppendLine("  step\tV(out)\tAT");
        for (int k = 0; k < caps.Length; k++)
        {
            var z = g1k[k];
            log.AppendLine(FormattableString.Invariant($"     {k + 1}\t({20 * Math.Log10(z.Magnitude):F3}dB,{z.Phase * 180 / Math.PI:F2}°)\t1000"));
        }
        log.AppendLine();
        log.AppendLine("Total elapsed time: 0.087 seconds.");
        File.WriteAllText(Path.ChangeExtension(asc, ".log"), log.ToString(), Encoding.Unicode);
        return asc;
    }
}
