using System.Numerics;

namespace NovaSpice.Core;

public sealed record AscWire(int X1, int Y1, int X2, int Y2);
public sealed record AscFlag(int X, int Y, string Name);
public sealed record AscText(int X, int Y, string Text, bool Directive);

public sealed class AscSymbol
{
    public string Type = "";
    public string Rot = "R0";
    public int X, Y;
    public Dictionary<string, string> Attrs = new(StringComparer.OrdinalIgnoreCase);
    public string Inst => Attrs.GetValueOrDefault("InstName", "");
    public string Value => Attrs.GetValueOrDefault("Value", "");
}

/// <summary>Parser for LTspice .asc schematics plus simple net tracing for probing.</summary>
public sealed class AscSchematic
{
    public List<AscWire> Wires = new();
    public List<AscFlag> Flags = new();
    public List<AscSymbol> Symbols = new();
    public List<AscText> Texts = new();
    public List<string?> WireNets = new();

    public static AscSchematic Parse(string text)
    {
        var s = new AscSchematic();
        AscSymbol? cur = null;
        foreach (var line in text.Replace("\r", "").Split('\n'))
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) continue;
            switch (p[0].ToUpperInvariant())
            {
                case "WIRE" when p.Length >= 5:
                    s.Wires.Add(new AscWire(I(p[1]), I(p[2]), I(p[3]), I(p[4])));
                    break;
                case "FLAG" when p.Length >= 4:
                    s.Flags.Add(new AscFlag(I(p[1]), I(p[2]), p[3]));
                    break;
                case "SYMBOL" when p.Length >= 4:
                    var type = p[1].Replace('\\', '/');
                    cur = new AscSymbol
                    {
                        Type = type[(type.LastIndexOf('/') + 1)..].ToLowerInvariant(),
                        X = I(p[2]), Y = I(p[3]), Rot = p.Length > 4 ? p[4].ToUpperInvariant() : "R0",
                    };
                    s.Symbols.Add(cur);
                    break;
                case "SYMATTR" when cur != null && p.Length >= 2:
                    int idx = line.IndexOf(p[1], StringComparison.Ordinal) + p[1].Length;
                    cur.Attrs[p[1]] = idx < line.Length ? line[idx..].Trim() : "";
                    break;
                case "TEXT" when p.Length >= 6:
                    var rest = SplitRest(line, 5);
                    bool dir = rest.StartsWith('!');
                    s.Texts.Add(new AscText(I(p[1]), I(p[2]), rest.TrimStart('!', ';').Replace("\\n", "\n"), dir));
                    break;
            }
        }
        s.ComputeNets();
        return s;
    }

    static int I(string v) => int.TryParse(v, System.Globalization.NumberStyles.Integer, Si.Inv, out var r) ? r : 0;

    static string SplitRest(string line, int skip)
    {
        int i = 0;
        for (int k = 0; k < skip; k++)
        {
            while (i < line.Length && line[i] == ' ') i++;
            while (i < line.Length && line[i] != ' ') i++;
        }
        return i < line.Length ? line[(i + 1)..] : "";
    }

    static bool OnSeg(int px, int py, AscWire w) =>
        (long)(w.X2 - w.X1) * (py - w.Y1) == (long)(w.Y2 - w.Y1) * (px - w.X1) &&
        px >= Math.Min(w.X1, w.X2) && px <= Math.Max(w.X1, w.X2) &&
        py >= Math.Min(w.Y1, w.Y2) && py <= Math.Max(w.Y1, w.Y2);

    void ComputeNets()
    {
        int n = Wires.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                var a = Wires[i]; var b = Wires[j];
                if (OnSeg(a.X1, a.Y1, b) || OnSeg(a.X2, a.Y2, b) || OnSeg(b.X1, b.Y1, a) || OnSeg(b.X2, b.Y2, a))
                    parent[Find(i)] = Find(j);
            }
        var names = new Dictionary<int, string>();
        foreach (var f in Flags)
            for (int i = 0; i < n; i++)
                if (OnSeg(f.X, f.Y, Wires[i])) names[Find(i)] = f.Name;
        WireNets = Enumerable.Range(0, n).Select(i => names.TryGetValue(Find(i), out var v) ? v : null).ToList();
    }
}

/// <summary>Geometry of the common built-in LTspice symbols (local coordinates, same pins as LTspice).</summary>
public sealed class SymbolShape
{
    public List<Vector2[]> Lines = new();
    public List<(Vector2 C, float R)> Circles = new();
    public Vector2[] Pins = Array.Empty<Vector2>();
    public bool Known = true;

    static Vector2 V(float x, float y) => new(x, y);

    public static SymbolShape Get(string type)
    {
        var s = new SymbolShape();
        switch (type)
        {
            case "res": case "res2":
                s.Lines.Add(new[] { V(16, 16), V(16, 28), V(4, 32), V(28, 40), V(4, 48), V(28, 56), V(4, 64), V(28, 72), V(4, 80), V(16, 84), V(16, 96) });
                s.Pins = new[] { V(16, 16), V(16, 96) };
                break;
            case "cap":
                s.Lines.Add(new[] { V(16, 0), V(16, 26) });
                s.Lines.Add(new[] { V(0, 26), V(32, 26) });
                s.Lines.Add(new[] { V(0, 38), V(32, 38) });
                s.Lines.Add(new[] { V(16, 38), V(16, 64) });
                s.Pins = new[] { V(16, 0), V(16, 64) };
                break;
            case "polcap":
                s.Lines.Add(new[] { V(16, 0), V(16, 26) });
                s.Lines.Add(new[] { V(0, 26), V(32, 26) });
                s.Lines.Add(Arc(16, 52, 18, -125, -55));
                s.Lines.Add(new[] { V(16, 37), V(16, 64) });
                s.Lines.Add(new[] { V(2, 14), V(10, 14) });
                s.Lines.Add(new[] { V(6, 10), V(6, 18) });
                s.Pins = new[] { V(16, 0), V(16, 64) };
                break;
            case "ind": case "ind2":
                var pts = new List<Vector2> { V(16, 16), V(16, 24) };
                for (int k = 0; k < 4; k++) pts.AddRange(Arc(16, 32 + k * 16, 8, -90, 90));
                pts.Add(V(16, 96));
                s.Lines.Add(pts.ToArray());
                s.Pins = new[] { V(16, 16), V(16, 96) };
                break;
            case "voltage": case "battery":
                s.Lines.Add(new[] { V(0, 16), V(0, 24) });
                s.Circles.Add((V(0, 56), 32));
                s.Lines.Add(new[] { V(0, 88), V(0, 96) });
                s.Lines.Add(new[] { V(-7, 42), V(7, 42) });
                s.Lines.Add(new[] { V(0, 35), V(0, 49) });
                s.Lines.Add(new[] { V(-7, 72), V(7, 72) });
                s.Pins = new[] { V(0, 16), V(0, 96) };
                break;
            case "current":
                s.Lines.Add(new[] { V(0, 0), V(0, 16) });
                s.Circles.Add((V(0, 40), 24));
                s.Lines.Add(new[] { V(0, 26), V(0, 54) });
                s.Lines.Add(new[] { V(-6, 46), V(0, 54), V(6, 46) });
                s.Lines.Add(new[] { V(0, 64), V(0, 80) });
                s.Pins = new[] { V(0, 0), V(0, 80) };
                break;
            case "diode": case "schottky": case "zener": case "led":
                s.Lines.Add(new[] { V(16, 0), V(16, 20) });
                s.Lines.Add(new[] { V(0, 20), V(32, 20), V(16, 44), V(0, 20) });
                s.Lines.Add(new[] { V(0, 44), V(32, 44) });
                s.Lines.Add(new[] { V(16, 44), V(16, 64) });
                s.Pins = new[] { V(16, 0), V(16, 64) };
                break;
            default:
                s.Known = false;
                s.Lines.Add(new[] { V(0, 0), V(64, 0), V(64, 64), V(0, 64), V(0, 0) });
                break;
        }
        return s;
    }

    static Vector2[] Arc(float cx, float cy, float r, float a0, float a1)
    {
        var p = new Vector2[13];
        for (int i = 0; i <= 12; i++)
        {
            double a = (a0 + (a1 - a0) * i / 12.0) * Math.PI / 180;
            p[i] = V(cx + r * (float)Math.Cos(a), cy + r * (float)Math.Sin(a));
        }
        return p;
    }

    /// <summary>LTspice orientation: optional mirror (M) then rotation (R0/90/180/270, clockwise on screen).</summary>
    public static Vector2 Transform(Vector2 p, string rot, int x, int y)
    {
        bool mirror = rot.StartsWith('M');
        int.TryParse(rot.AsSpan(1), out int ang);
        if (mirror) p = new Vector2(-p.X, p.Y);
        p = (ang % 360) switch
        {
            90 => new Vector2(-p.Y, p.X),
            180 => new Vector2(-p.X, -p.Y),
            270 => new Vector2(p.Y, -p.X),
            _ => p,
        };
        return new Vector2(p.X + x, p.Y + y);
    }
}
