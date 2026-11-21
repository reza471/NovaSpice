using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using NovaSpice.Controls;
using NovaSpice.Core;

namespace NovaSpice.Views;

public sealed partial class EditorView : UserControl
{
    public string FilePath { get; private set; } = "";
    public bool IsDirty { get; private set; }
    public bool HasDocument { get; private set; }
    public bool IsUntitled => HasDocument && FilePath.Length == 0;
    public bool IsSchematic { get; private set; }
    public SchematicCanvas Canvas => Sch;
    Encoding encoding = Encoding.Latin1;
    bool suppressText;
    bool dialogOpen;
    readonly DispatcherQueueTimerHolder reparse;

    public event Action<string>? Probe;
    public event Action? DirtyChanged;

    public EditorView()
    {
        InitializeComponent();
        reparse = new DispatcherQueueTimerHolder(DispatcherQueue, ReparseText);
        Sch.Probe += p => Probe?.Invoke(p);
        Sch.Changed += OnCanvasChanged;
        Sch.ToolChanged += (t, type) => SyncToolButtons(t, type);
        Sch.EditRequested += async o => await EditAsync(o);
        Sch.LabelRequested += async () => await AskLabelAsync();
        Sch.TextRequested += async () => await AskTextAsync();
        Sch.LibraryRequested += async () => await PickFromLibraryAsync();
        SyncToolButtons(SchTool.Select, "");
    }

    public void SetTheme(PlotTheme t) { Sch.Theme = t; Sch.Invalidate(); }

    public static (string Text, Encoding Enc) ReadText(string path)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length > 1 && (b[1] == 0 || (b[0] == 0xFF && b[1] == 0xFE)))
            return (Encoding.Unicode.GetString(b).TrimStart('﻿'), Encoding.Unicode);
        if (b.Length > 2 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
            return (Encoding.UTF8.GetString(b, 3, b.Length - 3), new UTF8Encoding(true));
        return (Encoding.Latin1.GetString(b), Encoding.Latin1);
    }

    // ---------------- documents ----------------
    public void Load(string path)
    {
        var (text, enc) = ReadText(path);
        FilePath = path; encoding = enc;
        bool asc = Path.GetExtension(path).Equals(".asc", StringComparison.OrdinalIgnoreCase);
        Show(asc, text, asc ? AscSchematic.Parse(text) : null);
    }

    public void NewSchematic()
    {
        FilePath = ""; encoding = Encoding.Latin1;
        var doc = new AscSchematic();
        doc.Texts.Add(new AscText { X = 32, Y = 288, Text = ".tran 1m", Directive = true });
        Show(true, doc.Serialize(), doc);
        SetDirty(true);
        Sch.Focus(FocusState.Programmatic);
    }

    void Show(bool asc, string text, AscSchematic? doc)
    {
        IsSchematic = asc;
        HasDocument = true;
        suppressText = true;
        Code.Text = text;
        suppressText = false;
        SetDirty(false);
        Toolbar.Visibility = Visibility.Visible;
        SetToolbarEnabled(asc);
        SchPanel.Visibility = asc ? Visibility.Visible : Visibility.Collapsed;
        SchCol.Width = asc ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        TextTitle.Text = asc ? "Schematic source (.asc) — edits sync to the drawing" : "Netlist";
        SourceToggle.IsChecked = !asc;
        SourceToggle.IsEnabled = asc;
        ApplySourceVisibility();
        Sch.Load(doc);
        Body.Visibility = Visibility.Visible;
        Empty.Visibility = Visibility.Collapsed;
    }

    void SetToolbarEnabled(bool on)
    {
        foreach (var child in ToolStrip.Children)
            if (child is Control c) c.IsEnabled = on;
    }

    public void Save() => SaveAs(FilePath);

    public void SaveAs(string path)
    {
        if (path.Length == 0) return;
        var text = Code.Text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
        File.WriteAllBytes(path, encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray());
        FilePath = path;
        SetDirty(false);
    }

    void SetDirty(bool d)
    {
        IsDirty = d;
        Dirty.Visibility = d ? Visibility.Visible : Visibility.Collapsed;
        DirtyChanged?.Invoke();
    }

    // ---------------- canvas <-> text sync ----------------
    void OnCanvasChanged()
    {
        if (Sch.Document == null) return;
        suppressText = true;
        Code.Text = Sch.Document.Serialize();
        suppressText = false;
        if (!IsDirty) SetDirty(true);
    }

    void Code_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (suppressText) return;
        if (!IsDirty) SetDirty(true);
        if (IsSchematic) reparse.Restart(TimeSpan.FromMilliseconds(600));
    }

    void ReparseText()
    {
        if (!IsSchematic) return;
        Sch.Replace(AscSchematic.Parse(Code.Text));
    }

    void SourceToggle_Click(object sender, RoutedEventArgs e) => ApplySourceVisibility();

    void ApplySourceVisibility()
    {
        bool show = SourceToggle.IsChecked == true || !IsSchematic;
        TextPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        TextCol.Width = show ? new GridLength(IsSchematic ? 0.7 : 1, GridUnitType.Star) : new GridLength(0);
    }

    // ---------------- tools ----------------
    void SyncToolButtons(SchTool t, string type)
    {
        string key = t switch
        {
            SchTool.Place => type,
            SchTool.Wire => "wire",
            SchTool.Ground => "ground",
            SchTool.Label => "label",
            SchTool.Text => "text",
            _ => "select",
        };
        foreach (var tb in new[] { TSelect, TWire, TRes, TCap, TInd, TDiode, TVolt, TCurr, TNpn, TPnp, TNmos, TPmos, TOpamp, TGround, TLabel, TText })
            tb.IsChecked = (string)tb.Tag == key;
    }

    async void Tool_Click(object sender, RoutedEventArgs e)
    {
        var tag = (string)((FrameworkElement)sender).Tag;
        switch (tag)
        {
            case "select": Sch.SetTool(SchTool.Select); break;
            case "wire": Sch.SetTool(SchTool.Wire); break;
            case "ground": Sch.SetTool(SchTool.Ground); break;
            case "label": await AskLabelAsync(); break;
            case "text": await AskTextAsync(); break;
            default: Sch.BeginPlace(tag); break;
        }
        SyncToolButtons(Sch.Tool, Sch.PlaceType);
        Sch.Focus(FocusState.Programmatic);
    }

    void Rotate_Click(object sender, RoutedEventArgs e) { Sch.RotateSelection(); Sch.Focus(FocusState.Programmatic); }
    void Mirror_Click(object sender, RoutedEventArgs e) { Sch.MirrorSelection(); Sch.Focus(FocusState.Programmatic); }
    void Delete_Click(object sender, RoutedEventArgs e) { Sch.DeleteSelection(); Sch.Focus(FocusState.Programmatic); }
    void Undo_Click(object sender, RoutedEventArgs e) { Sch.Undo(); Sch.Focus(FocusState.Programmatic); }
    void Redo_Click(object sender, RoutedEventArgs e) { Sch.Redo(); Sch.Focus(FocusState.Programmatic); }
    void Fit_Click(object sender, RoutedEventArgs e) { Sch.Fit(); Sch.Focus(FocusState.Programmatic); }
    void New_Click(object sender, RoutedEventArgs e) => App.Main!.NewSchematic();
    async void Library_Click(object sender, RoutedEventArgs e) => await PickFromLibraryAsync();

    async Task PickFromLibraryAsync()
    {
        if (!SymbolLibrary.Available)
        {
            var msg = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, MaxWidth = 460,
                Text = "The LTspice symbol library was not found. Install LTspice (it creates its lib/sym folder in your profile) and every part in it " +
                       "— op-amps, regulators, MOSFETs, logic, transformers — becomes available here with its exact pins.",
            };
            await ShowDialogAsync("LTspice library", msg, "OK");
            return;
        }
        var all = SymbolLibrary.Index();
        var search = new TextBox { PlaceholderText = $"Search {all.Count:N0} parts — e.g. LT1001, npn, 7805, opamp" };
        var list = new ListView { Height = 380, SelectionMode = ListViewSelectionMode.Single };
        void Filter()
        {
            var q = search.Text.Trim();
            list.ItemsSource = all.Where(e => q.Length == 0 || e.RelPath.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Take(400).Select(e => e.RelPath.Replace(".asy", "")).ToList();
        }
        search.TextChanged += (_, _) => Filter();
        Filter();
        var panel = new StackPanel { Spacing = 10, Width = 520 };
        panel.Children.Add(search);
        panel.Children.Add(list);
        panel.Children.Add(new TextBlock { Text = SymbolLibrary.Root, FontSize = 11, Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"] });
        if (!await ShowDialogAsync("Place a part from the LTspice library", panel, "Place")) return;
        if (list.SelectedItem is not string rel) return;
        var entry = all.First(e => e.RelPath.Replace(".asy", "") == rel);
        var attrs = SymbolLibrary.Attributes(entry);
        var prefix = attrs.GetValueOrDefault("Prefix", "X");
        var inst = prefix.Equals("X", StringComparison.OrdinalIgnoreCase) ? "U" : prefix.ToUpperInvariant()[..1];
        var value = attrs.GetValueOrDefault("Value", "");
        if (value.Length == 0) value = attrs.GetValueOrDefault("SpiceModel", "");
        Sch.BeginPlace(entry.Type, entry.RawType, inst, value);
        SyncToolButtons(Sch.Tool, Sch.PlaceType);
    }

    // ---------------- dialogs ----------------
    static readonly Dictionary<string, string[]> Presets = new()
    {
        ["res"] = new[] { "1k", "10k", "100k", "4.7k", "100", "1Meg" },
        ["cap"] = new[] { "100n", "1u", "10u", "100u", "10p" },
        ["polcap"] = new[] { "10u", "100u", "470u" },
        ["ind"] = new[] { "1u", "10u", "100u", "1m" },
        ["diode"] = new[] { "D", "1N4148", "1N5819", "1N4007" },
        ["voltage"] = new[] { "5", "12", "AC 1", "PULSE(0 5 0 1n 1n 0.5u 1u)", "SINE(0 1 1k)", "PWL(0 0 1m 5)" },
        ["current"] = new[] { "1m", "PULSE(0 1m 0 1n 1n 0.5m 1m)", "SINE(0 1m 1k)" },
        ["npn"] = new[] { "2N3904", "2N2222", "BC547B", "2N4401", "2N3055" },
        ["pnp"] = new[] { "2N3906", "2N2907", "BC557B", "2N4403" },
        ["nmos"] = new[] { "2N7002", "BSS123", "IRF530", "Si4410DY" },
        ["pmos"] = new[] { "BSS84", "FDS4435", "IRF9530", "Si4425DY" },
        ["opamp2"] = new[] { SymbolShape.OpAmpModel },
    };

    async Task<bool> ShowDialogAsync(string title, UIElement content, string primary = "OK")
    {
        if (dialogOpen) return false;
        dialogOpen = true;
        try
        {
            var dlg = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = title, Content = content,
                PrimaryButtonText = primary, CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary, RequestedTheme = ActualTheme,
            };
            return await dlg.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            dialogOpen = false;
            Sch.Focus(FocusState.Programmatic);
        }
    }

    static TextBox Field(string header, string text, bool mono = true) => new()
    {
        Header = header, Text = text, MinWidth = 380,
        FontFamily = mono ? new FontFamily("Cascadia Mono, Consolas") : new FontFamily("Segoe UI"),
    };

    async Task EditAsync(object o)
    {
        var doc = Sch.Document;
        if (doc == null) return;
        switch (o)
        {
            case AscSymbol s:
            {
                var name = Field("Reference", s.Inst);
                var value = new ComboBox
                {
                    Header = "Value", IsEditable = true, MinWidth = 380, Text = s.Value,
                    ItemsSource = Presets.GetValueOrDefault(s.Type, Array.Empty<string>()),
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                };
                var extra = Field("Extra SPICE parameters (optional, e.g. Rser=10m)", s.Attrs.GetValueOrDefault("SpiceLine", ""));
                var panel = new StackPanel { Spacing = 12 };
                panel.Children.Add(name); panel.Children.Add(value); panel.Children.Add(extra);
                if (!await ShowDialogAsync($"{SymbolShape.PartFor(s.Type)?.Name ?? s.RawType} properties", panel)) return;
                Sch.PushUndo();
                if (name.Text.Trim().Length > 0) s.Inst = name.Text.Trim();
                s.Value = (value.Text ?? value.SelectedItem as string ?? s.Value).Trim();
                if (extra.Text.Trim().Length > 0) s.Attrs["SpiceLine"] = extra.Text.Trim(); else s.Attrs.Remove("SpiceLine");
                Sch.Commit();
                break;
            }
            case AscFlag f:
            {
                var name = Field("Net name", f.Name);
                if (!await ShowDialogAsync("Rename net label", name)) return;
                var n = name.Text.Trim().Replace(' ', '_');
                if (n.Length == 0) return;
                Sch.PushUndo(); f.Name = n; Sch.Commit();
                break;
            }
            case AscText t:
            {
                var (box, kind) = TextEditor(t.Text, t.Directive);
                var panel = new StackPanel { Spacing = 12 };
                panel.Children.Add(kind); panel.Children.Add(box);
                if (!await ShowDialogAsync("Edit text", panel)) return;
                Sch.PushUndo();
                t.Text = box.Text.Replace("\r\n", "\n").Replace('\r', '\n');
                t.Directive = kind.SelectedIndex == 0;
                Sch.Commit();
                break;
            }
        }
    }

    static (TextBox Box, RadioButtons Kind) TextEditor(string text, bool directive)
    {
        var box = new TextBox
        {
            Text = text, AcceptsReturn = true, MinWidth = 460, MinHeight = 110, TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            PlaceholderText = ".tran 1m\n.ac dec 100 1 1Meg\n.step param R list 1k 10k\n.meas TRAN vavg AVG V(out)",
        };
        var kind = new RadioButtons { MaxColumns = 2, SelectedIndex = directive ? 0 : 1 };
        kind.Items.Add("SPICE directive");
        kind.Items.Add("Comment");
        return (box, kind);
    }

    async Task AskLabelAsync()
    {
        var name = Field("Net name (e.g. out, vin, fb)", "out");
        name.SelectAll();
        if (!await ShowDialogAsync("Place a net label", name, "Place")) { SyncToolButtons(Sch.Tool, Sch.PlaceType); return; }
        var n = name.Text.Trim().Replace(' ', '_');
        if (n.Length > 0) Sch.BeginLabel(n);
        SyncToolButtons(Sch.Tool, Sch.PlaceType);
    }

    async Task AskTextAsync()
    {
        var (box, kind) = TextEditor(".tran 1m", true);
        var templates = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var tpl in new[] { ".tran 1m", ".ac dec 100 1 1Meg", ".op", ".dc V1 0 5 0.1", ".step param R list 1k 10k 100k" })
        {
            var b = new Button { Content = tpl.Split(' ')[0], FontFamily = new FontFamily("Cascadia Mono, Consolas"), Padding = new Thickness(8, 3, 8, 3) };
            b.Click += (_, _) => { box.Text = tpl; kind.SelectedIndex = 0; };
            templates.Children.Add(b);
        }
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(templates); panel.Children.Add(kind); panel.Children.Add(box);
        if (!await ShowDialogAsync("Add a SPICE directive or comment", panel, "Place"))
        {
            SyncToolButtons(Sch.Tool, Sch.PlaceType);
            return;
        }
        if (box.Text.Trim().Length > 0)
            Sch.BeginText(box.Text.Replace("\r\n", "\n").Replace('\r', '\n'), kind.SelectedIndex == 0);
        SyncToolButtons(Sch.Tool, Sch.PlaceType);
    }
}
