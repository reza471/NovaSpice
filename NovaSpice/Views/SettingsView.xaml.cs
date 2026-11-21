using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NovaSpice.Core;

namespace NovaSpice.Views;

public sealed partial class SettingsView : UserControl
{
    AppSettings? s;
    bool loading;

    public SettingsView() { InitializeComponent(); }

    public void Bind(AppSettings settings)
    {
        s = settings;
        loading = true;
        PathBox.Text = settings.LtspicePath;
        ThemeChoice.SelectedIndex = settings.Theme switch { "Light" => 1, "System" => 2, _ => 0 };
        DataDir.Text = $"Settings and demos: {AppSettings.Dir}";
        loading = false;
        UpdateStatus();
    }

    void UpdateStatus()
    {
        var found = Ltspice.Find(s?.LtspicePath);
        PathStatus.Text = found.Length > 0 ? $"✓ Using {found}" : "✗ LTspice not found — Run is disabled until it is set.";
        PathStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[found.Length > 0 ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush"];
        App.Main?.RefreshLtspice();
    }

    void Commit()
    {
        if (s == null) return;
        s.LtspicePath = PathBox.Text.Trim().Trim('"');
        s.Save();
        UpdateStatus();
    }

    void PathBox_LostFocus(object sender, RoutedEventArgs e) => Commit();

    async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var p = await Pickers.OpenAsync(".exe");
        if (p == null) return;
        PathBox.Text = p;
        Commit();
    }

    void Detect_Click(object sender, RoutedEventArgs e)
    {
        PathBox.Text = Ltspice.Find(null);
        Commit();
        if (PathBox.Text.Length == 0) App.Main?.Notify("LTspice was not found in the usual install folders.", InfoBarSeverity.Warning);
    }

    void ThemeChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading || s == null) return;
        s.Theme = ThemeChoice.SelectedIndex switch { 1 => "Light", 2 => "System", _ => "Dark" };
        s.Save();
        App.Main?.ApplyTheme();
    }
}
