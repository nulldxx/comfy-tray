using System.Reflection;
using System.Windows;

namespace SampleTrayApp;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by WPF XAML framework")]
internal sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
        Dispatcher.BeginInvoke(() =>
            MessageBox.Show(
                $"Sample Tray App v{version}\n\nA sample Windows system tray application.",
                "About Sample Tray App",
                MessageBoxButton.OK,
                MessageBoxImage.Information));
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    private void TrayIcon_LeftClick(object sender, RoutedEventArgs e)
    {
        if (TrayIcon?.ContextMenu != null)
        {
            TrayIcon.ContextMenu.IsOpen = true;
        }
    }
}
