using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
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
        RefreshOutboundChecks();
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
    /// Ticks the mode the configuration currently holds. Cheap enough for the constructor,
    /// unlike <see cref="RefreshOutboundMenu"/>, which asks the guard how it is doing.
    /// </summary>
    private void RefreshOutboundChecks()
    {
        var mode = _config.Mode;
        OutboundAllowItem.IsChecked = mode == OutboundMode.None;
        OutboundEnvItem.IsChecked = mode == OutboundMode.EnvironmentOnly;
        OutboundFirewallItem.IsChecked = mode == OutboundMode.Firewall;
    }

    /// <summary>
    /// Brings the whole submenu up to date, including what the guard is actually doing. Driven
    /// from the submenu opening rather than a timer, so nothing is asked of the guard while
    /// nobody is looking.
    /// </summary>
    private void RefreshOutboundMenu()
    {
        RefreshOutboundChecks();

        var state = OutboundMenuState.For(
            _config.Mode,
            _guard?.Probe() ?? GuardAvailability.NotInstalled,
            _guard?.HasSession == true,
            _guard?.UnlockUntilUtc,
            DateTimeOffset.UtcNow,
            _guard?.Health);

        OutboundFirewallItem.IsEnabled = state.FirewallEnabled;
        UnlockItem.IsEnabled = state.UnlockEnabled;
        UnlockItem.Header = state.UnlockHeader;
        GuardStatusItem.Header = state.StatusHeader;
    }

    private void OutboundMenu_Opened(object sender, RoutedEventArgs e) => RefreshOutboundMenu();

    /// <summary>
    /// Chooses an outbound policy. The three items behave as a radio group, which WPF menus have
    /// no notion of, so the selection is rewritten from the configuration afterwards rather than
    /// left to the checkbox that was clicked.
    /// </summary>
    private void OutboundMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not string tag ||
            !Enum.TryParse<OutboundMode>(tag, out var mode))
        {
            return;
        }

        _config.BlockOutboundNetwork = mode != OutboundMode.None;
        _config.EnforceWithFirewall = mode == OutboundMode.Firewall;
        _config.TrySave(out _);

        var restartNeeded = _server.SetOutboundMode(mode);
        RefreshOutboundMenu();

        if (mode == OutboundMode.Firewall &&
            _guard?.Availability is not GuardAvailability.Connected)
        {
            MessageBox.Show(
                "Firewall enforcement needs the ComfyTray Guard service, which is not " +
                $"available ({GuardStatusItem.Header}).\n\n" +
                "Run the ComfyTray installer again and tick the firewall guard. Until then " +
                "ComfyUI is launched with best-effort blocking: environment variables that " +
                "most Python libraries honour, but which a custom node using a raw socket " +
                "can ignore.",
                "ComfyUI Tray", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (restartNeeded && _server.State == ComfyState.Running)
        {
            MessageBox.Show(
                "The new setting is saved, but the environment ComfyUI was launched with " +
                "cannot be changed while it runs.\n\n" +
                "Restart ComfyUI from the tray menu to apply it fully.",
                "ComfyUI Tray", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>
    /// Lifts blocking for a while, or puts it back if it is already lifted. The same item does
    /// both, because while blocking is lifted the only thing anyone wants from it is to end that.
    /// </summary>
    private void Unlock_Click(object sender, RoutedEventArgs e)
    {
        if (_guard is null)
        {
            return;
        }

        if (_guard.UnlockUntilUtc is { } until && until > DateTimeOffset.UtcNow)
        {
            _guard.TryRearm();
        }
        else
        {
            _guard.TryUnlock(OutboundMenuState.UnlockDuration);
        }

        RefreshOutboundMenu();
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
                $"Config: {ComfyConfig.ConfigPath}\n\n" +
                "Firewall enforcement uses the ComfyTray Guard service, which removes its rules " +
                "when ComfyUI stops. If rules are ever left behind — after a crash, say — clear " +
                "them from an elevated prompt with:\n\n" +
                "netsh advfirewall firewall delete rule group=\"ComfyTrayGuard\"",
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
