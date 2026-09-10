using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.ServiceProcess;
using System.Threading;

namespace ComfyTray;

/// <summary>
/// The Windows service host.
///
/// <para>
/// Its two most important responsibilities are the ones at the edges. On start it deletes every
/// rule the guard owns before it will accept a single connection — covering a reboot, a crash,
/// or a stale state of any provenance. On stop it does the same, because a service that has
/// stopped cannot clean up later and must therefore clean up on the way out. Between those, a
/// watchdog ticks the session manager so a client that goes quiet without disconnecting cannot
/// hold rules forever.
/// </para>
/// </summary>
internal sealed class GuardService : ServiceBase
{
    /// <summary>The service name. Must match the installer's <c>ServiceInstall</c>.</summary>
    public const string ServiceNameConstant = "ComfyTrayGuard";

    /// <summary>
    /// How often leases and unlocks are checked. Five seconds is well inside the minimum
    /// 30-second lease, so a dead client's rules never outlive it by long.
    /// </summary>
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(5);

    private readonly GuardEventLog _log;
    private readonly ComFirewallRuleStore _store;
    private readonly GuardSessionManager _sessions;
    private readonly PipeServer _pipeServer;
    private Timer? _watchdog;

    public GuardService(bool console)
    {
        ServiceName = ServiceNameConstant;
        CanStop = true;

        // Shutdown matters as much as stop: a machine powering down with rules live must not
        // leave them behind for the next boot to puzzle over.
        CanShutdown = true;

        _log = new GuardEventLog(console);
        _store = new ComFirewallRuleStore();
        _sessions = new GuardSessionManager(_store, () => DateTimeOffset.UtcNow);

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        _pipeServer = new PipeServer(new GuardRequestDispatcher(_sessions, _store, version), _log);
    }

    /// <summary>Runs the service loop in the foreground until the token is signalled.</summary>
    public void RunInteractive(CancellationToken cancellationToken)
    {
        OnStart([]);
        try
        {
            cancellationToken.WaitHandle.WaitOne();
        }
        finally
        {
            OnStop();
        }
    }

    protected override void OnStart(string[] args)
    {
        // Before anything can connect. A rule surviving from a previous life belongs to a session
        // that no longer exists, and leaving one in place is how a user ends up with an
        // interpreter that cannot reach the network for reasons nobody can explain.
        var purged = _sessions.PurgeAll();
        if (purged > 0)
        {
            _log.Warning(string.Format(
                CultureInfo.InvariantCulture,
                "removed {0} firewall rule(s) left behind by a previous run",
                purged));
        }

        ReportFirewallHealth();

        _watchdog = new Timer(_ => Tick(), null, WatchdogInterval, WatchdogInterval);
        _pipeServer.Start();

        _log.Info("service started");
    }

    protected override void OnStop()
    {
        _log.Info("service stopping");

        _watchdog?.Dispose();
        _watchdog = null;

        _pipeServer.Stop();

        var removed = _sessions.PurgeAll();
        _log.Info($"service stopped; removed {removed} firewall rule(s)");
    }

    protected override void OnShutdown() => OnStop();

    /// <summary>
    /// Says so when the firewall will not act on what the guard creates.
    ///
    /// <para>
    /// The worst outcome for a feature like this is not failing but appearing to work: a rule
    /// added while the firewall is off is accepted and enforces nothing, and one added where
    /// group policy owns the profile is ignored outright. Both are invisible unless something
    /// looks, so the guard looks, and says so at the top of its log.
    /// </para>
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "A health probe failure is worth logging but must not stop the service starting.")]
    private void ReportFirewallHealth()
    {
        try
        {
            if (_store.GetHealth().DegradedReason is { } reason)
            {
                _log.Warning($"blocking will be degraded: {reason}");
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"could not determine firewall state: {ex.Message}");
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "The watchdog must survive a transient firewall failure and try again.")]
    private void Tick()
    {
        try
        {
            _sessions.Tick();
        }
        catch (Exception ex)
        {
            _log.Error($"watchdog failed: {ex.Message}");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watchdog?.Dispose();
            _pipeServer.Dispose();
            _store.Dispose();
        }

        base.Dispose(disposing);
    }
}
