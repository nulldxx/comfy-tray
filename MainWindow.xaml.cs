using System;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;

namespace SampleTrayApp;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by WPF XAML framework")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:TypesThatOwnDisposableFieldsShouldBeDisposable", Justification = "WPF Window lifetime is framework-managed; the server is disposed on Exit.")]
internal sealed partial class MainWindow : Window
{
    private static readonly BitmapImage RedIcon =
        new(new Uri("pack://application:,,,/icons/red.ico"));

    private static readonly BitmapImage GreenIcon =
        new(new Uri("pack://application:,,,/icons/green.ico"));

    private readonly ComfyConfig _config;
    private readonly ComfyServerManager _server = new();
    private LogWindow? _logWindow;

    public MainWindow()
    {
        InitializeComponent();

        _config = ComfyConfig.Load(out var loadError);
        _server.StateChanged += OnServerStateChanged;
        UpdateForState(_server.State);

        if (loadError != null)
        {
            Dispatcher.BeginInvoke(() => MessageBox.Show(
                loadError, "ComfyUI Tray", MessageBoxButton.OK, MessageBoxImage.Warning));
        }
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _server.Start(_config);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "Could not start ComfyUI",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _server.Stop();

    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        if (_logWindow != null)
        {
            _logWindow.Activate();
            return;
        }

        _logWindow = new LogWindow(_server);
        _logWindow.Closed += (_, _) => _logWindow = null;
        _logWindow.Show();
    }

    private void OnServerStateChanged(object? sender, ComfyState state) =>
        Dispatcher.BeginInvoke(() => UpdateForState(state));

    private void UpdateForState(ComfyState state)
    {
        var running = state == ComfyState.Running;
        TrayIcon.IconSource = running ? GreenIcon : RedIcon;
        TrayIcon.ToolTipText = running ? "ComfyUI: Running" : "ComfyUI: Stopped";
        StartItem.IsEnabled = !running;
        StopItem.IsEnabled = running;
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
        Dispatcher.BeginInvoke(() =>
            MessageBox.Show(
                $"ComfyUI Tray v{version}\n\n" +
                "Runs the ComfyUI server headless in the background.\n\n" +
                $"Config: {ComfyConfig.ConfigPath}",
                "About ComfyUI Tray",
                MessageBoxButton.OK,
                MessageBoxImage.Information));
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        _server.Stop();
        _server.Dispose();
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
