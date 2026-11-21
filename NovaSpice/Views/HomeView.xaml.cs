using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NovaSpice.Core;

namespace NovaSpice.Views;

public sealed partial class HomeView : UserControl
{
    public HomeView() { InitializeComponent(); }

    public void Refresh(AppSettings s, string ltspice)
    {
        RecentList.ItemsSource = s.Recent.Where(File.Exists).ToList();
        NoRecent.Visibility = s.Recent.Any(File.Exists) ? Visibility.Collapsed : Visibility.Visible;
        if (ltspice.Length > 0)
        {
            LtBar.Severity = InfoBarSeverity.Success;
            LtBar.Message = $"Ready — {ltspice}";
            LtButton.Content = "Change…";
        }
        else
        {
            LtBar.Severity = InfoBarSeverity.Warning;
            LtBar.Message = "Not found on this PC. You can still open .raw files and explore the demos; set the location to run simulations.";
            LtButton.Content = "Set location…";
        }
    }

    void LtButton_Click(object sender, RoutedEventArgs e) => App.Main!.Navigate("settings");
    async void OpenCircuit_Click(object sender, RoutedEventArgs e) => await App.Main!.OpenDialogAsync(false);
    async void OpenRaw_Click(object sender, RoutedEventArgs e) => await App.Main!.OpenDialogAsync(true);
    void New_Click(object sender, RoutedEventArgs e) => App.Main!.NewSchematic();
    void DemoBuck_Click(object sender, RoutedEventArgs e) => App.Main!.OpenDemo("buck");
    void DemoRc_Click(object sender, RoutedEventArgs e) => App.Main!.OpenDemo("rc");
    void Recent_ItemClick(object sender, ItemClickEventArgs e) { if (e.ClickedItem is string p) App.Main!.OpenPath(p); }
}
