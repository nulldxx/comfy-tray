using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ComfyTray;

/// <summary>
/// Raised when a request cannot be honoured. Carries a stable code the tray can branch on.
/// </summary>
internal sealed class GuardRequestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Why a session ended. Recorded in the log so a user can tell a clean shutdown from a
/// watchdog kill.
/// </summary>
internal enum SessionEndReason
{
    /// <summary>The tray asked.</summary>
    Requested,

    /// <summary>The pipe dropped — tray crash, kill, or logoff.</summary>
    Disconnected,

    /// <summary>Heartbeats stopped without the connection dropping.</summary>
    LeaseExpired,

    /// <summary>The service is shutting down.</summary>
    ServiceStopping,
}

/// <summary>
/// The guard's whole model of what is blocked and why, with no Windows API in sight.
///
/// <para>
/// This class exists in this shape on purpose. The parts of the guard that cannot be tested —
/// COM, named pipes, job objects, the service host — are precisely the parts where a mistake is
/// merely annoying. The part where a mistake is serious is the one that decides when firewall
/// rules stop existing, because a bug there leaves a machine with a silently crippled Python
/// interpreter and no obvious cause. So that logic lives here, driven by an injected clock and
/// an injected rule store, and is covered by tests that run anywhere.
/// </para>
///
/// <para>
/// The invariant every method upholds: <b>a rule exists only while its session is alive and its
/// lease is fresh</b>. There is no path that creates a rule without an owning session, and no
/// way for a session to end without its rules going with it.
/// </para>
/// </summary>
internal sealed class GuardSessionManager
{
    /// <summary>
    /// Concurrent sessions allowed. More than one is not an edge case: the tray offers to stop
    /// ComfyUI when another user takes over the console, so two logged-on users each running a
    /// tray is an expected configuration.
    /// </summary>
    public const int MaxSessions = 4;

    public const int MinLeaseSeconds = 30;
    public const int MaxLeaseSeconds = 300;
    public const int MinUnlockSeconds = 60;
    public const int MaxUnlockSeconds = 3600;

    /// <summary>Log lines held per session before the oldest are dropped.</summary>
    private const int MaxBufferedLogLines = 200;

    private readonly IFirewallRuleStore _store;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Session> _sessions = [];

    public GuardSessionManager(IFirewallRuleStore store, Func<DateTimeOffset> clock)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Removes every rule the guard owns, whatever session claims it, and forgets all sessions.
    ///
    /// <para>
    /// Called on service start — covering a reboot or a crash that left rules behind — and again
    /// on stop, because a service that has stopped cannot clean up later and must therefore
    /// clean up on the way out.
    /// </para>
    /// </summary>
    public int PurgeAll()
    {
        lock (_gate)
        {
            var rules = _store.ListGuardRules();
            foreach (var rule in rules)
            {
                _store.Remove(rule.RuleName);
            }

            _sessions.Clear();
            return rules.Count;
        }
    }

    public (Guid SessionId, int GrantedLeaseSeconds) BeginSession(
        string clientVersion, int comfyPid, int requestedLeaseSeconds)
    {
        lock (_gate)
        {
            if (_sessions.Count >= MaxSessions)
            {
                throw new GuardRequestException(
                    "TooManySessions",
                    $"The guard is already tracking {MaxSessions} sessions.");
            }

            var lease = Math.Clamp(requestedLeaseSeconds, MinLeaseSeconds, MaxLeaseSeconds);
            var session = new Session
            {
                Id = Guid.NewGuid(),
                ClientVersion = clientVersion,
                ComfyPid = comfyPid,
                LeaseSeconds = lease,
                LeaseExpiresUtc = _clock().AddSeconds(lease),
            };

            _sessions[session.Id] = session;
            Log(session, $"session opened for pid {comfyPid} (client {clientVersion})");
            return (session.Id, lease);
        }
    }

    /// <summary>
    /// Adds executables to a session's blocked set. Idempotent — the tracker resends everything
    /// it can see on every scan, so re-blocking a known path must cost nothing.
    /// </summary>
    public (IReadOnlyList<string> Added, IReadOnlyList<string> AlreadyBlocked, IReadOnlyList<string> Rejected)
        BlockImages(Guid sessionId, IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        lock (_gate)
        {
            var session = Require(sessionId);
            var admitted = GuardPathSet.Admit(paths);

            var known = new HashSet<string>(session.DesiredPaths, StringComparer.Ordinal);
            var alreadyBlocked = admitted.Where(known.Contains).ToList();

            var (accepted, rejected) = FirewallPlan.ApplyCap(session.DesiredPaths, admitted);
            session.DesiredPaths.AddRange(accepted);

            Reconcile(session);

            foreach (var path in accepted)
            {
                Log(session, session.IsUnlocked
                    ? $"recorded {path} — it will be blocked when the unlock expires"
                    : $"blocked outbound: {path}");
            }

            if (rejected.Count > 0)
            {
                Log(session,
                    $"refused {rejected.Count} more executable(s): this session already holds the " +
                    $"maximum of {GuardPathSet.MaxRulesPerSession} rules");
            }

            return (accepted, alreadyBlocked, rejected);
        }
    }

    /// <summary>
    /// Lifts this session's blocking for a while — enough to let ComfyUI-Manager update, say.
    ///
    /// <para>
    /// The rules are <b>removed</b> rather than disabled. Disabling would preserve the exact set
    /// to re-enable later, but it would also mean a crash mid-unlock left disabled rules sitting
    /// in the policy store, and stale rules are the failure this design fears most. Removing
    /// fails open, which is consistent with everything else here: no rules without a live
    /// session.
    /// </para>
    /// </summary>
    public DateTimeOffset Unlock(Guid sessionId, int seconds)
    {
        lock (_gate)
        {
            var session = Require(sessionId);
            var duration = Math.Clamp(seconds, MinUnlockSeconds, MaxUnlockSeconds);

            session.UnlockUntilUtc = _clock().AddSeconds(duration);
            Reconcile(session);

            Log(session, string.Format(
                CultureInfo.InvariantCulture,
                "network allowed until {0:yyyy-MM-ddTHH:mm:ssZ} — {1} rule(s) lifted",
                session.UnlockUntilUtc.Value,
                session.DesiredPaths.Count));

            return session.UnlockUntilUtc.Value;
        }
    }

    /// <summary>
    /// Ends an unlock early, restoring every path the session knows about — including any
    /// discovered while it was unlocked.
    /// </summary>
    public int Rearm(Guid sessionId)
    {
        lock (_gate)
        {
            var session = Require(sessionId);
            return RearmLocked(session);
        }
    }

    /// <summary>Extends the lease and hands back whatever the guard has logged for this session.</summary>
    public (DateTimeOffset LeaseExpiresUtc, bool Unlocked, IReadOnlyList<string> LogLines)
        Heartbeat(Guid sessionId)
    {
        lock (_gate)
        {
            var session = Require(sessionId);
            session.LeaseExpiresUtc = _clock().AddSeconds(session.LeaseSeconds);

            var lines = session.LogLines.ToList();
            session.LogLines.Clear();

            return (session.LeaseExpiresUtc, session.IsUnlocked, lines);
        }
    }

    /// <summary>Ends a session and removes everything it was holding.</summary>
    public int EndSession(Guid sessionId, SessionEndReason reason)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(sessionId, out var session)
                ? EndSessionLocked(session, reason)
                : 0;
        }
    }

    /// <summary>
    /// Drives every time-based transition. Called on a timer by the service and directly by the
    /// tests, so expiry is deterministic rather than something that has to be waited for.
    ///
    /// <para>
    /// The lease watchdog is the teardown path that matters when the obvious one fails. A
    /// dropped pipe already ends a session, which covers a tray that crashes or is killed; this
    /// covers a tray that is still connected but no longer running — a wedged thread, a process
    /// suspended under a debugger, a hung write. Without it, such a client holds firewall rules
    /// indefinitely.
    /// </para>
    /// </summary>
    public void Tick()
    {
        lock (_gate)
        {
            var now = _clock();

            foreach (var session in _sessions.Values.ToList())
            {
                if (session.LeaseExpiresUtc <= now)
                {
                    EndSessionLocked(session, SessionEndReason.LeaseExpired);
                    continue;
                }

                if (session.UnlockUntilUtc is { } until && until <= now)
                {
                    RearmLocked(session);
                }
            }
        }
    }

    public IReadOnlyList<GuardSessionSummary> GetSummaries()
    {
        lock (_gate)
        {
            return _sessions.Values
                .Select(s => new GuardSessionSummary(
                    s.Id, s.ComfyPid, s.IsUnlocked ? 0 : s.DesiredPaths.Count,
                    s.IsUnlocked, s.LeaseExpiresUtc))
                .ToList();
        }
    }

    private int RearmLocked(Session session)
    {
        session.UnlockUntilUtc = null;
        Reconcile(session);
        Log(session, $"blocking restored — {session.DesiredPaths.Count} rule(s) back in place");
        return session.DesiredPaths.Count;
    }

    private int EndSessionLocked(Session session, SessionEndReason reason)
    {
        // Clearing the desired set first means Reconcile computes a removal for every rule the
        // session holds; there is only one code path that touches the firewall.
        var held = session.DesiredPaths.Count;
        session.DesiredPaths.Clear();
        session.UnlockUntilUtc = null;
        Reconcile(session);

        _sessions.Remove(session.Id);
        return held;
    }

    /// <summary>
    /// Makes the firewall match what this session should be holding. The single point where
    /// rules are created or destroyed.
    /// </summary>
    private void Reconcile(Session session)
    {
        // An unlocked session wants nothing blocked, but keeps its desired set so that re-arming
        // restores it — including anything discovered during the unlock.
        var desired = session.IsUnlocked ? [] : session.DesiredPaths;

        var plan = FirewallPlan.Compute(session.Id, desired, _store.ListGuardRules());

        foreach (var add in plan.Adds)
        {
            _store.Add(add);
        }

        foreach (var name in plan.Removes)
        {
            _store.Remove(name);
        }
    }

    private Session Require(Guid sessionId) =>
        _sessions.TryGetValue(sessionId, out var session)
            ? session
            : throw new GuardRequestException(
                "UnknownSession",
                $"Session {sessionId:N} is not open. It may have been torn down by the lease watchdog.");

    private static void Log(Session session, string text)
    {
        session.LogLines.Add(text);
        while (session.LogLines.Count > MaxBufferedLogLines)
        {
            session.LogLines.RemoveAt(0);
        }
    }

    private sealed class Session
    {
        public Guid Id { get; init; }

        public string ClientVersion { get; init; } = string.Empty;

        public int ComfyPid { get; init; }

        public int LeaseSeconds { get; init; }

        public DateTimeOffset LeaseExpiresUtc { get; set; }

        /// <summary>Null when armed; the moment blocking resumes when unlocked.</summary>
        public DateTimeOffset? UnlockUntilUtc { get; set; }

        public bool IsUnlocked => UnlockUntilUtc is not null;

        /// <summary>
        /// Every path this session wants blocked, in discovery order. Survives an unlock so that
        /// re-arming restores the full set.
        /// </summary>
        public List<string> DesiredPaths { get; } = [];

        public List<string> LogLines { get; } = [];
    }
}
