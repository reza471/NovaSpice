using System.Numerics;

namespace NovaSpice.Core;

/// <summary>
/// Reads LTspice symbol files (.asy) from the installed LTspice library so that any library part can be drawn
/// and placed with its exact pin positions. Built-in shapes are used when LTspice is not installed.
/// </summary>
public static class SymbolLibrary
{
    public sealed record Entry(string RelPath, string FullPath)
    {
        public string Type => Path.GetFileNameWithoutExtension(RelPath).ToLowerInvariant();
        public string RawType => Path.ChangeExtension(RelPath, null)!.Replace('/', '\\');
        public string Folder => Path.GetDirectoryName(RelPath) is { Length: > 0 } d ? d : "(root)";
    }

    static List<Entry>? index;
    static string root = "";

    public static string Root { get { Index(); return root; } }
    public static bool Available => Index().Count > 0;

    static IEnumerable<string> Roots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        yield return Path.Combine(local, "LTspice", "lib", "sym");            // LTspice 24
        yield return Path.Combine(docs, "LTspiceXVII", "lib", "sym");          // LTspice XVII
        yield return Path.Combine(docs, "LTspice", "lib", "sym");
        yield return @"C:\Program Files\LTC\LTspiceXVII\lib\sym";
        yield return @"C:\Program Files\ADI\LTspice\lib\sym";
    }

    public static IReadOnlyList<Entry> Index()
    {
        if (index != null) return index;
        index = new();
        foreach (var r in Roots())
        {
            if (!Directory.Exists(r)) continue;
            try
            {
                index = Directory.EnumerateFiles(r, "*.asy", SearchOption.AllDirectories)
                    .Select(f => new Entry(Path.GetRelativePath(r, f), f))
                    .OrderBy(e => e.RelPath.Count(c => c == '\\' || c == '/')).ThenBy(e => e.RelPath, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                root = r;
                break;
            }
            catch { }
        }
        return index;
    }

    public static Entry? Find(string type) =>
        Index().FirstOrDefault(e => e.Type == type.ToLowerInvariant());

    /// <summary>Default attributes of a library symbol (Prefix, Value, SpiceModel, Description).</summary>
    public static Dictionary<string, string> Attributes(Entry e)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in ReadLines(e.FullPath))
        {
            var p = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length >= 2 && p[0].Equals("SYMATTR", StringComparison.OrdinalIgnoreCase))
                d[p[1]] = p.Length > 2 ? p[2].Trim() : "";
        }
        return d;
    }

    static IEnumerable<string> ReadLines(string path)
    {
        var b = File.ReadAllBytes(path);
        bool utf16 = b.Length > 1 && (b[1] == 0 || (b[0] == 0xFF && b[1] == 0xFE));
        var text = (utf16 ? System.Text.Encoding.Unicode : System.Text.Encoding.Latin1).GetString(b).TrimStart('\uFEFF');
        return text.Replace("\r", "").Split('\n');
    }

    public static SymbolShape? TryLoad(string type)
    {
        var e = Find(type);
        if (e == null) return null;
        try { return Parse(ReadLines(e.FullPath)); }
        catch { return null; }
    }

    static int I(string s) => int.TryParse(s, System.Globalization.NumberStyles.Integer, Si.Inv, out var v) ? v : 0;

    public static SymbolShape Parse(IEnumerable<string> lines)
    {
        var s = new SymbolShape();
        var pins = new List<(Vector2 P, int Order)>();
        foreach (var line in lines)
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) continue;
            switch (p[0].ToUpperInvariant())
            {
                case "LINE" when p.Length >= 6:
                    s.Lines.Add(new[] { new Vector2(I(p[2]), I(p[3])), new Vector2(I(p[4]), I(p[5])) });
                    break;
                case "RECTANGLE" when p.Length >= 6:
                    float x1 = I(p[2]), y1 = I(p[3]), x2 = I(p[4]), y2 = I(p[5]);
                    s.Lines.Add(new[] { new Vector2(x1, y1), new Vector2(x2, y1), new Vector2(x2, y2), new Vector2(x1, y2), new Vector2(x1, y1) });
                    break;
                case "CIRCLE" when p.Length >= 6:
                    float cx = (I(p[2]) + I(p[4])) / 2f, cy = (I(p[3]) + I(p[5])) / 2f;
                    s.Ellipses.Add((new Vector2(cx, cy), Math.Abs(I(p[4]) - I(p[2])) / 2f, Math.Abs(I(p[5]) - I(p[3])) / 2f));
                    break;
                case "ARC" when p.Length >= 10:
                    s.Lines.Add(Arc(I(p[2]), I(p[3]), I(p[4]), I(p[5]), I(p[6]), I(p[7]), I(p[8]), I(p[9])));
                    break;
                case "PIN" when p.Length >= 3:
                    pins.Add((new Vector2(I(p[1]), I(p[2])), pins.Count + 1));
                    break;
                case "PINATTR" when p.Length >= 3 && p[1].Equals("SpiceOrder", StringComparison.OrdinalIgnoreCase) && pins.Count > 0:
                    pins[^1] = (pins[^1].P, I(p[2]));
                    break;
            }
        }
        s.Pins = pins.OrderBy(x => x.Order).Select(x => x.P).ToArray();
        if (s.Lines.Count == 0 && s.Ellipses.Count == 0)
            s.Lines.Add(new[] { new Vector2(0, 0), new Vector2(64, 0), new Vector2(64, 64), new Vector2(0, 64), new Vector2(0, 0) });
        return s;
    }

    /// <summary>LTspice ARC: bounding box, then start and end points, drawn counter-clockwise.</summary>
    static Vector2[] Arc(float x1, float y1, float x2, float y2, float sx, float sy, float ex, float ey)
    {
        float cx = (x1 + x2) / 2, cy = (y1 + y2) / 2, rx = Math.Max(0.5f, Math.Abs(x2 - x1) / 2), ry = Math.Max(0.5f, Math.Abs(y2 - y1) / 2);
        double a0 = Math.Atan2(-(sy - cy) / ry, (sx - cx) / rx), a1 = Math.Atan2(-(ey - cy) / ry, (ex - cx) / rx);
        if (a1 <= a0) a1 += 2 * Math.PI;
        int n = Math.Max(6, (int)((a1 - a0) / (Math.PI / 16)));
        var pts = new Vector2[n + 1];
        for (int i = 0; i <= n; i++)
        {
            double a = a0 + (a1 - a0) * i / n;
            pts[i] = new Vector2(cx + rx * (float)Math.Cos(a), cy - ry * (float)Math.Sin(a));
        }
        return pts;
    }
}
