using System;
using System.Text;
using System.Windows;

namespace ComfyTray;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated from MainWindow")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:TypesThatOwnDisposableFieldsShouldBeDisposable", Justification = "The server is owned by MainWindow, not this view; only referenced here.")]
internal sealed partial class LogWindow : Window
{
    private readonly ComfyServerManager _server;

    public LogWindow(ComfyServerManager server)
    {
        _server = server;
        InitializeComponent();

        var sb = new StringBuilder();
        foreach (var line in _server.GetLogSnapshot())
        {
            sb.AppendLine(line);
        }

        LogBox.Text = sb.ToString();
        LogBox.ScrollToEnd();

        _server.LogReceived += OnLogReceived;
        Closed += OnClosed;
    }

    private void OnLogReceived(object? sender, string line)
    {
        Dispatcher.BeginInvoke(() =>
        {
            LogBox.AppendText(line + Environment.NewLine);
            if (AutoScroll.IsChecked == true)
            {
                LogBox.ScrollToEnd();
            }
        });
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _server.LogReceived -= OnLogReceived;
        Closed -= OnClosed;
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => LogBox.Clear();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
