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
using Windows.System;
using Windows.UI;

namespace NovaSpice.Controls;

public sealed class PlotSeries
{
    public string Label = "", Unit = "", Key = "";
    public double[] X = Array.Empty<double>();
    public double[] Y = Array.Empty<double>();
    public Color Color;
}

public sealed class PlotPane
{
    public List<PlotSeries> Series = new();
    public bool AutoY = true;
    public double YMin = -1, YMax = 1;
    public string Title = "";
    public string Unit => Series.Count > 0 && Series.All(s => s.Unit == Series[0].Unit) ? Series[0].Unit : "";
}

/// <summary>
/// GPU-drawn (Win2D) multi-pane waveform plot: shared X axis, log/linear, min-max decimation,
/// rectangle zoom, wheel zoom, pan, A/B cursors and a live hover readout.
/// </summary>
public sealed class WaveformCanvas : UserControl
{
    readonly CanvasControl cv = new();
    public List<PlotPane> Panes { get; } = new();
    public PlotTheme Theme { get; set; } = PlotTheme.Dark;
    public bool LogX { get; set; }
    public string XUnit { get; set; } = "s";
    public string XTitle { get; set; } = "";
    public double XMin { get; set; } = 0;
    public double XMax { get; set; } = 1;
    public int ActivePane { get; set; }
    public bool ShowActive { get; set; } = true;
    public bool CursorsOn { get; private set; }
    public double CurA { get; private set; }
    public double CurB { get; private set; }
    public string EmptyText { get; set; } = "No waveform loaded";
    public string EmptyHint { get; set; } = "Open a .raw file, run a simulation, or try a demo from Home";

    public event Action? CursorsChanged;
    public event Action<string>? HoverChanged;
    public event Action<int>? ActivePaneChanged;

    const float L = 76, R = 18, T = 10, B = 30, Gap = 14;
    float W = 100, H = 100;

    enum Drag { None, Zoom, Pan, CurA, CurB }
    Drag drag;
    Point dragStart, dragNow;
    int dragPane;
    double panX0, panX1, panY0, panY1;
    Point? hover;

    readonly CanvasTextFormat fmtSmall = new() { FontFamily = "Segoe UI", FontSize = 11, WordWrapping = CanvasWordWrapping.NoWrap };
    readonly CanvasTextFormat fmtRight = new() { FontFamily = "Segoe UI", FontSize = 11, HorizontalAlignment = CanvasHorizontalAlignment.Right, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
    readonly CanvasTextFormat fmtCenter = new() { FontFamily = "Segoe UI", FontSize = 11, HorizontalAlignment = CanvasHorizontalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
    readonly CanvasTextFormat fmtBig = new() { FontFamily = "Segoe UI", FontSize = 18, HorizontalAlignment = CanvasHorizontalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
    readonly CanvasTextFormat fmtBold = new() { FontFamily = "Segoe UI", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, WordWrapping = CanvasWordWrapping.NoWrap };
    readonly CanvasStrokeStyle round = new() { LineJoin = CanvasLineJoin.Round, StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round };
    readonly CanvasStrokeStyle dash = new() { DashStyle = CanvasDashStyle.Dash };

    public WaveformCanvas()
    {
        Content = cv;
        IsTabStop = true;
        cv.Draw += (s, e) => Render(e.DrawingSession, (float)s.ActualWidth, (float)s.ActualHeight, true);
        cv.PointerPressed += OnPressed;
        cv.PointerMoved += OnMoved;
        cv.PointerReleased += OnReleased;
        cv.PointerExited += (_, _) => { hover = null; HoverChanged?.Invoke(""); cv.Invalidate(); };
        cv.PointerWheelChanged += OnWheel;
        cv.DoubleTapped += (_, _) => FitAll();
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Cross);
    }

    public void Invalidate() => cv.Invalidate();

    // ---------- coordinates ----------
    double Tx(double x) => LogX ? Math.Log10(Math.Max(x, 1e-300)) : x;
    double ITx(double t) => LogX ? Math.Pow(10, t) : t;
    float PlotW => Math.Max(1, W - L - R);

    Rect PaneRect(int i)
    {
        int n = Math.Max(1, Panes.Count);
        float ph = Math.Max(20, (H - T - B - Gap * (n - 1)) / n);
        return new Rect(L, T + i * (ph + Gap), PlotW, ph);
    }

    float SX(double x) => (float)(L + (Tx(x) - Tx(XMin)) / (Tx(XMax) - Tx(XMin)) * PlotW);
    double DX(double sx) => ITx(Tx(XMin) + (sx - L) / PlotW * (Tx(XMax) - Tx(XMin)));
    static float SY(double y, PlotPane p, Rect r) => (float)Math.Clamp(r.Bottom - (y - p.YMin) / (p.YMax - p.YMin) * r.Height, -1e5, 1e5);
    static double DY(double sy, PlotPane p, Rect r) => p.YMin + (r.Bottom - sy) / r.Height * (p.YMax - p.YMin);

    int PaneAt(Point p)
    {
        for (int i = 0; i < Panes.Count; i++)
        {
            var r = PaneRect(i);
            if (p.Y >= r.Top - Gap / 2 && p.Y <= r.Bottom + Gap / 2 && p.X >= 0) return i;
        }
        return -1;
    }

    (double Lo, double Hi) DataRange()
    {
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var p in Panes)
            foreach (var s in p.Series)
                if (s.X.Length > 0)
                {
                    double a = s.X[0], b = s.X[^1];
                    if (LogX && a <= 0) a = s.X.FirstOrDefault(v => v > 0, 1);
                    lo = Math.Min(lo, a); hi = Math.Max(hi, b);
                }
        if (lo >= hi) return (lo == double.MaxValue ? 0 : lo, lo == double.MaxValue ? 1 : lo + 1);
        return (lo, hi);
    }

    public void FitAll()
    {
        (XMin, XMax) = DataRange();
        foreach (var p in Panes) p.AutoY = true;
        cv.Invalidate();
    }

    public void SetCursors(bool on)
    {
        CursorsOn = on;
        if (on)
        {
            double t0 = Tx(XMin), t1 = Tx(XMax);
            if (!(CurA > XMin && CurA < XMax)) CurA = ITx(t0 + 0.25 * (t1 - t0));
            if (!(CurB > XMin && CurB < XMax)) CurB = ITx(t0 + 0.75 * (t1 - t0));
        }
        CursorsChanged?.Invoke();
        cv.Invalidate();
    }

    void AutoY(PlotPane p)
    {
        double mn = double.MaxValue, mx = double.MinValue;
        foreach (var s in p.Series)
        {
            if (s.X.Length == 0) continue;
            int a = Math.Max(0, Dsp.LowerBound(s.X, XMin) - 1), b = Math.Min(s.X.Length - 1, Dsp.LowerBound(s.X, XMax));
            for (int i = a; i <= b; i++)
            {
                double y = s.Y[i];
                if (double.IsFinite(y)) { if (y < mn) mn = y; if (y > mx) mx = y; }
            }
        }
        if (mn > mx) { mn = -1; mx = 1; }
        double span = mx - mn;
        if (span < Math.Max(Math.Abs(mx), Math.Abs(mn)) * 1e-9 || span == 0)
        {
            double d = Math.Abs(mx) > 0 ? Math.Abs(mx) * 0.1 : 1;
            mn -= d; mx += d; span = mx - mn;
        }
        p.YMin = mn - span * 0.07;
        p.YMax = mx + span * 0.07;
    }

    // ---------- rendering ----------
    public void Render(CanvasDrawingSession ds, float w, float h, bool interactive)
    {
        W = w; H = h;
        ds.Clear(Theme.Bg);
        if (Panes.Count == 0 || Panes.All(p => p.Series.Count == 0) && Panes.Count == 1)
        {
            ds.DrawText(EmptyText, new Rect(0, h / 2 - 30, w, 30), Theme.Text, fmtBig);
            ds.DrawText(EmptyHint, new Rect(0, h / 2 + 4, w, 20), Theme.Muted, fmtCenter);
            if (Panes.Count == 0) return;
        }
        if (XMax <= XMin) (XMin, XMax) = DataRange();

        for (int i = 0; i < Panes.Count; i++)
        {
            var p = Panes[i];
            var r = PaneRect(i);
            if (p.AutoY) AutoY(p);
            ds.FillRoundedRectangle(r, 8, 8, Theme.Surface);
            DrawGrid(ds, p, r, i == Panes.Count - 1);
            using (ds.CreateLayer(1f, r))
            {
                foreach (var s in p.Series) DrawSeries(ds, s, p, r);
                if (CursorsOn) DrawCursors(ds, p, r, i == 0);
                if (drag == Drag.Zoom && i == dragPane)
                {
                    var zr = new Rect(new Point(Math.Min(dragStart.X, dragNow.X), Math.Min(dragStart.Y, dragNow.Y)),
                                      new Point(Math.Max(dragStart.X, dragNow.X), Math.Max(dragStart.Y, dragNow.Y)));
                    ds.FillRectangle(zr, PlotTheme.WithAlpha(Theme.Accent, 40));
                    ds.DrawRectangle(zr, Theme.Accent, 1);
                }
            }
            bool active = ShowActive && Panes.Count > 1 && i == ActivePane;
            ds.DrawRoundedRectangle(r, 8, 8, active ? Theme.Accent : Theme.Frame, active ? 1.6f : 1f);
            DrawLegend(ds, p, r);
        }
        if (!string.IsNullOrEmpty(XTitle))
            ds.DrawText(XTitle + (XUnit.Length > 0 && !LogX ? "" : ""), new Rect(L, H - 16, PlotW, 16), Theme.Muted, fmtRight);
        if (interactive && hover is Point hp && drag == Drag.None) DrawHover(ds, hp);
    }

    void DrawGrid(CanvasDrawingSession ds, PlotPane p, Rect r, bool xLabels)
    {
        // Y
        double ystep = Si.NiceStep(p.YMax - p.YMin, Math.Max(2, (int)(r.Height / 44)));
        double yabs = Math.Max(Math.Abs(p.YMin), Math.Abs(p.YMax));
        for (double v = Math.Ceiling(p.YMin / ystep) * ystep; v <= p.YMax; v += ystep)
        {
            float y = SY(v, p, r);
            ds.DrawLine((float)r.Left, y, (float)r.Right, y, Theme.Grid, 1);
            ds.DrawText(Si.FmtAxis(v, ystep, yabs, p.Unit), new Rect(0, y - 9, L - 8, 18), Theme.Muted, fmtRight);
        }
        // X
        if (LogX)
        {
            int d0 = (int)Math.Floor(Math.Log10(XMin)), d1 = (int)Math.Ceiling(Math.Log10(XMax));
            bool labelEvery = d1 - d0 <= PlotW / 70;
            for (int d = d0; d <= d1; d++)
            {
                for (int k = 1; k <= 9; k++)
                {
                    double v = k * Math.Pow(10, d);
                    if (v < XMin || v > XMax) continue;
                    float x = SX(v);
                    ds.DrawLine(x, (float)r.Top, x, (float)r.Bottom, k == 1 ? Theme.Grid : Theme.GridMinor, 1);
                    if (k == 1 && xLabels && (labelEvery || d % 2 == 0))
                        ds.DrawText(Si.Fmt(v, XUnit, 3), new Rect(x - 50, r.Bottom + 6, 100, 16), Theme.Muted, fmtCenter);
                }
            }
        }
        else
        {
            double xstep = Si.NiceStep(XMax - XMin, Math.Max(2, (int)(PlotW / 110)));
            double xabs = Math.Max(Math.Abs(XMin), Math.Abs(XMax));
            for (double v = Math.Ceiling(XMin / xstep) * xstep; v <= XMax; v += xstep)
            {
                float x = SX(v);
                ds.DrawLine(x, (float)r.Top, x, (float)r.Bottom, Theme.Grid, 1);
                if (xLabels) ds.DrawText(Si.FmtAxis(v, xstep, xabs, XUnit), new Rect(x - 60, r.Bottom + 6, 120, 16), Theme.Muted, fmtCenter);
            }
        }
    }

    void DrawSeries(CanvasDrawingSession ds, PlotSeries s, PlotPane p, Rect r)
    {
        var X = s.X; var Y = s.Y;
        int n = X.Length;
        if (n == 0) return;
        int lo = Math.Max(0, Dsp.LowerBound(X, XMin) - 1), hi = Math.Min(n - 1, Dsp.LowerBound(X, XMax));
        if (hi < lo) return;
        var pts = new List<Vector2>(Math.Min(hi - lo + 2, (int)PlotW * 4 + 8));
        if (hi - lo > PlotW * 2)
        {
            int col = int.MinValue; double mn = 0, mx = 0; int imn = 0, imx = 0; float cx = 0;
            void Flush()
            {
                if (col == int.MinValue) return;
                if (imn <= imx) { pts.Add(new(cx, SY(mn, p, r))); pts.Add(new(cx, SY(mx, p, r))); }
                else { pts.Add(new(cx, SY(mx, p, r))); pts.Add(new(cx, SY(mn, p, r))); }
            }
            for (int i = lo; i <= hi; i++)
            {
                float x = SX(X[i]);
                int c = (int)x;
                double y = Y[i];
                if (c != col) { Flush(); col = c; cx = x; mn = mx = y; imn = imx = i; }
                else { if (y < mn) { mn = y; imn = i; } if (y > mx) { mx = y; imx = i; } }
            }
            Flush();
        }
        else
            for (int i = lo; i <= hi; i++) pts.Add(new(SX(X[i]), SY(Y[i], p, r)));
        if (pts.Count < 2) return;
        using var pb = new CanvasPathBuilder(ds);
        pb.BeginFigure(pts[0]);
        for (int i = 1; i < pts.Count; i++) pb.AddLine(pts[i]);
        pb.EndFigure(CanvasFigureLoop.Open);
        using var geo = CanvasGeometry.CreatePath(pb);
        ds.DrawGeometry(geo, s.Color, 1.6f, round);
    }

    void DrawCursors(CanvasDrawingSession ds, PlotPane p, Rect r, bool labels)
    {
        foreach (var (pos, color, name) in new[] { (CurA, Theme.CurA, "A"), (CurB, Theme.CurB, "B") })
        {
            float x = SX(pos);
            ds.DrawLine(x, (float)r.Top, x, (float)r.Bottom, color, 1.2f, dash);
            foreach (var s in p.Series)
            {
                double y = Dsp.Interp(s.X, s.Y, pos);
                if (!double.IsFinite(y)) continue;
                float sy = SY(y, p, r);
                ds.FillCircle(x, sy, 4.2f, Theme.Surface);
                ds.DrawCircle(x, sy, 4.2f, s.Color, 2f);
            }
            if (labels)
            {
                var chip = new Rect(x - 9, r.Top + 4, 18, 18);
                ds.FillRoundedRectangle(chip, 5, 5, color);
                ds.DrawText(name, new Rect(chip.X, chip.Y + 1, 18, 16), Theme.Bg, fmtCenter);
            }
        }
    }

    void DrawLegend(CanvasDrawingSession ds, PlotPane p, Rect r)
    {
        float x = (float)r.Left + 12, y = (float)r.Top + 8;
        if (!string.IsNullOrEmpty(p.Title))
        {
            float tw0 = MeasureW(ds, p.Title, fmtBold);
            ds.FillRoundedRectangle(new Rect(x - 4, y - 1, tw0 + 8, 17), 6, 6, PlotTheme.WithAlpha(Theme.Bg, 200));
            ds.DrawText(p.Title, x, y, Theme.Muted, fmtBold);
            x += 16 + tw0;
        }
        foreach (var s in p.Series)
        {
            float tw = MeasureW(ds, s.Label, fmtSmall);
            if (x + tw + 26 > r.Right - 10) { x = (float)r.Left + 12; y += 18; if (y > r.Bottom - 18) break; }
            var bg = new Rect(x - 4, y - 1, tw + 24, 17);
            ds.FillRoundedRectangle(bg, 6, 6, PlotTheme.WithAlpha(Theme.Bg, 170));
            ds.FillRoundedRectangle(new Rect(x + 1, y + 5, 10, 4), 2, 2, s.Color);
            ds.DrawText(s.Label, x + 16, y, Theme.Text, fmtSmall);
            x += tw + 32;
        }
    }

    static float MeasureW(CanvasDrawingSession ds, string text, CanvasTextFormat f)
    {
        using var layout = new CanvasTextLayout(ds, text, f, 2000, 100);
        return (float)layout.LayoutBounds.Width;
    }

    void DrawHover(CanvasDrawingSession ds, Point hp)
    {
        int i = PaneAt(hp);
        if (i < 0 || hp.X < L || hp.X > L + PlotW) return;
        var p = Panes[i];
        var r = PaneRect(i);
        double x = DX(hp.X);
        float sx = (float)hp.X;
        foreach (var (pi, pr) in Panes.Select((pp, k) => (pp, PaneRect(k))))
            ds.DrawLine(sx, (float)pr.Top, sx, (float)pr.Bottom, PlotTheme.WithAlpha(Theme.Muted, 110), 1);

        var lines = new List<(string Text, Color C)> { (Si.Fmt(x, XUnit, 5), Theme.Muted) };
        foreach (var s in p.Series.Take(10))
        {
            double y = Dsp.Interp(s.X, s.Y, x);
            if (!double.IsFinite(y)) continue;
            float sy = SY(y, p, r);
            if (sy >= r.Top && sy <= r.Bottom) ds.FillCircle(sx, sy, 3.5f, s.Color);
            lines.Add(($"{s.Label}   {Si.Fmt(y, s.Unit, 5)}", s.Color));
        }
        if (lines.Count <= 1) return;
        float tw = lines.Max(l => MeasureW(ds, l.Text, fmtSmall)) + 30, th = lines.Count * 17 + 10;
        float bx = sx + 16, by = (float)hp.Y + 14;
        if (bx + tw > W - 4) bx = sx - 16 - tw;
        if (by + th > H - 4) by = H - 4 - th;
        ds.FillRoundedRectangle(new Rect(bx + 2, by + 3, tw, th), 9, 9, PlotTheme.WithAlpha(Microsoft.UI.Colors.Black, 60));
        ds.FillRoundedRectangle(new Rect(bx, by, tw, th), 9, 9, PlotTheme.WithAlpha(Theme.Surface, 245));
        ds.DrawRoundedRectangle(new Rect(bx, by, tw, th), 9, 9, Theme.Frame, 1);
        for (int k = 0; k < lines.Count; k++)
        {
            float ly = by + 5 + k * 17;
            if (k > 0) ds.FillCircle(bx + 12, ly + 8, 4, lines[k].C);
            ds.DrawText(lines[k].Text, bx + (k > 0 ? 22 : 10), ly, k == 0 ? Theme.Muted : Theme.Text, k == 0 ? fmtBold : fmtSmall);
        }
    }

    // ---------- interaction ----------
    bool NearCursor(double px, double pos) => CursorsOn && Math.Abs(px - SX(pos)) < 7;

    void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(Microsoft.UI.Xaml.FocusState.Pointer);
        var pt = e.GetCurrentPoint(cv);
        var pos = pt.Position;
        int pi = PaneAt(pos);
        if (pi < 0) return;
        cv.CapturePointer(e.Pointer);
        dragStart = dragNow = pos; dragPane = pi;
        if (pt.Properties.IsLeftButtonPressed)
        {
            if (NearCursor(pos.X, CurA)) drag = Drag.CurA;
            else if (NearCursor(pos.X, CurB)) drag = Drag.CurB;
            else drag = Drag.Zoom;
        }
        else if (pt.Properties.IsRightButtonPressed || pt.Properties.IsMiddleButtonPressed)
        {
            drag = Drag.Pan;
            panX0 = XMin; panX1 = XMax;
            panY0 = Panes[pi].YMin; panY1 = Panes[pi].YMax;
        }
        e.Handled = true;
    }

    void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(cv).Position;
        hover = pos;
        switch (drag)
        {
            case Drag.CurA: CurA = Math.Clamp(DX(pos.X), XMin, XMax); CursorsChanged?.Invoke(); break;
            case Drag.CurB: CurB = Math.Clamp(DX(pos.X), XMin, XMax); CursorsChanged?.Invoke(); break;
            case Drag.Zoom: dragNow = pos; break;
            case Drag.Pan:
                double dt = (pos.X - dragStart.X) / PlotW * (Tx(panX1) - Tx(panX0));
                XMin = ITx(Tx(panX0) - dt); XMax = ITx(Tx(panX1) - dt);
                if (Math.Abs(pos.Y - dragStart.Y) > 3)
                {
                    var p = Panes[dragPane]; var r = PaneRect(dragPane);
                    double dy = (pos.Y - dragStart.Y) / r.Height * (panY1 - panY0);
                    p.AutoY = false; p.YMin = panY0 + dy; p.YMax = panY1 + dy;
                }
                break;
        }
        bool nearCur = NearCursor(pos.X, CurA) || NearCursor(pos.X, CurB) || drag is Drag.CurA or Drag.CurB;
        ProtectedCursor = InputSystemCursor.Create(drag == Drag.Pan ? InputSystemCursorShape.SizeAll
            : nearCur ? InputSystemCursorShape.SizeWestEast : InputSystemCursorShape.Cross);

        int i = PaneAt(pos);
        if (i >= 0 && pos.X >= L)
        {
            var p = Panes[i]; var r = PaneRect(i);
            HoverChanged?.Invoke($"x = {Si.Fmt(DX(pos.X), XUnit, 5)}     y = {Si.Fmt(DY(pos.Y, p, r), p.Unit, 5)}");
        }
        cv.Invalidate();
    }

    void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(cv).Position;
        if (drag == Drag.Zoom)
        {
            double dx = Math.Abs(pos.X - dragStart.X), dy = Math.Abs(pos.Y - dragStart.Y);
            if (dx > 6)
            {
                double a = DX(Math.Min(pos.X, dragStart.X)), b = DX(Math.Max(pos.X, dragStart.X));
                XMin = a; XMax = b;
            }
            if (dy > 6 && dragPane < Panes.Count)
            {
                var p = Panes[dragPane]; var r = PaneRect(dragPane);
                double y0 = DY(Math.Max(pos.Y, dragStart.Y), p, r), y1 = DY(Math.Min(pos.Y, dragStart.Y), p, r);
                p.AutoY = false; p.YMin = y0; p.YMax = y1;
            }
            if (dx <= 6 && dy <= 6 && ActivePane != dragPane)
            {
                ActivePane = dragPane;
                ActivePaneChanged?.Invoke(ActivePane);
            }
        }
        if (drag is Drag.CurA or Drag.CurB) CursorsChanged?.Invoke();
        drag = Drag.None;
        cv.ReleasePointerCapture(e.Pointer);
        cv.Invalidate();
    }

    void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(cv);
        var pos = pt.Position;
        int delta = pt.Properties.MouseWheelDelta;
        double f = delta > 0 ? 1 / 1.25 : 1.25;
        var mods = e.KeyModifiers;
        if (mods.HasFlag(VirtualKeyModifiers.Control))
        {
            int i = PaneAt(pos);
            if (i >= 0)
            {
                var p = Panes[i]; var r = PaneRect(i);
                double c = DY(pos.Y, p, r);
                p.AutoY = false;
                p.YMin = c - (c - p.YMin) * f; p.YMax = c + (p.YMax - c) * f;
            }
        }
        else if (mods.HasFlag(VirtualKeyModifiers.Shift))
        {
            double t0 = Tx(XMin), t1 = Tx(XMax), d = (t1 - t0) * 0.1 * Math.Sign(-delta);
            XMin = ITx(t0 + d); XMax = ITx(t1 + d);
        }
        else
        {
            double t0 = Tx(XMin), t1 = Tx(XMax), tc = Tx(DX(Math.Clamp(pos.X, L, L + PlotW)));
            XMin = ITx(tc - (tc - t0) * f); XMax = ITx(tc + (t1 - tc) * f);
        }
        e.Handled = true;
        cv.Invalidate();
    }

    public async Task SavePngAsync(string path)
    {
        float w = (float)Math.Max(400, ActualWidth), h = (float)Math.Max(300, ActualHeight);
        float ow = W, oh = H;
        var dev = CanvasDevice.GetSharedDevice();
        using var rt = new CanvasRenderTarget(dev, w, h, 192);
        using (var ds = rt.CreateDrawingSession()) Render(ds, w, h, false);
        W = ow; H = oh;
        await rt.SaveAsync(path, CanvasBitmapFileFormat.Png);
    }
}
