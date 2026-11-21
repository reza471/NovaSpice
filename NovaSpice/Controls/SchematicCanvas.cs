using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using NovaSpice.Core;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace NovaSpice.Controls;

public enum SchTool { Select, Wire, Place, Ground, Label, Text }

/// <summary>
/// Win2D schematic editor for LTspice .asc files: place parts, draw wires, move/rotate/mirror,
/// rubber-band select, undo/redo, copy/paste, probe. Coordinates are LTspice units on a 16-unit grid.
/// </summary>
public sealed class SchematicCanvas : UserControl
{
    const int Grid = 16;
    readonly CanvasControl cv = new();
    AscSchematic? doc;

    public AscSchematic? Document => doc;
    public PlotTheme Theme { get; set; } = PlotTheme.Dark;
    public SchTool Tool { get; private set; } = SchTool.Select;
    public string PlaceType { get; private set; } = "res";

    public event Action<string>? Probe;
    public event Action? Changed;
    public event Action<object>? EditRequested;
    public event Action<SchTool, string>? ToolChanged;
    public event Action? LabelRequested;
    public event Action? TextRequested;

    // view
    float scale = 1;
    Vector2 offset;
    bool fitPending = true;

    // tool state
    string placeRot = "R0";
    string pendingLabel = "out";
    AscText? pendingText;
    (int X, int Y) snapCursor;
    bool cursorInside;
    (int X, int Y)? wireStart;

    // selection / undo
    readonly HashSet<object> sel = new();
    object? hover;
    readonly Stack<string> undo = new(), redo = new();
    static string clipboard = "";

    // dragging
    enum Drag { None, Pan, Move, Band }
    Drag drag;
    Point pressPos;
    bool pressedRight, dragMovedPx;
    Vector2 offStart;
    (int X, int Y) moveStart, moveDelta;
    string? moveSnapshot;
    List<(AscSymbol S, int X, int Y)> mvSyms = new();
    List<(AscFlag F, int X, int Y)> mvFlags = new();
    List<(AscText T, int X, int Y)> mvTexts = new();
    List<(AscWire W, int X1, int Y1, int X2, int Y2, bool M1, bool M2)> mvWires = new();
    Vector2 bandStart, bandNow;

    readonly CanvasStrokeStyle round = new() { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round };
    readonly CanvasStrokeStyle dash = new() { DashStyle = CanvasDashStyle.Dash, StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round };

    public SchematicCanvas()
    {
        Content = cv;
        IsTabStop = true;
        cv.Draw += (s, e) => Render(e.DrawingSession, (float)s.ActualWidth, (float)s.ActualHeight);
        cv.SizeChanged += (_, _) => { if (fitPending) cv.Invalidate(); };
        cv.PointerPressed += OnPressed;
        cv.PointerMoved += OnMoved;
        cv.PointerReleased += OnReleased;
        cv.PointerEntered += (_, _) => cursorInside = true;
        cv.PointerExited += (_, _) => { cursorInside = false; hover = null; cv.Invalidate(); };
        cv.PointerWheelChanged += OnWheel;
        cv.DoubleTapped += OnDoubleTapped;
        KeyDown += OnKeyDown;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    }

    // ================= public API =================
    /// <summary>Load a new document (clears undo history, fits the view).</summary>
    public void Load(AscSchematic? s)
    {
        doc = s;
        sel.Clear(); undo.Clear(); redo.Clear();
        wireStart = null;
        fitPending = true;
        SetTool(SchTool.Select);
        cv.Invalidate();
    }

    /// <summary>Replace the document after a text edit, keeping view and history.</summary>
    public void Replace(AscSchematic s)
    {
        if (doc != null) { undo.Push(doc.Serialize()); redo.Clear(); }
        doc = s;
        sel.Clear();
        cv.Invalidate();
    }

    public void Invalidate() => cv.Invalidate();
    public void Fit() { fitPending = true; cv.Invalidate(); }
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    public void SetTool(SchTool t, string? type = null)
    {
        Tool = t;
        if (type != null) PlaceType = type;
        wireStart = null;
        if (t != SchTool.Select) sel.Clear();
        ProtectedCursor = InputSystemCursor.Create(t == SchTool.Select ? InputSystemCursorShape.Arrow : InputSystemCursorShape.Cross);
        ToolChanged?.Invoke(t, PlaceType);
        cv.Invalidate();
    }

    public void BeginPlace(string type) { placeRot = "R0"; SetTool(SchTool.Place, type); Focus(FocusState.Programmatic); }
    public void BeginLabel(string name) { pendingLabel = name; SetTool(SchTool.Label); Focus(FocusState.Programmatic); }
    public void BeginText(string text, bool directive)
    {
        pendingText = new AscText { Text = text, Directive = directive };
        SetTool(SchTool.Text);
        Focus(FocusState.Programmatic);
    }

    /// <summary>Call before changing the document from outside (e.g. a properties dialog).</summary>
    public void PushUndo()
    {
        if (doc == null) return;
        undo.Push(doc.Serialize());
        redo.Clear();
    }

    public void Commit()
    {
        if (doc == null) return;
        doc.ComputeNets();
        Changed?.Invoke();
        cv.Invalidate();
    }

    public void Undo()
    {
        if (doc == null || undo.Count == 0) return;
        redo.Push(doc.Serialize());
        doc = AscSchematic.Parse(undo.Pop());
        sel.Clear();
        Commit();
    }

    public void Redo()
    {
        if (doc == null || redo.Count == 0) return;
        undo.Push(doc.Serialize());
        doc = AscSchematic.Parse(redo.Pop());
        sel.Clear();
        Commit();
    }

    public void DeleteSelection()
    {
        if (doc == null || sel.Count == 0) return;
        PushUndo();
        doc.Symbols.RemoveAll(sel.Contains);
        doc.Wires.RemoveAll(sel.Contains);
        doc.Flags.RemoveAll(sel.Contains);
        doc.Texts.RemoveAll(sel.Contains);
        sel.Clear();
        Commit();
    }

    public void RotateSelection() => Reorient(SymbolShape.Rotate);
    public void MirrorSelection() => Reorient(SymbolShape.Mirror);

    void Reorient(Func<string, string> f)
    {
        if (Tool == SchTool.Place) { placeRot = f(placeRot); cv.Invalidate(); return; }
        var syms = sel.OfType<AscSymbol>().ToList();
        if (doc == null || syms.Count == 0) return;
        PushUndo();
        foreach (var s in syms)
        {
            var (a0, b0) = Bounds(s);
            s.Rot = f(s.Rot);
            var (a1, b1) = Bounds(s);
            var d = (a0 + b0) / 2 - (a1 + b1) / 2;
            s.X += (int)MathF.Round(d.X / Grid) * Grid;
            s.Y += (int)MathF.Round(d.Y / Grid) * Grid;
        }
        Commit();
    }

    public void SelectAll()
    {
        if (doc == null) return;
        sel.Clear();
        foreach (var o in doc.Symbols.Cast<object>().Concat(doc.Wires).Concat(doc.Flags).Concat(doc.Texts)) sel.Add(o);
        cv.Invalidate();
    }

    public void Copy()
    {
        if (doc == null || sel.Count == 0) return;
        var frag = new AscSchematic
        {
            Symbols = sel.OfType<AscSymbol>().ToList(), Wires = sel.OfType<AscWire>().ToList(),
            Flags = sel.OfType<AscFlag>().ToList(), Texts = sel.OfType<AscText>().ToList(),
        };
        clipboard = frag.Serialize();
    }

    public void Paste()
    {
        if (doc == null || clipboard.Length == 0) return;
        var frag = AscSchematic.Parse(clipboard);
        var pts = frag.Wires.SelectMany(w => new[] { (w.X1, w.Y1) }).Concat(frag.Symbols.Select(s => (s.X, s.Y)))
            .Concat(frag.Flags.Select(f => (f.X, f.Y))).Concat(frag.Texts.Select(t => (t.X, t.Y))).ToList();
        if (pts.Count == 0) return;
        int minX = pts.Min(p => p.Item1), minY = pts.Min(p => p.Item2);
        int dx = (cursorInside ? snapCursor.X : minX + 32) - minX, dy = (cursorInside ? snapCursor.Y : minY + 32) - minY;
        PushUndo();
        sel.Clear();
        foreach (var w in frag.Wires) { w.X1 += dx; w.X2 += dx; w.Y1 += dy; w.Y2 += dy; doc.Wires.Add(w); sel.Add(w); }
        foreach (var f in frag.Flags) { f.X += dx; f.Y += dy; doc.Flags.Add(f); sel.Add(f); }
        foreach (var t in frag.Texts) { t.X += dx; t.Y += dy; doc.Texts.Add(t); sel.Add(t); }
        foreach (var s in frag.Symbols)
        {
            s.X += dx; s.Y += dy;
            var prefix = new string(s.Inst.TakeWhile(char.IsLetter).ToArray());
            if (prefix.Length > 0) s.Inst = doc.NextInstName(prefix);
            doc.Symbols.Add(s); sel.Add(s);
        }
        Commit();
    }

    // ================= geometry =================
    static int Snap(float v) => (int)MathF.Round(v / Grid) * Grid;
    (int X, int Y) Snap(Vector2 w) => (Snap(w.X), Snap(w.Y));
    Vector2 ToWorld(Point p) => (new Vector2((float)p.X, (float)p.Y) - offset) / scale;
    Vector2 S(float x, float y) => new Vector2(x, y) * scale + offset;
    Vector2 S(Vector2 v) => v * scale + offset;

    static (Vector2 Min, Vector2 Max) Bounds(AscSymbol sy)
    {
        var shape = SymbolShape.Get(sy.Type);
        var pts = shape.Lines.SelectMany(l => l)
            .Concat(shape.Circles.SelectMany(c => new[] { c.C - new Vector2(c.R), c.C + new Vector2(c.R) }))
            .Select(p => SymbolShape.Transform(p, sy.Rot, sy.X, sy.Y)).ToList();
        return (new Vector2(pts.Min(p => p.X), pts.Min(p => p.Y)), new Vector2(pts.Max(p => p.X), pts.Max(p => p.Y)));
    }

    static Rect LabelRect(AscFlag f)
    {
        double w = 8.5 * f.Name.Length + 14;
        return new Rect(f.X - w / 2, f.Y - 30, w, 22);
    }

    static Rect TextRect(AscText t)
    {
        var lines = t.Text.Split('\n');
        return new Rect(t.X, t.Y - 10, Math.Max(16, lines.Max(l => l.Length) * 7.9), lines.Length * 16.5 + 2);
    }

    static float DistSeg(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float t = ab.LengthSquared() == 0 ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
        return Vector2.Distance(p, a + ab * t);
    }

    object? Hit(Vector2 w)
    {
        if (doc == null) return null;
        float tol = Math.Max(4, 7 / scale);
        for (int i = doc.Symbols.Count - 1; i >= 0; i--)
        {
            var (mn, mx) = Bounds(doc.Symbols[i]);
            if (w.X >= mn.X - 3 && w.X <= mx.X + 3 && w.Y >= mn.Y - 3 && w.Y <= mx.Y + 3) return doc.Symbols[i];
        }
        foreach (var f in doc.Flags)
        {
            if (f.IsGround) { if (Math.Abs(w.X - f.X) <= 18 && w.Y >= f.Y - 4 && w.Y <= f.Y + 20) return f; }
            else if (LabelRect(f).Contains(new Point(w.X, w.Y))) return f;
        }
        foreach (var t in doc.Texts) if (TextRect(t).Contains(new Point(w.X, w.Y))) return t;
        foreach (var wi in doc.Wires)
            if (DistSeg(w, new(wi.X1, wi.Y1), new(wi.X2, wi.Y2)) < tol) return wi;
        return null;
    }

    (int X, int Y) PlaceOrigin(string type, string rot, (int X, int Y) at)
    {
        var shape = SymbolShape.Get(type);
        if (shape.Pins.Length == 0) return at;
        var p0 = SymbolShape.Transform(shape.Pins[0], rot, 0, 0);
        return (at.X - (int)MathF.Round(p0.X), at.Y - (int)MathF.Round(p0.Y));
    }

    IEnumerable<Vector2> AllPoints()
    {
        if (doc == null) yield break;
        foreach (var w in doc.Wires) { yield return new(w.X1, w.Y1); yield return new(w.X2, w.Y2); }
        foreach (var f in doc.Flags) yield return new(f.X, f.Y);
        foreach (var sy in doc.Symbols) { var (a, b) = Bounds(sy); yield return a; yield return b; }
        foreach (var t in doc.Texts) { var r = TextRect(t); yield return new((float)r.Left, (float)r.Top); yield return new((float)r.Right, (float)r.Bottom); }
    }

    void FitView(float w, float h)
    {
        var pts = AllPoints().ToList();
        if (w < 10 || h < 10) return;
        if (pts.Count == 0) { scale = 1.5f; offset = new Vector2(w / 2, h / 2); fitPending = false; return; }
        float x0 = pts.Min(p => p.X), x1 = pts.Max(p => p.X), y0 = pts.Min(p => p.Y), y1 = pts.Max(p => p.Y);
        float bw = Math.Max(160, x1 - x0), bh = Math.Max(160, y1 - y0);
        scale = Math.Clamp(Math.Min((w - 100) / bw, (h - 120) / bh), 0.2f, 1.8f);
        offset = new Vector2(w / 2, h / 2) - new Vector2((x0 + x1) / 2, (y0 + y1) / 2) * scale;
        fitPending = false;
    }

    // ================= rendering =================
    Color ColorFor(object o, Color normal) =>
        sel.Contains(o) ? Theme.Accent : ReferenceEquals(hover, o) && Tool == SchTool.Select ? Theme.Accent2 : normal;

    void Render(CanvasDrawingSession ds, float w, float h)
    {
        ds.Clear(Theme.Surface);
        if (doc == null)
        {
            ds.DrawText("Schematic appears here for .asc files — or create a new one (Ctrl+N)", new Rect(0, h / 2 - 10, w, 20), Theme.Muted,
                new CanvasTextFormat { FontSize = 13, HorizontalAlignment = CanvasHorizontalAlignment.Center });
            return;
        }
        if (fitPending) FitView(w, h);

        // dot grid
        float step = Grid * scale;
        while (step < 10) step *= 4;
        var dot = PlotTheme.WithAlpha(Theme.Muted, 55);
        for (float x = offset.X % step; x < w; x += step)
            for (float y = offset.Y % step; y < h; y += step)
                ds.FillRectangle(x - 0.6f, y - 0.6f, 1.3f, 1.3f, dot);

        float sw = Math.Max(1.2f, 2f * scale);
        var label = new CanvasTextFormat { FontFamily = "Segoe UI", FontSize = Math.Max(6, 13 * scale), WordWrapping = CanvasWordWrapping.NoWrap };
        var mono = new CanvasTextFormat { FontFamily = "Cascadia Mono, Consolas", FontSize = Math.Max(6, 13 * scale), WordWrapping = CanvasWordWrapping.NoWrap };

        // connectivity bookkeeping
        var counts = new Dictionary<(int, int), int>();
        void Count((int, int) p, int n = 1) => counts[p] = counts.GetValueOrDefault(p) + n;
        foreach (var wi in doc.Wires) { Count((wi.X1, wi.Y1)); Count((wi.X2, wi.Y2)); }
        var pins = doc.Symbols.SelectMany(s => doc.Pins(s)).ToList();
        foreach (var p in pins) Count(p);
        foreach (var pt in counts.Keys.ToList())
            foreach (var wi in doc.Wires)
                if (AscSchematic.OnSeg(pt.Item1, pt.Item2, wi) && (wi.X1, wi.Y1) != pt && (wi.X2, wi.Y2) != pt) Count(pt, 2);

        // wires
        string? hoverNet = hover is AscWire hw ? doc.WireNets.ElementAtOrDefault(doc.Wires.IndexOf(hw)) : null;
        for (int i = 0; i < doc.Wires.Count; i++)
        {
            var wi = doc.Wires[i];
            var col = ColorFor(wi, hoverNet != null && doc.WireNets[i] == hoverNet && Tool == SchTool.Select ? Theme.Accent2 : Theme.Wire);
            ds.DrawLine(S(wi.X1, wi.Y1), S(wi.X2, wi.Y2), col, sel.Contains(wi) ? sw * 1.7f : sw, round);
        }

        // symbols
        foreach (var sy in doc.Symbols) DrawSymbol(ds, sy, ColorFor(sy, Theme.Symbol), sw, label, false);

        // junction dots
        foreach (var kv in counts.Where(k => k.Value >= 3))
            ds.FillCircle(S(kv.Key.Item1, kv.Key.Item2), Math.Max(2.5f, 4.5f * scale), Theme.Wire);

        // unconnected pins
        foreach (var p in pins)
            if (counts.GetValueOrDefault(p) <= 1 && !doc.Flags.Any(f => f.X == p.Item1 && f.Y == p.Item2))
            {
                var c = S(p.Item1, p.Item2);
                float r = Math.Max(3, 4 * scale);
                ds.DrawRectangle(c.X - r, c.Y - r, 2 * r, 2 * r, PlotTheme.H("F87171"), 1.4f);
            }

        // flags
        foreach (var f in doc.Flags) DrawFlag(ds, f, ColorFor(f, f.IsGround ? Theme.Symbol : Theme.Accent), sw, label, false);

        // texts
        foreach (var t in doc.Texts)
        {
            var col = ColorFor(t, t.Directive ? Theme.Directive : Theme.Muted);
            ds.DrawText(t.Text, S(t.X, t.Y - 9), col, mono);
            if (sel.Contains(t))
            {
                var r = TextRect(t);
                var a = S((float)r.Left - 3, (float)r.Top - 2); var b = S((float)r.Right + 3, (float)r.Bottom + 2);
                ds.DrawRoundedRectangle(new Rect(a.X, a.Y, b.X - a.X, b.Y - a.Y), 4, 4, Theme.Accent, 1, dash);
            }
        }

        // tool previews
        if (cursorInside && drag == Drag.None)
        {
            var ghost = PlotTheme.WithAlpha(Theme.Accent, 200);
            switch (Tool)
            {
                case SchTool.Place:
                    var (ox, oy) = PlaceOrigin(PlaceType, placeRot, snapCursor);
                    DrawSymbol(ds, new AscSymbol { Type = PlaceType, X = ox, Y = oy, Rot = placeRot }, ghost, sw, label, true);
                    break;
                case SchTool.Ground:
                    DrawFlag(ds, new AscFlag { X = snapCursor.X, Y = snapCursor.Y, Name = "0" }, ghost, sw, label, true);
                    break;
                case SchTool.Label:
                    DrawFlag(ds, new AscFlag { X = snapCursor.X, Y = snapCursor.Y, Name = pendingLabel }, ghost, sw, label, true);
                    break;
                case SchTool.Text when pendingText != null:
                    ds.DrawText(pendingText.Text, S(snapCursor.X, snapCursor.Y - 9), ghost, mono);
                    break;
                case SchTool.Wire:
                    var c = S(snapCursor.X, snapCursor.Y);
                    ds.DrawCircle(c, Math.Max(3, 5 * scale), Theme.Accent, 1.5f);
                    if (wireStart is (int sx, int sy0))
                    {
                        var corner = S(snapCursor.X, sy0);
                        ds.DrawLine(S(sx, sy0), corner, Theme.Accent, sw, dash);
                        ds.DrawLine(corner, c, Theme.Accent, sw, dash);
                    }
                    break;
            }
        }

        if (drag == Drag.Band)
        {
            var a = S(bandStart); var b = S(bandNow);
            var r = new Rect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
            ds.FillRectangle(r, PlotTheme.WithAlpha(Theme.Accent, 36));
            ds.DrawRectangle(r, Theme.Accent, 1);
        }

        DrawHint(ds, w, h);
    }

    void DrawSymbol(CanvasDrawingSession ds, AscSymbol sy, Color col, float sw, CanvasTextFormat label, bool ghost)
    {
        var shape = SymbolShape.Get(sy.Type);
        float width = sel.Contains(sy) ? sw * 1.5f : sw;
        foreach (var line in shape.Lines)
        {
            using var pb = new CanvasPathBuilder(ds);
            pb.BeginFigure(S(SymbolShape.Transform(line[0], sy.Rot, sy.X, sy.Y)));
            for (int k = 1; k < line.Length; k++) pb.AddLine(S(SymbolShape.Transform(line[k], sy.Rot, sy.X, sy.Y)));
            pb.EndFigure(CanvasFigureLoop.Open);
            using var g = CanvasGeometry.CreatePath(pb);
            ds.DrawGeometry(g, col, width, ghost ? dash : round);
        }
        foreach (var (c, r) in shape.Circles)
            ds.DrawCircle(S(SymbolShape.Transform(c, sy.Rot, sy.X, sy.Y)), r * scale, col, width);
        if (ghost) return;
        var (min, max) = Bounds(sy);
        var center = (min + max) / 2;
        bool horizontal = max.X - min.X > max.Y - min.Y;
        string txt = string.IsNullOrEmpty(sy.Value) ? sy.Inst : $"{sy.Inst}\n{sy.Value}";
        if (!shape.Known) txt = $"{sy.Type}\n{txt}";
        var at = horizontal ? S(center.X - 18, min.Y - 34) : S(max.X + 8, center.Y - 16);
        ds.DrawText(txt, at, sel.Contains(sy) ? Theme.Accent : Theme.Text, label);
    }

    void DrawFlag(CanvasDrawingSession ds, AscFlag f, Color col, float sw, CanvasTextFormat label, bool ghost)
    {
        if (f.IsGround)
        {
            using var pb = new CanvasPathBuilder(ds);
            pb.BeginFigure(S(f.X - 16, f.Y)); pb.AddLine(S(f.X + 16, f.Y)); pb.AddLine(S(f.X, f.Y + 16)); pb.EndFigure(CanvasFigureLoop.Closed);
            using var g = CanvasGeometry.CreatePath(pb);
            ds.DrawGeometry(g, col, sel.Contains(f) ? sw * 1.5f : sw, ghost ? dash : round);
            return;
        }
        var r = LabelRect(f);
        var a = S((float)r.Left, (float)r.Top); var b = S((float)r.Right, (float)r.Bottom);
        var rect = new Rect(a.X, a.Y, b.X - a.X, b.Y - a.Y);
        ds.FillRoundedRectangle(rect, 6 * scale, 6 * scale, PlotTheme.WithAlpha(col, (byte)(sel.Contains(f) ? 90 : 42)));
        if (ghost || sel.Contains(f)) ds.DrawRoundedRectangle(rect, 6 * scale, 6 * scale, col, 1);
        ds.DrawText(f.Name, new Rect(rect.X, rect.Y + 1 * scale, rect.Width, rect.Height), col,
            new CanvasTextFormat { FontFamily = "Segoe UI", FontSize = label.FontSize, HorizontalAlignment = CanvasHorizontalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap });
        ds.FillCircle(S(f.X, f.Y), Math.Max(2f, 3f * scale), col);
        ds.DrawLine(S(f.X, f.Y), S(f.X, (float)r.Bottom), col, 1);
    }

    void DrawHint(CanvasDrawingSession ds, float w, float h)
    {
        string hint = Tool switch
        {
            SchTool.Wire => wireStart == null ? "Wire · click to start" : "Wire · click to bend · ends on a connection · double-click / right-click to finish · Esc",
            SchTool.Place => $"Place {SymbolShape.Placeable.FirstOrDefault(p => p.Type == PlaceType).Name ?? PlaceType} · click to drop · Ctrl+R rotate · Ctrl+E mirror · Esc",
            SchTool.Ground => "Ground · click to place · Esc",
            SchTool.Label => $"Net label \"{pendingLabel}\" · click on a wire or pin · Esc",
            SchTool.Text => "Click to place the text",
            _ => hover switch
            {
                AscSymbol s => $"{s.Inst} · double-click to edit · right-click for more · drag to move",
                AscWire wi when doc?.WireNets.ElementAtOrDefault(doc.Wires.IndexOf(wi)) is string n => $"Net {n} · double-click to plot V({n})",
                AscFlag f when !f.IsGround => $"Label {f.Name} · double-click to rename",
                AscText => "Double-click to edit",
                _ => "",
            },
        };
        if (hint.Length == 0) return;
        var fmt = new CanvasTextFormat { FontFamily = "Segoe UI", FontSize = 12, WordWrapping = CanvasWordWrapping.NoWrap };
        using var lay = new CanvasTextLayout(ds, hint, fmt, 2000, 30);
        var r = new Rect(12, h - 38, lay.LayoutBounds.Width + 24, 26);
        ds.FillRoundedRectangle(r, 9, 9, PlotTheme.WithAlpha(Theme.Bg, 235));
        ds.DrawRoundedRectangle(r, 9, 9, Theme.Frame, 1);
        ds.DrawText(hint, (float)r.X + 12, (float)r.Y + 5, Theme.Text, fmt);
    }

    // ================= input =================
    static bool KeyDownState(VirtualKey k) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(k) & CoreVirtualKeyStates.Down) != 0;

    void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        var pt = e.GetCurrentPoint(cv);
        var pos = pt.Position;
        var w = ToWorld(pos);
        var snap = Snap(w);
        pressPos = pos; dragMovedPx = false;
        cv.CapturePointer(e.Pointer);
        e.Handled = true;

        if (pt.Properties.IsMiddleButtonPressed || pt.Properties.IsRightButtonPressed)
        {
            drag = Drag.Pan; pressedRight = pt.Properties.IsRightButtonPressed; offStart = offset;
            return;
        }
        if (!pt.Properties.IsLeftButtonPressed || doc == null) return;
        LeftPress(w, KeyDownState(VirtualKey.Shift) || KeyDownState(VirtualKey.Control));
    }

    /// <summary>Simulate a left click at world coordinates (used by the self-test).</summary>
    public void SimulateClick(int x, int y)
    {
        LeftPress(new Vector2(x, y), false);
        if (drag == Drag.Move && moveDelta != (0, 0) && moveSnapshot != null) { undo.Push(moveSnapshot); redo.Clear(); Commit(); }
        drag = Drag.None;
    }

    void LeftPress(Vector2 w, bool shift)
    {
        if (doc == null) return;
        var snap = Snap(w);
        switch (Tool)
        {
            case SchTool.Select:
                var hit = Hit(w);
                if (hit != null)
                {
                    if (shift) { if (!sel.Remove(hit)) sel.Add(hit); }
                    else if (!sel.Contains(hit)) { sel.Clear(); sel.Add(hit); }
                    BeginMove(snap);
                    drag = Drag.Move;
                }
                else
                {
                    if (!shift) sel.Clear();
                    drag = Drag.Band; bandStart = bandNow = w;
                }
                break;
            case SchTool.Wire:
                WireClick(snap);
                break;
            case SchTool.Place:
                PushUndo();
                var info = SymbolShape.Placeable.FirstOrDefault(p => p.Type == PlaceType);
                var (ox, oy) = PlaceOrigin(PlaceType, placeRot, snap);
                var sym = new AscSymbol { RawType = PlaceType, Type = PlaceType, X = ox, Y = oy, Rot = placeRot };
                sym.Inst = doc.NextInstName(info.Prefix ?? "X");
                sym.Value = info.Value ?? "";
                doc.Symbols.Add(sym);
                Commit();
                break;
            case SchTool.Ground:
                PushUndo();
                doc.Flags.Add(new AscFlag { X = snap.X, Y = snap.Y, Name = "0" });
                Commit();
                break;
            case SchTool.Label:
                PushUndo();
                doc.Flags.Add(new AscFlag { X = snap.X, Y = snap.Y, Name = pendingLabel });
                Commit();
                break;
            case SchTool.Text when pendingText != null:
                PushUndo();
                pendingText.X = snap.X; pendingText.Y = snap.Y;
                doc.Texts.Add(pendingText);
                pendingText = null;
                Commit();
                SetTool(SchTool.Select);
                break;
        }
        cv.Invalidate();
    }

    void WireClick((int X, int Y) p)
    {
        if (doc == null) return;
        if (wireStart is not (int sx, int sy)) { wireStart = p; return; }
        if ((sx, sy) == p) return;
        PushUndo();
        var segs = new List<AscWire>();
        if (sx != p.X && sy != p.Y)
        {
            segs.Add(new AscWire(sx, sy, p.X, sy));
            segs.Add(new AscWire(p.X, sy, p.X, p.Y));
        }
        else segs.Add(new AscWire(sx, sy, p.X, p.Y));

        // Split new segments wherever they pass over a pin, flag or wire end, so every connection is explicit.
        var stops = doc.Symbols.SelectMany(doc.Pins).Concat(doc.Flags.Select(f => (f.X, f.Y)))
            .Concat(doc.Wires.SelectMany(w => new[] { (w.X1, w.Y1), (w.X2, w.Y2) })).Distinct().ToList();
        var added = new List<AscWire>();
        foreach (var seg in segs)
        {
            var cuts = stops.Where(q => AscSchematic.OnSeg(q.Item1, q.Item2, seg) && q != (seg.X1, seg.Y1) && q != (seg.X2, seg.Y2))
                .OrderBy(q => Math.Abs(q.Item1 - seg.X1) + Math.Abs(q.Item2 - seg.Y1)).ToList();
            var prev = (seg.X1, seg.Y1);
            foreach (var c in cuts) { added.Add(new AscWire(prev.Item1, prev.Item2, c.Item1, c.Item2)); prev = c; }
            added.Add(new AscWire(prev.Item1, prev.Item2, seg.X2, seg.Y2));
        }
        doc.Wires.AddRange(added);
        Commit();
        wireStart = doc.IsConnectionPoint(p.X, p.Y, added) ? null : p;
    }

    void BeginMove((int X, int Y) snap)
    {
        if (doc == null) return;
        moveStart = snap; moveDelta = (0, 0);
        moveSnapshot = doc.Serialize();
        var anchors = new HashSet<(int, int)>();
        mvSyms = sel.OfType<AscSymbol>().Select(s => (s, s.X, s.Y)).ToList();
        foreach (var s in mvSyms) foreach (var p in doc.Pins(s.S)) anchors.Add(p);
        mvFlags = sel.OfType<AscFlag>().Select(f => (f, f.X, f.Y)).ToList();
        foreach (var f in mvFlags) anchors.Add((f.X, f.Y));
        mvTexts = sel.OfType<AscText>().Select(t => (t, t.X, t.Y)).ToList();
        mvWires = new();
        foreach (var w in doc.Wires)
        {
            if (sel.Contains(w)) { mvWires.Add((w, w.X1, w.Y1, w.X2, w.Y2, true, true)); anchors.Add((w.X1, w.Y1)); anchors.Add((w.X2, w.Y2)); }
        }
        foreach (var w in doc.Wires)
        {
            if (sel.Contains(w)) continue;
            bool m1 = anchors.Contains((w.X1, w.Y1)), m2 = anchors.Contains((w.X2, w.Y2));
            if (m1 || m2) mvWires.Add((w, w.X1, w.Y1, w.X2, w.Y2, m1, m2));
        }
    }

    void ApplyMove((int X, int Y) snap)
    {
        var d = (X: snap.X - moveStart.X, Y: snap.Y - moveStart.Y);
        if (d == moveDelta) return;
        moveDelta = d;
        foreach (var (s, x, y) in mvSyms) { s.X = x + d.X; s.Y = y + d.Y; }
        foreach (var (f, x, y) in mvFlags) { f.X = x + d.X; f.Y = y + d.Y; }
        foreach (var (t, x, y) in mvTexts) { t.X = x + d.X; t.Y = y + d.Y; }
        foreach (var (w, x1, y1, x2, y2, m1, m2) in mvWires)
        {
            w.X1 = x1 + (m1 ? d.X : 0); w.Y1 = y1 + (m1 ? d.Y : 0);
            w.X2 = x2 + (m2 ? d.X : 0); w.Y2 = y2 + (m2 ? d.Y : 0);
        }
    }

    void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(cv).Position;
        var w = ToWorld(pos);
        snapCursor = Snap(w);
        if (Math.Abs(pos.X - pressPos.X) + Math.Abs(pos.Y - pressPos.Y) > 4) dragMovedPx = true;
        switch (drag)
        {
            case Drag.Pan:
                offset = offStart + new Vector2((float)(pos.X - pressPos.X), (float)(pos.Y - pressPos.Y));
                break;
            case Drag.Move:
                ApplyMove(snapCursor);
                break;
            case Drag.Band:
                bandNow = w;
                break;
            default:
                if (Tool == SchTool.Select)
                {
                    var h = Hit(w);
                    if (!ReferenceEquals(h, hover))
                    {
                        hover = h;
                        ProtectedCursor = InputSystemCursor.Create(h != null ? InputSystemCursorShape.SizeAll : InputSystemCursorShape.Arrow);
                    }
                }
                break;
        }
        cv.Invalidate();
    }

    void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(cv).Position;
        switch (drag)
        {
            case Drag.Pan when pressedRight && !dragMovedPx:
                RightClick(pos);
                break;
            case Drag.Move:
                if (moveDelta != (0, 0) && moveSnapshot != null) { undo.Push(moveSnapshot); redo.Clear(); Commit(); }
                break;
            case Drag.Band when doc != null:
                float x0 = Math.Min(bandStart.X, bandNow.X), x1 = Math.Max(bandStart.X, bandNow.X);
                float y0 = Math.Min(bandStart.Y, bandNow.Y), y1 = Math.Max(bandStart.Y, bandNow.Y);
                bool In(float x, float y) => x >= x0 && x <= x1 && y >= y0 && y <= y1;
                foreach (var s in doc.Symbols) { var (a, b) = Bounds(s); if (In(a.X, a.Y) && In(b.X, b.Y)) sel.Add(s); }
                foreach (var wi in doc.Wires) if (In(wi.X1, wi.Y1) && In(wi.X2, wi.Y2)) sel.Add(wi);
                foreach (var f in doc.Flags) if (In(f.X, f.Y)) sel.Add(f);
                foreach (var t in doc.Texts) if (In(t.X, t.Y)) sel.Add(t);
                break;
        }
        drag = Drag.None;
        cv.ReleasePointerCapture(e.Pointer);
        cv.Invalidate();
    }

    void RightClick(Point pos)
    {
        if (Tool == SchTool.Wire && wireStart != null) { wireStart = null; cv.Invalidate(); return; }
        if (Tool != SchTool.Select) { SetTool(SchTool.Select); return; }
        if (doc == null) return;
        var hit = Hit(ToWorld(pos));
        if (hit != null && !sel.Contains(hit)) { sel.Clear(); sel.Add(hit); }
        var menu = new MenuFlyout();
        void Item(string text, string glyph, Action a)
        {
            var mi = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
        }
        switch (hit)
        {
            case AscSymbol s:
                Item("Edit properties…", "", () => EditRequested?.Invoke(s));
                Item($"Plot I({s.Inst})", "", () => Probe?.Invoke($"I({s.Inst})"));
                menu.Items.Add(new MenuFlyoutSeparator());
                Item("Rotate   Ctrl+R", "", RotateSelection);
                Item("Mirror   Ctrl+E", "", MirrorSelection);
                Item("Copy   Ctrl+C", "", Copy);
                Item("Delete   Del", "", DeleteSelection);
                break;
            case AscWire wi:
                if (doc.WireNets.ElementAtOrDefault(doc.Wires.IndexOf(wi)) is string net && net != "0")
                    Item($"Plot V({net})", "", () => Probe?.Invoke($"V({net})"));
                Item("Delete   Del", "", DeleteSelection);
                break;
            case AscFlag f:
                if (!f.IsGround)
                {
                    Item("Rename…", "", () => EditRequested?.Invoke(f));
                    Item($"Plot V({f.Name})", "", () => Probe?.Invoke($"V({f.Name})"));
                }
                Item("Delete   Del", "", DeleteSelection);
                break;
            case AscText t:
                Item("Edit…", "", () => EditRequested?.Invoke(t));
                Item("Delete   Del", "", DeleteSelection);
                break;
            default:
                Item("Paste   Ctrl+V", "", Paste);
                Item("Select all   Ctrl+A", "", SelectAll);
                Item("Fit to window   F", "", Fit);
                break;
        }
        cv.Invalidate();
        menu.ShowAt(cv, new FlyoutShowOptions { Position = pos });
    }

    void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (doc == null) return;
        if (Tool == SchTool.Wire) { wireStart = null; cv.Invalidate(); return; }
        if (Tool != SchTool.Select) return;
        var hit = Hit(ToWorld(e.GetPosition(cv)));
        switch (hit)
        {
            case AscWire wi when doc.WireNets.ElementAtOrDefault(doc.Wires.IndexOf(wi)) is string net && net != "0":
                Probe?.Invoke($"V({net})");
                break;
            case AscSymbol or AscText:
            case AscFlag { IsGround: false }:
                EditRequested?.Invoke(hit);
                break;
            case null:
                Fit();
                break;
        }
    }

    void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(cv);
        float f = pt.Properties.MouseWheelDelta > 0 ? 1.18f : 1 / 1.18f;
        var p = new Vector2((float)pt.Position.X, (float)pt.Position.Y);
        var world = (p - offset) / scale;
        scale = Math.Clamp(scale * f, 0.1f, 12f);
        offset = p - world * scale;
        cv.Invalidate();
        e.Handled = true;
    }

    void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = KeyDownState(VirtualKey.Control), shift = KeyDownState(VirtualKey.Shift);
        bool handled = true;
        switch (e.Key)
        {
            case VirtualKey.Escape:
                if (wireStart != null) wireStart = null;
                else if (Tool != SchTool.Select) SetTool(SchTool.Select);
                else sel.Clear();
                break;
            case VirtualKey.Delete: case VirtualKey.Back: DeleteSelection(); break;
            case VirtualKey.R when ctrl: RotateSelection(); break;
            case VirtualKey.E when ctrl: MirrorSelection(); break;
            case VirtualKey.Z when ctrl: if (shift) Redo(); else Undo(); break;
            case VirtualKey.Y when ctrl: Redo(); break;
            case VirtualKey.A when ctrl: SelectAll(); break;
            case VirtualKey.C when ctrl: Copy(); break;
            case VirtualKey.V when ctrl: Paste(); break;
            case VirtualKey.X when ctrl: Copy(); DeleteSelection(); break;
            case VirtualKey.R when !ctrl: BeginPlace("res"); break;
            case VirtualKey.C when !ctrl: BeginPlace("cap"); break;
            case VirtualKey.L when !ctrl: BeginPlace("ind"); break;
            case VirtualKey.D when !ctrl: BeginPlace("diode"); break;
            case VirtualKey.V when !ctrl: BeginPlace("voltage"); break;
            case VirtualKey.I when !ctrl: BeginPlace("current"); break;
            case VirtualKey.G when !ctrl: SetTool(SchTool.Ground); break;
            case VirtualKey.W when !ctrl: case VirtualKey.F3: SetTool(SchTool.Wire); break;
            case VirtualKey.N when !ctrl: case VirtualKey.F4: LabelRequested?.Invoke(); break;
            case VirtualKey.T when !ctrl: TextRequested?.Invoke(); break;
            case VirtualKey.F when !ctrl: Fit(); break;
            default: handled = false; break;
        }
        if (handled) { e.Handled = true; cv.Invalidate(); }
    }
}
