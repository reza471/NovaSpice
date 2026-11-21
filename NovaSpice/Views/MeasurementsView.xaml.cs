using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NovaSpice.Core;
using Windows.ApplicationModel.DataTransfer;

namespace NovaSpice.Views;

public sealed partial class MeasurementsView : UserControl
{
    LogInfo? info;
    public int Count => info?.Measurements.Count ?? 0;

    public MeasurementsView() { InitializeComponent(); }

    public void Show(LogInfo? log, string path)
    {
        info = log;
        Source.Text = path;
        List.ItemsSource = log?.Measurements;
        NoMeas.Visibility = (log?.Measurements.Count ?? 0) > 0 ? Visibility.Collapsed : Visibility.Visible;
        LogText.Text = log?.Text ?? "";
        int e = log?.Errors.Count ?? 0, w = log?.Warnings.Count ?? 0;
        ErrBadge.Visibility = e > 0 ? Visibility.Visible : Visibility.Collapsed;
        ErrText.Text = $"{e} error{(e == 1 ? "" : "s")}";
        WarnBadge.Visibility = w > 0 ? Visibility.Visible : Visibility.Collapsed;
        WarnText.Text = $"{w} warning{(w == 1 ? "" : "s")}";
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (info == null) return;
        var lines = new List<string> { "Name\tStep\tExpression\tValue\tDetails" };
        lines.AddRange(info.Measurements.Select(m => $"{m.Name}\t{m.StepText}\t{m.Expr}\t{m.Value}\t{m.Details}"));
        var dp = new DataPackage();
        dp.SetText(string.Join("\r\n", lines));
        Clipboard.SetContent(dp);
        App.Main!.Notify("Measurements copied — paste into Excel or a report.", InfoBarSeverity.Success);
    }
}
