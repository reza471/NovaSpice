using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NovaSpice.Core;
using Windows.Foundation;
using Windows.UI;

namespace NovaSpice.Controls;

/// <summary>Read-only LTspice schematic viewer with pan/zoom and click-to-probe.</summary>
public sealed class SchematicCanvas : UserControl
{
    readonly CanvasControl cv = new();
    AscSchematic? sch;
    public PlotTheme Theme { get; set; } = PlotTheme.Dark;
    public event Action<string>? Probe;

    float scale = 1;
    Vector2 offset;
    bool fitPending = true;
    bool panning;
    Point panStart;
    Vector2 offStart;
    string? hoverProbe;
    int hoverKind = -1, hoverIndex = -1; // 0 wire, 1 symbol, 2 flag

    readonly CanvasStrokeStyle round = new() { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round };

    public SchematicCanvas()
    {
        Content = cv;
        cv.Draw += (s, e) => Render(e.DrawingSession, (float)s.ActualWidth, (float)s.ActualHeight);
        cv.SizeChanged += (_, _) => { if (fitPending) cv.Invalidate(); };
        cv.PointerPressed += (s, e) =>
        {
            panning = true; panStart = e.GetCurrentPoint(cv).Position; offStart = offset;
            cv.CapturePointer(e.Pointer);
        };
        cv.PointerReleased += (s, e) => { panning = false; cv.ReleasePointerCapture(e.Pointer); };
        cv.PointerMoved += OnMoved;
        cv.PointerWheelChanged += (s, e) =>
        {
            var pt = e.GetCurrentPoint(cv);
            float f = pt.Properties.MouseWheelDelta > 0 ? 1.18f : 1 / 1.18f;
            var p = new Vector2((float)pt.Position.X, (float)pt.Position.Y);
            var world = (p - offset) / scale;
            scale = Math.Clamp(scale * f, 0.1f, 12f);
            offset = p - world * scale;
            cv.Invalidate();
            e.Handled = true;
        };
        cv.DoubleTapped += (s, e) =>
        {
            if (hoverProbe != null) Probe?.Invoke(hoverProbe);
            else { fitPending = true; cv.Invalidate(); }
        };
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    }

    public void Load(AscSchematic? s)
    {
        sch = s;
        fitPending = true;
        cv.Invalidate();
    }

    public void Invalidate() => cv.Invalidate();

    Vector2 S(float x, float y) => new Vector2(x, y) * scale + offset;
    Vector2 S(Vector2 v) => v * scale + offset;

    IEnumerable<Vector2> AllPoints()
    {
        if (sch == null) yield break;
        foreach (var w in sch.Wires) { yield return new(w.X1, w.Y1); yield return new(w.X2, w.Y2); }
        foreach (var f in sch.Flags) yield return new(f.X, f.Y);
        foreach (var sy in sch.Symbols)
        {
            var shape = SymbolShape.Get(sy.Type);
            foreach (var l in shape.Lines) foreach (var p in l) yield return SymbolShape.Transform(p, sy.Rot, sy.X, sy.Y);
        }
        foreach (var t in sch.Texts) { yield return new(t.X, t.Y); yield return new(t.X + 8 * t.Text.Split('\n').Max(l => l.Length), t.Y + 16); }
    }

    void Fit(float w, float h)
    {
        var pts = AllPoints().ToList();
        if (pts.Count == 0 || w < 10 || h < 10) return;
        float x0 = pts.Min(p => p.X), x1 = pts.Max(p => p.X), y0 = pts.Min(p => p.Y), y1 = pts.Max(p => p.Y);
        float bw = Math.Max(64, x1 - x0), bh = Math.Max(64, y1 - y0);
        scale = Math.Clamp(Math.Min((w - 80) / bw, (h - 80) / bh), 0.2f, 4f);
        offset = new Vector2(w / 2, h / 2) - new Vector2((x0 + x1) / 2, (y0 + y1) / 2) * scale;
        fitPending = false;
    }

    void Render(CanvasDrawingSession ds, float w, float h)
    {
        ds.Clear(Theme.Surface);
        if (sch == null)
        {
            ds.DrawText("Schematic preview appears here for .asc files", new Rect(0, h / 2 - 10, w, 20), Theme.Muted,
                new CanvasTextFormat { FontSize = 13, HorizontalAlignment = CanvasHorizontalAlignment.Center });
            return;
        }
        if (fitPending) Fit(w, h);

        // dot grid
        float step = 16 * scale;
        while (step < 10) step *= 4;
        var dot = PlotTheme.WithAlpha(Theme.Muted, 50);
        for (float x = offset.X % step; x < w; x += step)
            for (float y = offset.Y % step; y < h; y += step)
                ds.FillRectangle(x, y, 1.3f, 1.3f, dot);

        float sw = Math.Max(1.2f, 2f * scale);
        var hl = Theme.Accent;
        var counts = new Dictionary<(int, int), int>();
        void Count(int x, int y) => counts[(x, y)] = counts.GetValueOrDefault((x, y)) + 1;

        for (int i = 0; i < sch.Wires.Count; i++)
        {
            var wi = sch.Wires[i];
            bool hot = hoverKind == 0 && hoverProbe != null && sch.WireNets[i] == sch.WireNets[hoverIndex];
            ds.DrawLine(S(wi.X1, wi.Y1), S(wi.X2, wi.Y2), hot ? hl : Theme.Wire, hot ? sw * 1.6f : sw, round);
            Count(wi.X1, wi.Y1); Count(wi.X2, wi.Y2);
        }

        var label = new CanvasTextFormat { FontFamily = "Segoe UI", FontSize = Math.Max(6, 13 * scale), WordWrapping = CanvasWordWrapping.NoWrap };
        var mono = new CanvasTextFormat { FontFamily = "Cascadia Mono, Consolas", FontSize = Math.Max(6, 12.5f * scale), WordWrapping = CanvasWordWrapping.NoWrap };

        for (int i = 0; i < sch.Symbols.Count; i++)
        {
            var sy = sch.Symbols[i];
            var shape = SymbolShape.Get(sy.Type);
            bool hot = hoverKind == 1 && hoverIndex == i;
            var col = hot ? hl : Theme.Symbol;
            foreach (var line in shape.Lines)
            {
                using var pb = new CanvasPathBuilder(ds);
                pb.BeginFigure(S(SymbolShape.Transform(line[0], sy.Rot, sy.X, sy.Y)));
                for (int k = 1; k < line.Length; k++) pb.AddLine(S(SymbolShape.Transform(line[k], sy.Rot, sy.X, sy.Y)));
                pb.EndFigure(CanvasFigureLoop.Open);
                using var g = CanvasGeometry.CreatePath(pb);
                ds.DrawGeometry(g, col, sw, round);
            }
            foreach (var (c, r) in shape.Circles)
            {
                var cc = S(SymbolShape.Transform(c, sy.Rot, sy.X, sy.Y));
                ds.DrawCircle(cc, r * scale, col, sw);
            }
            foreach (var pin in shape.Pins)
            {
                var pp = SymbolShape.Transform(pin, sy.Rot, sy.X, sy.Y);
                Count((int)MathF.Round(pp.X), (int)MathF.Round(pp.Y));
            }
            var (min, max) = Bounds(sy, shape);
            var center = (min + max) / 2;
            bool horizontal = max.X - min.X > max.Y - min.Y;
            string txt = string.IsNullOrEmpty(sy.Value) ? sy.Inst : $"{sy.Inst}\n{sy.Value}";
            if (!shape.Known) txt = $"{sy.Type}\n{txt}";
            var at = horizontal ? S(center.X - 18, min.Y - 34) : S(max.X + 8, center.Y - 16);
            ds.DrawText(txt, at, hot ? hl : Theme.Text, label);
        }

        foreach (var kv in counts.Where(k => k.Value >= 3))
            ds.FillCircle(S(kv.Key.Item1, kv.Key.Item2), Math.Max(2.5f, 4.5f * scale), Theme.Wire);

        for (int i = 0; i < sch.Flags.Count; i++)
        {
            var f = sch.Flags[i];
            if (f.Name == "0")
            {
                using var pb = new CanvasPathBuilder(ds);
                pb.BeginFigure(S(f.X - 16, f.Y)); pb.AddLine(S(f.X + 16, f.Y)); pb.AddLine(S(f.X, f.Y + 16)); pb.EndFigure(CanvasFigureLoop.Closed);
                using var g = CanvasGeometry.CreatePath(pb);
                ds.DrawGeometry(g, Theme.Symbol, sw, round);
                continue;
            }
            bool hot = hoverKind == 2 && hoverIndex == i;
            using var lay = new CanvasTextLayout(ds, f.Name, label, 1000, 100);
            var b = lay.LayoutBounds;
            var p = S(f.X, f.Y) + new Vector2(-(float)b.Width / 2 - 6 * scale, -(float)b.Height - 10 * scale);
            var rect = new Rect(p.X, p.Y, b.Width + 12 * scale, b.Height + 2 * scale);
            ds.FillRoundedRectangle(rect, 6 * scale, 6 * scale, PlotTheme.WithAlpha(hot ? hl : Theme.Accent, (byte)(hot ? 90 : 45)));
            ds.DrawText(f.Name, p.X + 6 * scale, p.Y, hot ? Theme.Text : Theme.Accent, label);
            ds.FillCircle(S(f.X, f.Y), Math.Max(2f, 3f * scale), Theme.Accent);
        }

        foreach (var t in sch.Texts)
            ds.DrawText((t.Directive ? "" : "") + t.Text, S(t.X, t.Y - 9), t.Directive ? Theme.Directive : Theme.Muted, mono);

        if (hoverProbe != null)
        {
            var hint = new CanvasTextFormat { FontSize = 12 };
            ds.FillRoundedRectangle(new Rect(12, h - 36, 260, 24), 8, 8, PlotTheme.WithAlpha(Theme.Bg, 230));
            ds.DrawText($"Double-click to plot  {hoverProbe}", 22, h - 32, Theme.Text, hint);
        }
    }

    static (Vector2 Min, Vector2 Max) Bounds(AscSymbol sy, SymbolShape shape)
    {
        var pts = shape.Lines.SelectMany(l => l).Select(p => SymbolShape.Transform(p, sy.Rot, sy.X, sy.Y))
            .Concat(shape.Circles.SelectMany(c => new[] { c.C - new Vector2(c.R), c.C + new Vector2(c.R) }).Select(p => SymbolShape.Transform(p, sy.Rot, sy.X, sy.Y))).ToList();
        return (new Vector2(pts.Min(p => p.X), pts.Min(p => p.Y)), new Vector2(pts.Max(p => p.X), pts.Max(p => p.Y)));
    }

    void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(cv).Position;
        if (panning)
        {
            offset = offStart + new Vector2((float)(pos.X - panStart.X), (float)(pos.Y - panStart.Y));
            cv.Invalidate();
            return;
        }
        if (sch == null) return;
        var w = (new Vector2((float)pos.X, (float)pos.Y) - offset) / scale;
        float tol = 7 / scale;
        string? probe = null; int kind = -1, idx = -1;

        for (int i = 0; i < sch.Flags.Count && probe == null; i++)
        {
            var f = sch.Flags[i];
            if (f.Name != "0" && Math.Abs(w.X - f.X) < 30 && w.Y < f.Y + tol && w.Y > f.Y - 30) { probe = $"V({f.Name})"; kind = 2; idx = i; }
        }
        for (int i = 0; i < sch.Symbols.Count && probe == null; i++)
        {
            var sy = sch.Symbols[i];
            var (mn, mx) = Bounds(sy, SymbolShape.Get(sy.Type));
            if (w.X >= mn.X - 4 && w.X <= mx.X + 4 && w.Y >= mn.Y - 4 && w.Y <= mx.Y + 4 && sy.Inst.Length > 0)
            { probe = $"I({sy.Inst})"; kind = 1; idx = i; }
        }
        for (int i = 0; i < sch.Wires.Count && probe == null; i++)
        {
            var wi = sch.Wires[i];
            if (sch.WireNets[i] is string net && net != "0" && DistSeg(w, new(wi.X1, wi.Y1), new(wi.X2, wi.Y2)) < tol)
            { probe = $"V({net})"; kind = 0; idx = i; }
        }
        if (probe != hoverProbe || idx != hoverIndex || kind != hoverKind)
        {
            hoverProbe = probe; hoverKind = kind; hoverIndex = idx;
            ProtectedCursor = InputSystemCursor.Create(probe != null ? InputSystemCursorShape.Hand : InputSystemCursorShape.Arrow);
            cv.Invalidate();
        }
    }

    static float DistSeg(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float t = ab.LengthSquared() == 0 ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
        return Vector2.Distance(p, a + ab * t);
    }
}
