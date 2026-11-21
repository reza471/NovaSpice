using System.Globalization;
using Microsoft.UI.Xaml;

namespace NovaSpice;

public partial class App : Application
{
    public static MainWindow? Main { get; private set; }

    public App()
    {
        // Engineering data must never depend on the Windows locale (decimal separators, digits).
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            e.Handled = true;
            Main?.Notify($"Unexpected error: {e.Exception.Message}", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Main = new MainWindow();
        Main.Activate();
    }
}
