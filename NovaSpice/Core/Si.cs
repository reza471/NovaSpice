using System.Globalization;

namespace NovaSpice.Core;

/// <summary>Engineering-notation formatting with SI prefixes.</summary>
public static class Si
{
    static readonly (double S, string P)[] Prefixes =
    {
        (1e12, "T"), (1e9, "G"), (1e6, "M"), (1e3, "k"), (1, ""),
        (1e-3, "m"), (1e-6, "µ"), (1e-9, "n"), (1e-12, "p"), (1e-15, "f"),
    };

    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static bool NoPrefix(string unit) => unit is "dB" or "°" or "%";

    public static string Fmt(double v, string unit = "", int digits = 4)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return "—";
        if (NoPrefix(unit)) return (v.ToString("0.00", Inv) + " " + unit).Trim();
        if (v == 0) return ("0 " + unit).Trim();
        double a = Math.Abs(v);
        foreach (var (s, p) in Prefixes)
            if (a >= s * 0.9995)
                return ((v / s).ToString("G" + digits, Inv) + " " + p + unit).Trim();
        return (v.ToString("0.###e+0", Inv) + " " + unit).Trim();
    }

    public static (double Scale, string Prefix) PrefixFor(double absMax, string unit)
    {
        if (NoPrefix(unit) || absMax == 0 || double.IsNaN(absMax)) return (1, "");
        foreach (var (s, p) in Prefixes)
            if (absMax >= s * 0.9995) return (s, p);
        return (1e-15, "f");
    }

    /// <summary>Axis tick label: consistent prefix across the axis, decimals from the step.</summary>
    public static string FmtAxis(double v, double step, double absMax, string unit)
    {
        var (s, p) = PrefixFor(absMax, unit);
        int dec = Math.Clamp((int)-Math.Floor(Math.Log10(step / s) + 1e-9), 0, 9);
        if (Math.Abs(v) < step * 1e-6) v = 0;
        return ((v / s).ToString("F" + dec, Inv) + " " + p + unit).Trim();
    }

    public static string Unit(string kind) => kind switch
    {
        "voltage" => "V",
        "device_current" or "current" or "subckt_current" => "A",
        "time" => "s",
        "frequency" => "Hz",
        _ => "",
    };

    public static double NiceStep(double range, int target)
    {
        if (range <= 0 || double.IsNaN(range)) return 1;
        double raw = range / Math.Max(1, target);
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double n = raw / mag;
        double step = n < 1.5 ? 1 : n < 3 ? 2 : n < 7 ? 5 : 10;
        return step * mag;
    }

    /// <summary>Parse a SPICE number such as 10k, 4.7u, 2Meg, 1e-3.</summary>
    public static bool TryParseSpice(string s, out double value)
    {
        value = 0;
        s = s.Trim();
        int i = 0;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] is '.' or '+' or '-' ||
               ((s[i] is 'e' or 'E') && i + 1 < s.Length && (char.IsDigit(s[i + 1]) || s[i + 1] is '+' or '-'))))
            i++;
        if (!double.TryParse(s[..i], NumberStyles.Float, Inv, out value)) return false;
        value *= SuffixScale(s[i..]);
        return true;
    }

    public static double SuffixScale(string suffix)
    {
        var t = suffix.ToLowerInvariant();
        if (t.Length == 0) return 1;
        if (t.StartsWith("meg")) return 1e6;
        if (t.StartsWith("mil")) return 25.4e-6;
        return t[0] switch
        {
            't' => 1e12, 'g' => 1e9, 'k' => 1e3, 'm' => 1e-3, 'u' or 'µ' => 1e-6,
            'n' => 1e-9, 'p' => 1e-12, 'f' => 1e-15, _ => 1,
        };
    }
}
