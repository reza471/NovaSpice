using Microsoft.UI;
using Windows.UI;

namespace NovaSpice.Controls;

public sealed class PlotTheme
{
    public bool IsDark;
    public Color Bg, Surface, Grid, GridMinor, Frame, Text, Muted, Accent, Accent2, CurA, CurB, Wire, Directive, Symbol;
    public Color[] Traces = Array.Empty<Color>();

    public static Color H(string hex, byte a = 255)
    {
        hex = hex.TrimStart('#');
        return ColorHelper.FromArgb(a, Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
    }

    public static Color WithAlpha(Color c, byte a) => ColorHelper.FromArgb(a, c.R, c.G, c.B);

    public static readonly PlotTheme Dark = new()
    {
        IsDark = true,
        Bg = H("0B0E14"), Surface = H("10141C"), Grid = H("1E2633"), GridMinor = H("161C26"), Frame = H("263042"),
        Text = H("E7EAF0"), Muted = H("8B94A7"), Accent = H("7C9CFF"), Accent2 = H("2DD4BF"),
        CurA = H("FBBF24"), CurB = H("2DD4BF"), Wire = H("5EEAD4"), Directive = H("7C9CFF"), Symbol = H("D6DBE5"),
        Traces = new[] { "5B9CFF", "FFB547", "3DDC97", "FF6B9A", "B48CFF", "FFE066", "34D1E0", "FF8A65", "9CCC65", "F48FB1" }
            .Select(s => H(s)).ToArray(),
    };

    public static readonly PlotTheme Light = new()
    {
        IsDark = false,
        Bg = H("F3F5F9"), Surface = H("FFFFFF"), Grid = H("E3E7EF"), GridMinor = H("F0F2F6"), Frame = H("D5DBE6"),
        Text = H("1A2030"), Muted = H("5F6B80"), Accent = H("4F6BED"), Accent2 = H("0D9488"),
        CurA = H("D97706"), CurB = H("0D9488"), Wire = H("047857"), Directive = H("3B5BDB"), Symbol = H("1F2633"),
        Traces = new[] { "1F6FEB", "D97706", "059669", "DB2777", "7C3AED", "CA8A04", "0891B2", "EA580C", "65A30D", "BE185D" }
            .Select(s => H(s)).ToArray(),
    };

    /// <summary>Rotate the hue a little per .step so stepped curves stay related but distinguishable.</summary>
    public static Color StepColor(Color c, int k, int n)
    {
        if (n <= 1 || k == 0) return c;
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? ((g - b) / d % 6) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h = (h * 60 + 360) % 360;
        double s = max == 0 ? 0 : d / max, v = max;
        h = (h + 34.0 * k) % 360;
        v = Math.Clamp(v * (k % 2 == 1 ? 0.92 : 1.0), 0, 1);
        double cc = v * s, x = cc * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - cc;
        (double r1, double g1, double b1) = h switch
        {
            < 60 => (cc, x, 0.0), < 120 => (x, cc, 0.0), < 180 => (0.0, cc, x),
            < 240 => (0.0, x, cc), < 300 => (x, 0.0, cc), _ => (cc, 0.0, x),
        };
        return ColorHelper.FromArgb(255, (byte)((r1 + m) * 255), (byte)((g1 + m) * 255), (byte)((b1 + m) * 255));
    }
}
