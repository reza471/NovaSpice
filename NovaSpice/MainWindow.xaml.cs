using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NovaSpice.Controls;
using NovaSpice.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;

namespace NovaSpice;

public sealed partial class MainWindow : Window
{
    static readonly string[] CircuitExts = { ".asc", ".net", ".cir", ".sp", ".spi" };

    public AppSettings Settings { get; } = AppSettings.Load();
    string currentFile = "";
    CancellationTokenSource? runCts;
    readonly DispatcherQueueTimerHolder toastTimer;
    readonly List<(string Title, Action Run)> commands = new();

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.Resize(new SizeInt32(1560, 980));
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop();
            RootGrid.Background = new SolidColorBrush(Colors.Transparent);
        }

        toastTimer = new DispatcherQueueTimerHolder(DispatcherQueue, () => Toast.IsOpen = false);
        TitleBar.SizeChanged += (_, _) => UpdateTitleBarRegions();
        RootGrid.ActualThemeChanged += (_, _) => ApplyCanvasTheme();

        WavesV.Hover += s => HoverText.Text = s;
        WavesV.Notify += Notify;
        EditorV.Probe += Probe;
        EditorV.DirtyChanged += UpdateTitle;
        SettingsV.Bind(Settings);

        BuildCommands();
        AddAccelerators();
        ApplyTheme();
        RefreshLtspice();
        Nav.SelectedItem = Nav.MenuItems[0];

        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && args[1] == "--selftest-draw") DispatcherQueue.TryEnqueue(SelfTestDraw);
        if (args.Length > 1 && args[1] == "--selftest-parts") DispatcherQueue.TryEnqueue(SelfTestParts);
        int page = Array.IndexOf(args, "--page");
        if (args.Length > 2 && args[1] == "--demo") DispatcherQueue.TryEnqueue(() =>
        {
            OpenDemo(args[2]);
            if (page > 0 && page + 1 < args.Length) Navigate(args[page + 1]);
        });
        else if (args.Length > 1 && File.Exists(args[1])) DispatcherQueue.TryEnqueue(() => OpenPath(args[1]));
    }

    // ---------------- title bar / theme ----------------
    void UpdateTitleBarRegions()
    {
        double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1;
        CaptionInset.Width = new GridLength(Math.Max(0, AppWindow.TitleBar.RightInset / scale));
        if (Palette.ActualWidth <= 0) return;
        var b = Palette.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, Palette.ActualWidth, Palette.ActualHeight));
        var src = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        src.SetRegionRects(NonClientRegionKind.Passthrough, new[]
        {
            new RectInt32((int)(b.X * scale), (int)(b.Y * scale), (int)(b.Width * scale), (int)(b.Height * scale)),
        });
    }

    public void ApplyTheme()
    {
        RootGrid.RequestedTheme = Settings.Theme switch
        {
            "Light" => ElementTheme.Light,
            "System" => ElementTheme.Default,
            _ => ElementTheme.Dark,
        };
        ApplyCanvasTheme();
    }

    void ApplyCanvasTheme()
    {
        bool dark = RootGrid.ActualTheme == ElementTheme.Dark;
        var t = dark ? PlotTheme.Dark : PlotTheme.Light;
        WavesV.SetTheme(t);
        EditorV.SetTheme(t);
        AppWindow.TitleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = dark ? ColorHelper.FromArgb(30, 255, 255, 255) : ColorHelper.FromArgb(20, 0, 0, 0);
    }

    void ToggleTheme()
    {
        Settings.Theme = RootGrid.ActualTheme == ElementTheme.Dark ? "Light" : "Dark";
        Settings.Save();
        SettingsV.Bind(Settings);
        ApplyTheme();
    }

    public void RefreshLtspice()
    {
        var exe = Ltspice.Find(Settings.LtspicePath);
        HomeV.Refresh(Settings, exe);
        if (WavesV.Raw == null) InfoText.Text = exe.Length > 0 ? "LTspice ready" : "LTspice not found — viewer mode";
    }

    // ---------------- navigation ----------------
    public void Navigate(string tag)
    {
        var item = tag == "settings" ? Nav.SettingsItem
            : Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
        if (item != null) Nav.SelectedItem = item;
    }

    void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string tag = args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "home";
        HomeV.Visibility = tag == "home" ? Visibility.Visible : Visibility.Collapsed;
        EditorV.Visibility = tag == "editor" ? Visibility.Visible : Visibility.Collapsed;
        WavesV.Visibility = tag == "waves" ? Visibility.Visible : Visibility.Collapsed;
        MeasV.Visibility = tag == "meas" ? Visibility.Visible : Visibility.Collapsed;
        SettingsV.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "home") RefreshLtspice();
    }

    // ---------------- notifications ----------------
    public void Notify(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        Toast.Title = severity switch
        {
            InfoBarSeverity.Success => "Done",
            InfoBarSeverity.Warning => "Heads up",
            InfoBarSeverity.Error => "Problem",
            _ => "",
        };
        Toast.Message = message;
        Toast.Severity = severity;
        Toast.IsOpen = true;
        toastTimer.Restart(TimeSpan.FromSeconds(severity == InfoBarSeverity.Error ? 7 : 4));
    }

    void SetStatus(string text, bool busy, InfoBarSeverity sev = InfoBarSeverity.Success)
    {
        StatusText.Text = text;
        Busy.IsActive = busy;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusDot.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        StatusDot.Fill = (Brush)Application.Current.Resources[sev switch
        {
            InfoBarSeverity.Error => "SystemFillColorCriticalBrush",
            InfoBarSeverity.Warning => "SystemFillColorCautionBrush",
            _ => "SystemFillColorSuccessBrush",
        }];
    }

    void UpdateTitle()
    {
        if (EditorV.IsUntitled)
        {
            FileTitle.Text = "Untitled schematic" + (EditorV.IsDirty ? "  •" : "");
            FilePath.Text = "not saved yet";
            FileGlyph.Glyph = "";
            Title = "Untitled — NovaSpice Studio";
            return;
        }
        if (currentFile.Length == 0 && WavesV.Raw == null) { FileTitle.Text = "No file open"; FilePath.Text = ""; return; }
        var f = currentFile.Length > 0 ? currentFile : WavesV.Raw!.Path;
        FileTitle.Text = Path.GetFileName(f) + (EditorV.IsDirty ? "  •" : "");
        FilePath.Text = Path.GetDirectoryName(f) ?? "";
        FileGlyph.Glyph = Path.GetExtension(f).Equals(".raw", StringComparison.OrdinalIgnoreCase) ? "" : "";
        Title = $"{Path.GetFileName(f)} — NovaSpice Studio";
    }

    // ---------------- files ----------------
    public async Task OpenDialogAsync(bool rawOnly)
    {
        var p = rawOnly ? await Views.Pickers.OpenAsync(".raw")
                        : await Views.Pickers.OpenAsync(".asc", ".net", ".cir", ".sp", ".raw");
        if (p != null) OpenPath(p);
    }

    public void OpenPath(string path)
    {
        try
        {
            if (!File.Exists(path)) { Notify($"File not found: {path}", InfoBarSeverity.Error); return; }
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".raw")
            {
                currentFile = "";
                if (LoadResults(path)) Navigate("waves");
            }
            else if (CircuitExts.Contains(ext))
            {
                EditorV.Load(path);
                currentFile = path;
                var raw = Path.ChangeExtension(path, ".raw");
                bool hasResults = File.Exists(raw) && LoadResults(raw, quiet: true);
                if (!hasResults) ClearMeasurements();
                Navigate("editor");
                Notify(hasResults ? $"Opened {Path.GetFileName(path)} with its last results — press F5 to re-simulate."
                                  : $"Opened {Path.GetFileName(path)} — press F5 to simulate.", InfoBarSeverity.Informational);
            }
            else { Notify($"Unsupported file type: {ext}", InfoBarSeverity.Warning); return; }
            Settings.AddRecent(path);
            UpdateTitle();
            RefreshLtspice();
        }
        catch (Exception ex)
        {
            Notify($"Could not open {Path.GetFileName(path)}: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    void ClearMeasurements() { MeasV.Show(null, ""); MeasBadge.Visibility = Visibility.Collapsed; }

    bool LoadResults(string rawPath, bool quiet = false)
    {
        try
        {
            var raw = RawFile.Read(rawPath);
            WavesV.Load(raw);
            var log = Path.ChangeExtension(rawPath, ".log");
            var info = File.Exists(log) ? LogParser.Read(log) : null;
            MeasV.Show(info, File.Exists(log) ? log : "No .log file next to the results");
            MeasBadge.Value = MeasV.Count;
            MeasBadge.Visibility = MeasV.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            InfoText.Text = $"{raw.Plotname} · {raw.Names.Count - 1} signals · {raw.Points:N0} points";
            if (!quiet) Notify($"Loaded {Path.GetFileName(rawPath)} — {raw.Names.Count - 1} signals, {raw.Points:N0} points.", InfoBarSeverity.Success);
            return true;
        }
        catch (Exception ex)
        {
            Notify($"Could not read {Path.GetFileName(rawPath)}: {ex.Message}", InfoBarSeverity.Error);
            return false;
        }
    }

    public void OpenDemo(string name)
    {
        try
        {
            var p = Demo.Ensure(name);
            OpenPath(p);
            if (WavesV.Raw == null) return;
            if (name == "buck")
            {
                WavesV.Probe("I(L1)");
                Navigate("waves");
            }
            else Navigate("editor");
        }
        catch (Exception ex) { Notify($"Demo failed: {ex.Message}", InfoBarSeverity.Error); }
    }

    public void NewSchematic()
    {
        EditorV.NewSchematic();
        currentFile = "";
        ClearMeasurements();
        Navigate("editor");
        UpdateTitle();
        Notify("New schematic — press R, C, L, V, G to place parts, W to draw wires, T for .tran/.ac.", InfoBarSeverity.Informational);
    }

    /// <summary>Draws an RC low-pass through the editor's own click handlers and saves it (developer self-test).</summary>
    void SelfTestDraw()
    {
        NewSchematic();
        var s = EditorV.Canvas;
        s.BeginPlace("voltage"); s.SimulateClick(64, 112);
        s.BeginPlace("res"); s.RotateSelection(); s.SimulateClick(224, 112);
        s.BeginPlace("cap"); s.SimulateClick(272, 112);
        s.SetTool(Controls.SchTool.Wire);
        s.SimulateClick(64, 112); s.SimulateClick(144, 112);   // ends on R1 pin
        s.SimulateClick(224, 112); s.SimulateClick(352, 112);  // passes the C1 pin, open end
        s.SetTool(Controls.SchTool.Ground);
        s.SimulateClick(64, 192); s.SimulateClick(272, 176);
        s.BeginLabel("out"); s.SimulateClick(352, 112);
        s.SetTool(Controls.SchTool.Select);
        s.SimulateClick(176, 64);                                // select R1 (body)
        s.Fit();
        var path = Path.Combine(Demo.DemoDir, "selftest.asc");
        Directory.CreateDirectory(Demo.DemoDir);
        EditorV.SaveAs(path);
        currentFile = path;
        UpdateTitle();
    }

    /// <summary>Places every semiconductor part plus a wired op-amp (developer self-test).</summary>
    void SelfTestParts()
    {
        NewSchematic();
        var s = EditorV.Canvas;
        s.BeginPlace("npn"); s.SimulateClick(160, 64);
        s.BeginPlace("pnp"); s.SimulateClick(336, 64);
        s.BeginPlace("nmos"); s.SimulateClick(512, 64);
        s.BeginPlace("pmos"); s.SimulateClick(688, 64);
        s.BeginPlace("opamp2"); s.SimulateClick(160, 320);           // In+ pin at the click
        s.SetTool(Controls.SchTool.Wire);
        s.SimulateClick(160, 288); s.SimulateClick(96, 288); s.SimulateClick(96, 320); s.SimulateClick(160, 320); // ties In- to In+ area via wires
        s.SetTool(Controls.SchTool.Ground);
        s.SimulateClick(192, 336);                                    // V- pin
        s.BeginLabel("vcc"); s.SimulateClick(192, 272);               // V+ pin
        s.BeginLabel("out"); s.SimulateClick(224, 304);               // OUT pin
        s.SetTool(Controls.SchTool.Select);
        s.Fit();
        var path = Path.Combine(Demo.DemoDir, "selftest_parts.asc");
        Directory.CreateDirectory(Demo.DemoDir);
        EditorV.SaveAs(path);
        currentFile = path;
        UpdateTitle();
    }

    async Task<bool> SaveAsync()
    {
        if (!EditorV.HasDocument) { Notify("Nothing to save.", InfoBarSeverity.Informational); return false; }
        try
        {
            if (EditorV.IsUntitled)
            {
                var p = await Views.Pickers.SaveAsync("circuit", "LTspice schematic", ".asc");
                if (p == null) return false;
                EditorV.SaveAs(p);
                currentFile = p;
                Settings.AddRecent(p);
            }
            else EditorV.Save();
            Notify($"Saved {Path.GetFileName(EditorV.FilePath)}", InfoBarSeverity.Success);
            return true;
        }
        catch (Exception ex) { Notify($"Save failed: {ex.Message}", InfoBarSeverity.Error); return false; }
        finally { UpdateTitle(); }
    }

    void Save() => _ = SaveAsync();

    // ---------------- simulation ----------------
    async Task RunAsync()
    {
        if (runCts != null) { runCts.Cancel(); return; }
        if (currentFile.Length == 0 && !EditorV.IsUntitled) { Notify("Open or draw a circuit first.", InfoBarSeverity.Warning); return; }
        if (EditorV.IsDirty || EditorV.IsUntitled)
        {
            if (!await SaveAsync()) return;
        }
        var exe = Ltspice.Find(Settings.LtspicePath);
        var rawPath = Path.ChangeExtension(currentFile, ".raw");
        if (exe.Length == 0)
        {
            if (File.Exists(rawPath))
            {
                LoadResults(rawPath, quiet: true);
                Navigate("waves");
                Notify("LTspice isn't installed here, so you're seeing the last saved results. Set its location in Settings to simulate.", InfoBarSeverity.Warning);
            }
            else
            {
                Notify("LTspice not found. Set its location in Settings.", InfoBarSeverity.Error);
                Navigate("settings");
            }
            return;
        }

        runCts = new CancellationTokenSource();
        var started = DateTime.Now;
        SetStatus("Simulating…", true);
        RunText.Text = "Stop"; RunGlyph.Glyph = "";
        try
        {
            var r = await Ltspice.RunAsync(exe, currentFile, runCts.Token);
            if (r.Cancelled) { SetStatus("Stopped", false, InfoBarSeverity.Warning); Notify("Simulation stopped.", InfoBarSeverity.Warning); return; }
            var log = Path.ChangeExtension(currentFile, ".log");
            var info = File.Exists(log) ? LogParser.Read(log) : null;
            if (File.Exists(rawPath) && File.GetLastWriteTime(rawPath) >= started.AddSeconds(-2))
            {
                LoadResults(rawPath, quiet: true);
                SetStatus($"Done in {r.Seconds.ToString("0.00", Si.Inv)} s", false,
                    info?.Errors.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
                Notify($"Simulation finished in {r.Seconds.ToString("0.00", Si.Inv)} s" +
                       (MeasV.Count > 0 ? $" · {MeasV.Count} measurements" : ""), InfoBarSeverity.Success);
                Navigate("waves");
            }
            else
            {
                MeasV.Show(info, log);
                SetStatus("Failed", false, InfoBarSeverity.Error);
                var first = info?.Errors.FirstOrDefault() ?? (r.Output.Trim().Length > 0 ? r.Output.Trim() : $"exit code {r.ExitCode}");
                Notify($"Simulation produced no results: {first}", InfoBarSeverity.Error);
                Navigate("meas");
            }
        }
        catch (Exception ex)
        {
            SetStatus("Failed", false, InfoBarSeverity.Error);
            Notify($"Could not start LTspice: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            runCts?.Dispose(); runCts = null;
            RunText.Text = "Run"; RunGlyph.Glyph = "";
        }
    }

    void Probe(string name)
    {
        if (WavesV.Raw == null) { Notify("Run the simulation first, then probe.", InfoBarSeverity.Informational); return; }
        if (WavesV.Probe(name)) { Navigate("waves"); Notify($"Plotted {name}", InfoBarSeverity.Success); }
        else Notify($"{name} isn't in the results.", InfoBarSeverity.Warning);
    }

    // ---------------- commands / palette ----------------
    void BuildCommands()
    {
        commands.Clear();
        commands.Add(("New schematic  (Ctrl+N)", NewSchematic));
        commands.Add(("Run simulation  (F5)", () => _ = RunAsync()));
        commands.Add(("Open file…  (Ctrl+O)", () => _ = OpenDialogAsync(false)));
        commands.Add(("Open waveform (.raw)…", () => _ = OpenDialogAsync(true)));
        commands.Add(("Save  (Ctrl+S)", Save));
        commands.Add(("Go to Home  (Ctrl+1)", () => Navigate("home")));
        commands.Add(("Go to Circuit  (Ctrl+2)", () => Navigate("editor")));
        commands.Add(("Go to Waveforms  (Ctrl+3)", () => Navigate("waves")));
        commands.Add(("Go to Measurements  (Ctrl+4)", () => Navigate("meas")));
        commands.Add(("Settings", () => Navigate("settings")));
        commands.Add(("Toggle dark / light theme", ToggleTheme));
        commands.Add(("Toggle cursors  (C)", () => { Navigate("waves"); WavesV.ToggleCursors(); }));
        commands.Add(("Fit waveforms  (F)", () => { Navigate("waves"); WavesV.Fit(); }));
        commands.Add(("FFT of selected signal", () => { Navigate("waves"); _ = WavesV.ShowFftAsync(); }));
        commands.Add(("Open demo: Buck converter", () => OpenDemo("buck")));
        commands.Add(("Open demo: RC filter sweep", () => OpenDemo("rc")));
        commands.Add(("Open demos folder", () => { Directory.CreateDirectory(Demo.DemoDir); _ = Launcher.LaunchFolderPathAsync(Demo.DemoDir); }));
    }

    static int? Fuzzy(string q, string s)
    {
        if (q.Length == 0) return 0;
        int i = s.IndexOf(q, StringComparison.OrdinalIgnoreCase);
        if (i >= 0) return i;
        int pos = 0, score = 100;
        foreach (var ch in q)
        {
            int j = s.IndexOf(ch.ToString(), pos, StringComparison.OrdinalIgnoreCase);
            if (j < 0) return null;
            score += j - pos; pos = j + 1;
        }
        return score;
    }

    List<(string Title, Action Run)> PaletteItems()
    {
        var items = new List<(string, Action)>(commands);
        foreach (var s in WavesV.SignalNames)
        {
            var name = s;
            items.Add(($"Plot {name}", () => { Navigate("waves"); WavesV.Probe(name); }));
        }
        foreach (var r in Settings.Recent.Where(File.Exists).Take(8))
        {
            var p = r;
            items.Add(($"Open recent: {Path.GetFileName(p)}", () => OpenPath(p)));
        }
        return items;
    }

    void Palette_GotFocus(object sender, RoutedEventArgs e) => FillPalette(Palette.Text);

    void Palette_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) FillPalette(sender.Text);
    }

    void FillPalette(string q)
    {
        Palette.ItemsSource = PaletteItems()
            .Select(c => (c.Title, Score: Fuzzy(q.Trim(), c.Title)))
            .Where(c => c.Score != null).OrderBy(c => c.Score).Take(14).Select(c => c.Title).ToList();
        Palette.IsSuggestionListOpen = true;
    }

    void Palette_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var title = args.ChosenSuggestion as string ?? (sender.ItemsSource as List<string>)?.FirstOrDefault();
        var cmd = PaletteItems().FirstOrDefault(c => c.Title == title);
        sender.Text = "";
        sender.IsSuggestionListOpen = false;
        Nav.Focus(FocusState.Programmatic);
        cmd.Run?.Invoke();
    }

    void AddAccelerators()
    {
        void Add(VirtualKey key, VirtualKeyModifiers mods, Action a)
        {
            var ka = new KeyboardAccelerator { Key = key, Modifiers = mods };
            ka.Invoked += (_, e) => { a(); e.Handled = true; };
            RootGrid.KeyboardAccelerators.Add(ka);
        }
        RootGrid.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        Add(VirtualKey.K, VirtualKeyModifiers.Control, () => Palette.Focus(FocusState.Keyboard));
        Add(VirtualKey.F5, VirtualKeyModifiers.None, () => _ = RunAsync());
        Add(VirtualKey.N, VirtualKeyModifiers.Control, NewSchematic);
        Add(VirtualKey.O, VirtualKeyModifiers.Control, () => _ = OpenDialogAsync(false));
        Add(VirtualKey.S, VirtualKeyModifiers.Control, Save);
        Add(VirtualKey.Number1, VirtualKeyModifiers.Control, () => Navigate("home"));
        Add(VirtualKey.Number2, VirtualKeyModifiers.Control, () => Navigate("editor"));
        Add(VirtualKey.Number3, VirtualKeyModifiers.Control, () => Navigate("waves"));
        Add(VirtualKey.Number4, VirtualKeyModifiers.Control, () => Navigate("meas"));
    }

    // ---------------- toolbar + drag/drop ----------------
    async void Open_Click(object sender, RoutedEventArgs e) => await OpenDialogAsync(false);
    void Save_Click(object sender, RoutedEventArgs e) => Save();
    async void Run_Click(object sender, RoutedEventArgs e) => await RunAsync();

    void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Open in NovaSpice";
        }
    }

    async void Root_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var f = items.FirstOrDefault();
        if (f != null) OpenPath(f.Path);
    }
}

/// <summary>Restartable one-shot timer on the UI dispatcher.</summary>
public sealed class DispatcherQueueTimerHolder
{
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer timer;
    public DispatcherQueueTimerHolder(Microsoft.UI.Dispatching.DispatcherQueue q, Action tick)
    {
        timer = q.CreateTimer();
        timer.IsRepeating = false;
        timer.Tick += (_, _) => tick();
    }
    public void Restart(TimeSpan t) { timer.Stop(); timer.Interval = t; timer.Start(); }
}
