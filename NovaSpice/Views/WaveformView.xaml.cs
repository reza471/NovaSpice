using System.Collections.ObjectModel;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NovaSpice.Controls;
using NovaSpice.Core;
using Windows.UI;

namespace NovaSpice.Views;

public sealed partial class WaveformView : UserControl
{
    public RawData? Raw { get; private set; }
    readonly Dictionary<string, Trace> exprs = new();
    List<List<string>> model = new() { new() };
    readonly List<SignalItem> all = new();
    readonly ObservableCollection<SignalItem> visible = new();
    readonly ObservableCollection<ReadoutRow> rows = new();
    PlotTheme theme = PlotTheme.Dark;

    public event Action<string>? Hover;
    public event Action<string, InfoBarSeverity>? Notify;

    public WaveformView()
    {
        InitializeComponent();
        SignalList.ItemsSource = visible;
        ReadoutList.ItemsSource = rows;
        Plot.CursorsChanged += UpdateReadout;
        Plot.HoverChanged += s => Hover?.Invoke(s);
        Plot.KeyDown += Plot_KeyDown;
        UpdateReadout();
    }

    public IEnumerable<string> SignalNames => all.Select(s => s.Name);

    public void SetTheme(PlotTheme t)
    {
        theme = t;
        Plot.Theme = t;
        foreach (var s in all) s.Brush = new SolidColorBrush(ColorOf(s.Name));
        RebuildSeries(true);
    }

    public void Load(RawData raw)
    {
        Raw = raw;
        exprs.Clear();
        string? def = new[] { "V(out)", "V(vout)", "V(output)", "V(o)" }.Select(raw.Find).FirstOrDefault(n => n != null);
        def ??= raw.Names.Skip(1).Where((n, i) => raw.Kinds[i + 1] == "voltage").FirstOrDefault() ?? raw.Names.Skip(1).FirstOrDefault();
        model = new() { def != null ? new() { def } : new() };
        if (Plot.CursorsOn) Plot.SetCursors(false);
        CursorToggle.IsChecked = false;
        RebuildList();
        RebuildSeries(false);
        var steps = raw.Steps.Count > 1 ? $" · {raw.Steps.Count} steps" : "";
        MetaText.Text = $"{raw.Plotname} · {raw.Points:N0} points{steps}\n{Path.GetFileName(raw.Path)}";
    }

    // ---------------- signals list ----------------
    Color ColorOf(string name)
    {
        int i = Raw?.Names.IndexOf(name) ?? -1;
        if (i < 0) i = 6 + exprs.Keys.ToList().IndexOf(name);
        else i -= 1;
        return theme.Traces[((i % theme.Traces.Length) + theme.Traces.Length) % theme.Traces.Length];
    }

    bool IsPlotted(string name) => model.Any(m => m.Contains(name));

    void RebuildList()
    {
        all.Clear();
        if (Raw != null)
        {
            for (int i = 1; i < Raw.Names.Count; i++)
            {
                var k = Raw.Kinds[i];
                all.Add(new SignalItem
                {
                    Name = Raw.Names[i],
                    KindTag = k == "voltage" ? "V" : k.Contains("current") ? "I" : k,
                    Brush = new SolidColorBrush(ColorOf(Raw.Names[i])),
                    IsPlotted = IsPlotted(Raw.Names[i]),
                });
            }
            foreach (var e in exprs.Keys)
                all.Add(new SignalItem { Name = e, KindTag = "ƒx", IsExpr = true, Brush = new SolidColorBrush(ColorOf(e)), IsPlotted = IsPlotted(e) });
        }
        ApplyFilter();
        SignalCount.Text = all.Count > 0 ? $"{all.Count}" : "";
    }

    void ApplyFilter()
    {
        var q = Search.Text.Trim();
        visible.Clear();
        foreach (var s in all)
            if (q.Length == 0 || s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) visible.Add(s);
    }

    void SyncChecks() { foreach (var s in all) s.IsPlotted = IsPlotted(s.Name); }

    void Search_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    void Signal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.Tag is string name) Toggle(name, cb.IsChecked == true);
    }

    void SignalList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (SignalList.SelectedItem is SignalItem s) Toggle(s.Name, !IsPlotted(s.Name));
    }

    public void Toggle(string name, bool on)
    {
        if (Raw == null) return;
        if (on)
        {
            int target = Raw.IsComplex ? 0 : Math.Clamp(Plot.ActivePane, 0, model.Count - 1);
            if (!model[target].Contains(name)) model[target].Add(name);
        }
        else foreach (var m in model) m.Remove(name);
        SyncChecks();
        bool first = model.Sum(m => m.Count) == 1 && on;
        RebuildSeries(!first);
    }

    /// <summary>Plot a signal by (case-insensitive) name, e.g. from a schematic probe.</summary>
    public bool Probe(string name)
    {
        if (Raw == null) return false;
        var f = Raw.Find(name) ?? exprs.Keys.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (f == null) return false;
        Toggle(f, true);
        return true;
    }

    void ExprBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || Raw == null) return;
        var text = ExprBox.Text.Trim();
        if (text.Length == 0) return;
        try
        {
            exprs[text] = Expr.Evaluate(text, Raw, exprs);
            ExprBox.Text = "";
            RebuildList();
            Toggle(text, true);
            Notify?.Invoke($"Added expression {text}", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Notify?.Invoke($"Expression error: {ex.Message}", InfoBarSeverity.Error);
        }
        e.Handled = true;
    }

    // ---------------- series ----------------
    Trace GetTrace(string name) => Raw!.Traces.TryGetValue(name, out var t) ? t : exprs[name];

    PlotSeries MakeSeries(string name, (int Start, int End) sl, int k, int nsteps, string role)
    {
        var tr = GetTrace(name);
        int len = sl.End - sl.Start;
        var x = Raw!.Axis.AsSpan(sl.Start, len).ToArray();
        var y = new double[len];
        string unit = Si.Unit(tr.Kind);
        if (tr.Im != null)
        {
            for (int i = 0; i < len; i++)
            {
                double re = tr.Re[sl.Start + i], im = tr.Im[sl.Start + i];
                y[i] = role == "phase" ? Math.Atan2(im, re) * 180 / Math.PI : 20 * Math.Log10(Math.Max(Math.Sqrt(re * re + im * im), 1e-300));
            }
            if (role == "phase") { y = Dsp.Unwrap(y); unit = "°"; } else unit = "dB";
        }
        else Array.Copy(tr.Re, sl.Start, y, 0, len);
        return new PlotSeries
        {
            Key = name,
            Label = nsteps > 1 ? $"{name} · {Raw.StepLabel(k)}" : name,
            X = x, Y = y, Unit = unit,
            Color = PlotTheme.StepColor(ColorOf(name), k, nsteps),
        };
    }

    void RebuildSeries(bool keepX)
    {
        Plot.Panes.Clear();
        if (Raw == null) { Plot.Invalidate(); UpdateReadout(); return; }
        var steps = Raw.StepRanges();
        if (Raw.IsComplex)
        {
            var mag = new PlotPane { Title = "Magnitude" };
            var ph = new PlotPane { Title = "Phase" };
            foreach (var n in model[0])
                for (int k = 0; k < steps.Count; k++)
                {
                    mag.Series.Add(MakeSeries(n, steps[k], k, steps.Count, "mag"));
                    ph.Series.Add(MakeSeries(n, steps[k], k, steps.Count, "phase"));
                }
            Plot.Panes.Add(mag); Plot.Panes.Add(ph);
        }
        else
            foreach (var m in model)
            {
                var p = new PlotPane();
                foreach (var n in m)
                    for (int k = 0; k < steps.Count; k++) p.Series.Add(MakeSeries(n, steps[k], k, steps.Count, "main"));
                Plot.Panes.Add(p);
            }
        Plot.LogX = Raw.IsLogX;
        Plot.XUnit = Si.Unit(Raw.AxisKind);
        Plot.XTitle = Raw.AxisName;
        Plot.ShowActive = !Raw.IsComplex;
        Plot.ActivePane = Math.Clamp(Plot.ActivePane, 0, Plot.Panes.Count - 1);
        if (!keepX) Plot.FitAll(); else Plot.Invalidate();
        UpdateReadout();
    }

    // ---------------- readout ----------------
    void UpdateReadout()
    {
        rows.Clear();
        if (Raw == null) { CursorInfo.Text = ""; return; }
        string xu = Si.Unit(Raw.AxisKind);
        bool cur = Plot.CursorsOn;
        double a = Plot.CurA, b = Plot.CurB;
        if (cur)
        {
            double d = b - a;
            CursorInfo.Text = $"A {Si.Fmt(a, xu)}   B {Si.Fmt(b, xu)}   Δ {Si.Fmt(d, xu)}" +
                              (d != 0 && xu == "s" ? $"   1/Δ {Si.Fmt(1 / Math.Abs(d), "Hz")}" : "");
        }
        else CursorInfo.Text = "drag = zoom · wheel = zoom X · Ctrl+wheel = zoom Y · right-drag = pan · double-click = fit";

        foreach (var p in Plot.Panes)
            foreach (var s in p.Series)
            {
                if (s.X.Length == 0) continue;
                double lo = cur ? Math.Min(a, b) : s.X[0], hi = cur ? Math.Max(a, b) : s.X[^1];
                var (mn, mx, avg, rms) = Dsp.Stats(s.X, s.Y, lo, hi);
                bool timeStats = Raw.IsTime && s.Unit is not ("dB" or "°");
                var r = new ReadoutRow
                {
                    Name = s.Label + (p.Title == "Phase" ? "  ∠" : ""),
                    Brush = new SolidColorBrush(s.Color),
                    Min = Si.Fmt(mn, s.Unit), Max = Si.Fmt(mx, s.Unit), PkPk = Si.Fmt(mx - mn, s.Unit),
                    Avg = timeStats ? Si.Fmt(avg, s.Unit) : "—",
                    Rms = timeStats ? Si.Fmt(rms, s.Unit) : "—",
                };
                if (cur)
                {
                    double va = Dsp.Interp(s.X, s.Y, a), vb = Dsp.Interp(s.X, s.Y, b);
                    r.A = Si.Fmt(va, s.Unit); r.B = Si.Fmt(vb, s.Unit); r.Delta = Si.Fmt(vb - va, s.Unit);
                }
                rows.Add(r);
            }
    }

    // ---------------- toolbar ----------------
    void AddPane_Click(object sender, RoutedEventArgs e)
    {
        if (Raw == null) return;
        if (Raw.IsComplex) { Notify?.Invoke("AC results use a fixed magnitude/phase (Bode) layout.", InfoBarSeverity.Informational); return; }
        model.Add(new());
        Plot.ActivePane = model.Count - 1;
        RebuildSeries(true);
        Notify?.Invoke("New pane added — tick signals to plot them there (click a pane to make it active).", InfoBarSeverity.Informational);
    }

    void RemovePane_Click(object sender, RoutedEventArgs e)
    {
        if (Raw == null || Raw.IsComplex) return;
        int i = Math.Clamp(Plot.ActivePane, 0, model.Count - 1);
        if (model.Count == 1) model[0].Clear(); else model.RemoveAt(i);
        Plot.ActivePane = Math.Max(0, i - 1);
        SyncChecks();
        RebuildSeries(true);
    }

    void Cursors_Click(object sender, RoutedEventArgs e) => Plot.SetCursors(CursorToggle.IsChecked == true);

    public void ToggleCursors()
    {
        CursorToggle.IsChecked = !(CursorToggle.IsChecked == true);
        Plot.SetCursors(CursorToggle.IsChecked == true);
    }

    public void Fit() => Plot.FitAll();
    void Fit_Click(object sender, RoutedEventArgs e) => Plot.FitAll();

    void Plot_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.F) { Plot.FitAll(); e.Handled = true; }
        else if (e.Key == Windows.System.VirtualKey.C) { ToggleCursors(); e.Handled = true; }
    }

    async void Fft_Click(object sender, RoutedEventArgs e) => await ShowFftAsync();

    public async Task ShowFftAsync()
    {
        if (Raw == null) return;
        if (!Raw.IsTime) { Notify?.Invoke("FFT needs a transient (time-domain) result.", InfoBarSeverity.Warning); return; }
        string? name = (SignalList.SelectedItem as SignalItem)?.Name ?? model.SelectMany(m => m).FirstOrDefault();
        if (name == null) { Notify?.Invoke("Select or plot a signal first.", InfoBarSeverity.Warning); return; }
        var sl = Raw.StepRanges()[0];
        var x = Raw.Axis.AsSpan(sl.Start, sl.End - sl.Start).ToArray();
        var tr = GetTrace(name);
        var y = tr.Re.AsSpan(sl.Start, sl.End - sl.Start).ToArray();
        double lo = Plot.CursorsOn ? Math.Min(Plot.CurA, Plot.CurB) : x[0];
        double hi = Plot.CursorsOn ? Math.Max(Plot.CurA, Plot.CurB) : x[^1];
        var sp = Dsp.Analyze(x, y, lo, hi);
        string unit = Si.Unit(tr.Kind);

        var canvas = new WaveformCanvas { Theme = theme, LogX = true, XUnit = "Hz", XTitle = "frequency", ShowActive = false, Height = 420 };
        canvas.Panes.Add(new PlotPane { Title = $"|{name}|", Series = { new PlotSeries { Label = name, X = sp.Freq, Y = sp.Db, Unit = "dB", Color = ColorOf(name) } } });
        canvas.FitAll();
        var info = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12,
            Text = $"Window {Si.Fmt(lo, "s")} … {Si.Fmt(hi, "s")}  ·  {sp.N:N0}-point Hann FFT\n" +
                   $"Fundamental {Si.Fmt(sp.Fundamental, "Hz")} @ {Si.Fmt(sp.FundamentalAmp, unit)} peak  ·  DC {Si.Fmt(sp.Dc, unit)}  ·  THD {sp.ThdPercent.ToString("0.###", Si.Inv)} % (2nd–10th)",
        };
        var panel = new StackPanel { Spacing = 12, Width = 980 };
        panel.Children.Add(new Border { CornerRadius = new CornerRadius(10), Child = canvas });
        panel.Children.Add(info);
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = $"Spectrum · {name}", Content = panel, CloseButtonText = "Close",
            RequestedTheme = ActualTheme,
        };
        dlg.Resources["ContentDialogMaxWidth"] = 1100.0;
        await dlg.ShowAsync();
    }

    async void Png_Click(object sender, RoutedEventArgs e)
    {
        if (Raw == null) return;
        var path = await Pickers.SaveAsync(Path.GetFileNameWithoutExtension(Raw.Path) + "_plot", "PNG image", ".png");
        if (path == null) return;
        await Plot.SavePngAsync(path);
        Notify?.Invoke($"Saved {Path.GetFileName(path)}", InfoBarSeverity.Success);
    }

    async void Csv_Click(object sender, RoutedEventArgs e)
    {
        if (Raw == null) return;
        var path = await Pickers.SaveAsync(Path.GetFileNameWithoutExtension(Raw.Path) + "_data", "CSV", ".csv");
        if (path == null) return;
        var series = Plot.Panes.SelectMany(p => p.Series.Select(s => (p, s))).ToList();
        var sb = new StringBuilder();
        int nsteps = Raw.StepRanges().Count;
        var keys = series.Select(t => (t.s.Key, t.s.Unit)).Distinct().ToList();
        sb.Append(nsteps > 1 ? "step," : "").Append(Raw.AxisName);
        foreach (var (k, u) in keys) sb.Append(',').Append(k).Append(u is "dB" or "°" ? $" [{u}]" : "");
        sb.AppendLine();
        var steps = Raw.StepRanges();
        for (int st = 0; st < steps.Count; st++)
        {
            var cols = keys.Select(k => series.Where(t => t.s.Key == k.Key && t.s.Unit == k.Unit).Skip(st).First().s.Y).ToList();
            int len = steps[st].End - steps[st].Start;
            for (int i = 0; i < len; i++)
            {
                if (nsteps > 1) sb.Append(st + 1).Append(',');
                sb.Append(Raw.Axis[steps[st].Start + i].ToString("G10", Si.Inv));
                foreach (var c in cols) sb.Append(',').Append(c[i].ToString("G9", Si.Inv));
                sb.AppendLine();
            }
        }
        await File.WriteAllTextAsync(path, sb.ToString());
        Notify?.Invoke($"Saved {Path.GetFileName(path)}", InfoBarSeverity.Success);
    }
}
