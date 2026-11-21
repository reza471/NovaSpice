using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NovaSpice.Controls;
using NovaSpice.Core;

namespace NovaSpice.Views;

public sealed partial class EditorView : UserControl
{
    public string FilePath { get; private set; } = "";
    public bool IsDirty { get; private set; }
    Encoding encoding = Encoding.Latin1;
    bool loading;

    public event Action<string>? Probe;
    public event Action? DirtyChanged;

    public EditorView()
    {
        InitializeComponent();
        Sch.Probe += p => Probe?.Invoke(p);
        Body.Visibility = Visibility.Collapsed;
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

    public void Load(string path)
    {
        var (text, enc) = ReadText(path);
        loading = true;
        FilePath = path; encoding = enc;
        Code.Text = text;
        loading = false;
        SetDirty(false);
        bool asc = Path.GetExtension(path).Equals(".asc", StringComparison.OrdinalIgnoreCase);
        SchCol.Width = asc ? new GridLength(3, GridUnitType.Star) : new GridLength(0);
        SchPanel.Visibility = asc ? Visibility.Visible : Visibility.Collapsed;
        TextTitle.Text = asc ? "Schematic source (.asc)" : "Netlist";
        Sch.Load(asc ? AscSchematic.Parse(text) : null);
        Body.Visibility = Visibility.Visible;
        Empty.Visibility = Visibility.Collapsed;
    }

    public void Save()
    {
        if (FilePath.Length == 0) return;
        var text = Code.Text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
        File.WriteAllBytes(FilePath, bytes);
        SetDirty(false);
        if (Path.GetExtension(FilePath).Equals(".asc", StringComparison.OrdinalIgnoreCase))
            Sch.Load(AscSchematic.Parse(text));
    }

    void SetDirty(bool d)
    {
        IsDirty = d;
        Dirty.Visibility = d ? Visibility.Visible : Visibility.Collapsed;
        DirtyChanged?.Invoke();
    }

    void Code_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!loading && !IsDirty) SetDirty(true);
    }
}
