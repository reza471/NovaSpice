using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;

namespace NovaSpice.Views;

public sealed class SignalItem : INotifyPropertyChanged
{
    bool plotted;
    SolidColorBrush brush = new();
    public string Name { get; set; } = "";
    public string KindTag { get; set; } = "";
    public bool IsExpr { get; set; }
    public SolidColorBrush Brush { get => brush; set { brush = value; Changed(); } }
    public bool IsPlotted { get => plotted; set { if (plotted != value) { plotted = value; Changed(); } } }
    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class ReadoutRow
{
    public string Name { get; set; } = "";
    public SolidColorBrush Brush { get; set; } = new();
    public string A { get; set; } = "—";
    public string B { get; set; } = "—";
    public string Delta { get; set; } = "—";
    public string Min { get; set; } = "—";
    public string Max { get; set; } = "—";
    public string Avg { get; set; } = "—";
    public string Rms { get; set; } = "—";
    public string PkPk { get; set; } = "—";
}
