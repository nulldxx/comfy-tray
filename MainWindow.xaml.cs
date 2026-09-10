using System;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ComfyTray;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by WPF XAML framework")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:TypesThatOwnDisposableFieldsShouldBeDisposable", Justification = "WPF Window lifetime is framework-managed; the server is disposed on Exit.")]
internal sealed partial class MainWindow : Window
{
    private static readonly BitmapImage RedIcon =
        new(new Uri("pack://application:,,,/icons/red.ico"));

    private static readonly BitmapImage GreenIcon =
        new(new Uri("pack://application:,,,/icons/green.ico"));

    private readonly ComfyConfig _config;
    private readonly HookSettings _hooks = HookSettings.Load();
    private readonly ComfyServerManager _server = new();
    private GuardClient? _guard;
    private LogWindow? _logWindow;

    /// <summary>
    /// True when the watch stopped ComfyUI because another user took the console, so it
    /// should be restarted when this session reconnects. Only touched on the UI thread.
    /// </summary>
    private bool _stoppedByUserSwitch;

    public MainWindow()
    {
        InitializeComponent();

        _config = ComfyConfig.Load(out var loadError);
        PurgeItem.IsChecked = _config.PurgeOutputsAndHistory;
        WatchLogonItem.IsChecked = _config.WatchForUserLogon;
        BlockOutboundItem.IsChecked = _config.BlockOutboundNetwork;
        _server.Hooks = _hooks;

        // The guard's own log lines go through the same sink as everything else, so they appear
        // in the Logs window without any further plumbing.
        _guard = new GuardClient(_server.AppendExternalLog);
        _server.Guard = _guard;

        _server.StateChanged += OnServerStateChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        UpdateForState(_server.State);

        if (loadError != null)
        {
            Dispatcher.BeginInvoke(() => MessageBox.Show(
                loadError, "ComfyUI Tray", MessageBoxButton.OK, MessageBoxImage.Warning));
        }
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        _stoppedByUserSwitch = false;
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

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _stoppedByUserSwitch = false;
        _server.Stop();
    }

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        _stoppedByUserSwitch = false;
        try
        {
            _server.Stop();
            _server.Start(_config);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "Could not restart ComfyUI",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Purge_Click(object sender, RoutedEventArgs e)
    {
        var enabled = PurgeItem.IsChecked;
        _config.PurgeOutputsAndHistory = enabled;
        _config.TrySave(out _);
        _server.SetPurgeEnabled(enabled);
    }

    /// <summary>
    /// Toggles best-effort outbound blocking for the server process. The environment is fixed
    /// when the process is created, so a change only bites on the next start — say so rather
    /// than let the user believe a running server just changed behaviour.
    /// </summary>
    private void BlockOutbound_Click(object sender, RoutedEventArgs e)
    {
        var enabled = BlockOutboundItem.IsChecked;
        _config.BlockOutboundNetwork = enabled;
        _config.TrySave(out _);

        if (_server.State == ComfyState.Running)
        {
            MessageBox.Show(
                (enabled
                    ? "Outbound blocking will apply the next time ComfyUI starts."
                    : "Outbound blocking will be lifted the next time ComfyUI starts.") +
                "\n\nRestart ComfyUI from the tray menu to apply it now.",
                "ComfyUI Tray", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void WatchLogon_Click(object sender, RoutedEventArgs e)
    {
        var enabled = WatchLogonItem.IsChecked;
        _config.WatchForUserLogon = enabled;
        _config.TrySave(out _);
        if (!enabled)
        {
            // Don't let a reconnect later resurrect a server the watch is no longer minding.
            _stoppedByUserSwitch = false;
        }
    }

    /// <summary>
    /// Yields the machine to whoever takes the physical console. Fired on the SystemEvents
    /// hidden-window thread, so marshal to the UI thread before touching state or the server.
    /// </summary>
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        var reason = e.Reason;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_config.WatchForUserLogon)
            {
                return;
            }

            switch (reason)
            {
                case SessionSwitchReason.ConsoleDisconnect:
                    if (_server.IsRunning)
                    {
                        _stoppedByUserSwitch = true;
                        _server.Stop();
                    }

                    break;

                case SessionSwitchReason.ConsoleConnect:
                    if (_stoppedByUserSwitch)
                    {
                        _stoppedByUserSwitch = false;
                        TryAutoRestart();
                    }

                    break;

                default:
                    break;
            }
        });
    }

    private void TryAutoRestart()
    {
        try
        {
            _server.Start(_config);
        }
        catch (InvalidOperationException)
        {
            // Start already logged the reason to the ring buffer; don't pop a modal on return.
        }
    }

    /// <summary>
    /// Edits the before-start/after-stop hook commands. The dialog updates <see cref="_hooks"/>
    /// in place and persists it, so the server manager — which holds the same instance — picks
    /// up the change on its next start or stop.
    /// </summary>
    private void Configuration_Click(object sender, RoutedEventArgs e) =>
        _ = new ConfigWindow(_hooks).ShowDialog();

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
        RestartItem.IsEnabled = running;
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
        // SystemEvents holds a static event; unsubscribe so the window isn't leaked.
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _server.Stop();

        // After Stop, which has already ended the session cleanly. This is the backstop for the
        // case where there was no server running but a session somehow survived.
        _guard?.Dispose();
        _guard = null;

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
