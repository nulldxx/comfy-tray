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
/// Its two most important responsibilities are the ones at the edges. Once running it deletes
/// every rule the guard owns before it will accept a single connection — covering a reboot, a
/// crash, or a stale state of any provenance. On stop it does the same, because a service that
/// has stopped cannot clean up later and must therefore clean up on the way out. Between those, a
/// watchdog ticks the session manager so a client that goes quiet without disconnecting cannot
/// hold rules forever.
/// </para>
///
/// <para>
/// None of that happens on the Service Control Manager's thread. <see cref="OnStart"/> starts a
/// thread and returns, and everything below runs on it. That is not an optimisation, it is the
/// fix for a deadlock: Windows Installer holds the SCM's service-database lock for the whole of
/// its StartServices action, so a service that blocks its own start on something which may in
/// turn demand-start another service — and connecting to the Windows Firewall is exactly that —
/// waits on a lock held by the installer that is waiting on it. Reporting running promptly and
/// doing the work afterwards is what makes that impossible.
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

    /// <summary>
    /// How long a stop waits for an in-flight startup to notice it. Generous because the thread
    /// may be parked in a COM call that has no cancellation; the stop continues regardless when
    /// it expires, and startup discards whatever it built rather than publishing it.
    /// </summary>
    private static readonly TimeSpan StartupJoinTimeout = TimeSpan.FromSeconds(30);

    private readonly GuardEventLog _log;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stopping = new();

    private ComFirewallRuleStore? _store;
    private GuardSessionManager? _sessions;
    private PipeServer? _pipeServer;
    private Timer? _watchdog;
    private Thread? _startup;

    /// <summary>
    /// Deliberately does nothing that can block or fail. It runs before
    /// <c>ServiceBase.Run</c> has entered the SCM dispatcher, so anything
    /// slow here is invisible to the SCM and counts against a start that has not been reported
    /// as beginning yet.
    /// </summary>
    public GuardService(bool console)
    {
        ServiceName = ServiceNameConstant;
        CanStop = true;

        // Shutdown matters as much as stop: a machine powering down with rules live must not
        // leave them behind for the next boot to puzzle over.
        CanShutdown = true;

        _log = new GuardEventLog(console);
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
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

        // Before anything that could fail or stall, so that a log which stops here says plainly
        // that the process started and the firewall did not answer.
        _log.Info($"service starting (version {version})");

        _startup = new Thread(() => Initialise(version))
        {
            IsBackground = true,
            Name = "ComfyTrayGuard startup",
        };
        _startup.Start();
    }

    /// <summary>
    /// Everything the service needs before it is useful, off the SCM's thread.
    ///
    /// <para>
    /// The cancellation checks are not decoration. A stop arriving mid-startup must not race a
    /// half-built service into existence, so each thing built is published under the gate only if
    /// no stop has been requested, and discarded if one has. The pipe goes last: until the store
    /// exists there is nothing useful to say to a caller, and the tray already treats a guard it
    /// cannot reach as unavailable rather than as an error.
    /// </para>
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "Top-level handler for the startup thread: any failure must be logged and stop the service cleanly.")]
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Ownership of each object moves to a field under the gate; the paths that decline to publish dispose it explicitly.")]
    private void Initialise(string version)
    {
        try
        {
            _log.Info("connecting to the Windows Firewall");
            var store = new ComFirewallRuleStore();
            _log.Info("connected to the Windows Firewall");

            var sessions = new GuardSessionManager(store, () => DateTimeOffset.UtcNow);

            lock (_gate)
            {
                if (_stopping.IsCancellationRequested)
                {
                    store.Dispose();
                    return;
                }

                _store = store;
                _sessions = sessions;
            }

            // A rule surviving from a previous life belongs to a session that no longer exists,
            // and leaving one in place is how a user ends up with an interpreter that cannot
            // reach the network for reasons nobody can explain.
            var purged = sessions.PurgeAll();
            if (purged > 0)
            {
                _log.Warning(string.Format(
                    CultureInfo.InvariantCulture,
                    "removed {0} firewall rule(s) left behind by a previous run",
                    purged));
            }

            ReportFirewallHealth(store);

            var pipeServer = new PipeServer(
                new GuardRequestDispatcher(sessions, store, version), _log);

            lock (_gate)
            {
                if (_stopping.IsCancellationRequested)
                {
                    pipeServer.Dispose();
                    return;
                }

                _pipeServer = pipeServer;
                _watchdog = new Timer(_ => Tick(), null, WatchdogInterval, WatchdogInterval);
            }

            pipeServer.Start();
            _log.Info("service started");
        }
        catch (Exception ex)
        {
            // Reaches the Application event log, which is where an administrator looking at a
            // service that stopped on its own will go. Stopping rather than retrying is the
            // honest response: nothing here is transient enough for a restart to fix, and the
            // tray reports the guard as unavailable and carries on without it.
            _log.Error($"could not start: {ex.Message}");
            Stop();
        }
    }

    protected override void OnStop()
    {
        _log.Info("service stopping");

        _stopping.Cancel();

        // Never join ourselves: a failed startup calls Stop() from the very thread below, and
        // the SCM runs that stop inline on the caller.
        if (_startup is { } startup && startup != Thread.CurrentThread)
        {
            startup.Join(StartupJoinTimeout);
        }

        Timer? watchdog;
        PipeServer? pipeServer;
        GuardSessionManager? sessions;

        lock (_gate)
        {
            watchdog = _watchdog;
            _watchdog = null;
            pipeServer = _pipeServer;
            sessions = _sessions;
        }

        watchdog?.Dispose();
        pipeServer?.Stop();

        // Null when the firewall was never reached, in which case there is nothing of ours on
        // the machine to remove and saying "removed 0" is the truth.
        var removed = sessions?.PurgeAll() ?? 0;
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
    private void ReportFirewallHealth(ComFirewallRuleStore store)
    {
        try
        {
            if (store.GetHealth().DegradedReason is { } reason)
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
            _sessions?.Tick();
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
            _pipeServer?.Dispose();
            _store?.Dispose();
            _stopping.Dispose();
        }

        base.Dispose(disposing);
    }
}
