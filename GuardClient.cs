using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32;

namespace ComfyTray;

/// <summary>
/// Win32 helpers the tray needs for firewall enforcement.
/// </summary>
internal static partial class TrayNativeMethods
{
    /// <summary>
    /// Expands an 8.3 short path to its long form.
    ///
    /// <para>
    /// A firewall rule stores the executable path as text and matches it literally, so a rule
    /// created for <c>C:\PROGRA~1\...</c> never matches the process it was meant for. A path a
    /// user typed into the configuration, or one recorded in a <c>pyvenv.cfg</c>, can easily be
    /// in that form. Returns the input unchanged when it cannot be expanded, which includes the
    /// common case of it already being long.
    /// </para>
    /// </summary>
    public static string GetLongPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var buffer = new char[1024];
        var length = GetLongPathNameW(path, buffer, (uint)buffer.Length);

        return length > 0 && length < buffer.Length
            ? new string(buffer, 0, (int)length)
            : path;
    }

    [System.Runtime.InteropServices.LibraryImport(
        "kernel32.dll",
        SetLastError = true,
        StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16)]
    private static partial uint GetLongPathNameW(
        string shortPath, [System.Runtime.InteropServices.Out] char[] longPath, uint bufferLength);
}

/// <summary>
/// The tray's side of the conversation with the guard service.
///
/// <para>
/// Synchronous, with a worker only for the heartbeat. <see cref="ComfyServerManager.Start"/>
/// holds its lock on the UI thread and already does bounded blocking work there — the
/// before-start hook runs to completion with a timeout — so this matches that rather than
/// introducing an async path that would have to be marshalled back anyway.
/// </para>
///
/// <para>
/// The guard being absent is a normal state, not an error. Every method here fails soft: the
/// caller logs, carries on, and ComfyUI still launches with environment-variable isolation. The
/// one thing this class must never do is prevent the server starting.
/// </para>
/// </summary>
internal sealed class GuardClient : IDisposable
{
    /// <summary>
    /// Lease length requested. The guard tears a session down after this long without a
    /// heartbeat, so it is also how long rules could outlive a tray that dies in a way the pipe
    /// does not notice.
    /// </summary>
    private const int LeaseSeconds = 60;

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How long a reachability answer is reused before asking again.</summary>
    private static readonly TimeSpan ProbeCacheLifetime = TimeSpan.FromSeconds(30);

    private readonly Action<string> _log;
    private readonly object _gate = new();

    private NamedPipeClientStream? _pipe;
    private FrameReader? _reader;
    private Guid _sessionId;
    private long _nextRequestId;
    private Timer? _heartbeat;
    private DateTimeOffset _lastProbe;
    private bool _disposed;

    public GuardClient(Action<string> log) =>
        _log = log ?? throw new ArgumentNullException(nameof(log));

    /// <summary>Last known reachability, refreshed by <see cref="Probe"/>.</summary>
    public GuardAvailability Availability { get; private set; } = GuardAvailability.Unknown;

    /// <summary>Last firewall health the guard reported, or null if it has never been asked.</summary>
    public FirewallHealth? Health { get; private set; }

    /// <summary>
    /// When blocking is temporarily lifted, the moment it comes back. Null while enforcing.
    /// </summary>
    public DateTimeOffset? UnlockUntilUtc { get; private set; }

    /// <summary>True while a session is open and rules may be held on our behalf.</summary>
    public bool HasSession
    {
        get
        {
            lock (_gate)
            {
                return _sessionId != Guid.Empty;
            }
        }
    }

    /// <summary>
    /// Works out whether the guard can be reached, without opening a session.
    ///
    /// <para>
    /// The registry is consulted first so that "not installed" can be told apart from "installed
    /// but not running" — the service key under <c>HKLM\SYSTEM\CurrentControlSet\Services</c> is
    /// world-readable, which avoids taking a dependency on <c>ServiceController</c> purely to
    /// ask a yes/no question.
    /// </para>
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "A probe reports a state; it has no failure mode the caller can act on differently.")]
    public GuardAvailability Probe()
    {
        lock (_gate)
        {
            if (_pipe?.IsConnected == true)
            {
                return Availability = GuardAvailability.Connected;
            }

            // Cached, because this runs on the UI thread when the tray menu opens and a probe
            // costs a connection attempt. A service appearing or disappearing is not something
            // that needs noticing within the second.
            if (Availability != GuardAvailability.Unknown &&
                DateTimeOffset.UtcNow - _lastProbe < ProbeCacheLifetime)
            {
                return Availability;
            }

            _lastProbe = DateTimeOffset.UtcNow;

            if (!IsServiceRegistered())
            {
                return Availability = GuardAvailability.NotInstalled;
            }

            try
            {
                using var probe = Connect(TimeSpan.FromMilliseconds(250));
                return Availability = probe is null
                    ? GuardAvailability.Stopped
                    : GuardAvailability.Connected;
            }
            catch (Exception)
            {
                return Availability = GuardAvailability.Stopped;
            }
        }
    }

    /// <summary>
    /// Opens a session and starts the heartbeat. Returns false when the guard is unavailable,
    /// which the caller should log and carry on from.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "Any failure here degrades to environment-variable isolation; none may stop a launch.")]
    public bool TryBeginSession(int comfyPid, out Guid sessionId)
    {
        sessionId = Guid.Empty;

        lock (_gate)
        {
            try
            {
                Disconnect();

                var pipe = Connect(DefaultTimeout);
                if (pipe is null)
                {
                    Availability = IsServiceRegistered()
                        ? GuardAvailability.Stopped
                        : GuardAvailability.NotInstalled;
                    _log($"[guard] service is {Describe(Availability)}");
                    return false;
                }

                _pipe = pipe;
                _reader = new FrameReader(pipe);

                if (!Handshake())
                {
                    Disconnect();
                    return false;
                }

                var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
                if (Send(new BeginSessionRequest(version, comfyPid, LeaseSeconds))
                    is not BeginSessionResponse opened)
                {
                    Disconnect();
                    return false;
                }

                _sessionId = sessionId = opened.SessionId;
                _heartbeat = new Timer(_ => Beat(), null, HeartbeatInterval, HeartbeatInterval);
                return true;
            }
            catch (Exception ex)
            {
                _log($"[guard] could not open a session: {ex.Message}");
                Disconnect();
                return false;
            }
        }
    }

    /// <summary>Asks for outbound blocking on these executables. Idempotent.</summary>
    [SuppressMessage(
        "Design",
        "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "Blocking is best-effort from the tray's point of view; a failure is logged, not thrown.")]
    public bool TryBlockImages(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        lock (_gate)
        {
            if (_sessionId == Guid.Empty || paths.Count == 0)
            {
                return false;
            }

            try
            {
                if (Send(new BlockImagesRequest(_sessionId, paths)) is not BlockImagesResponse blocked)
                {
                    return false;
                }

                foreach (var path in blocked.Added)
                {
                    _log($"[guard] blocked outbound: {path}");
                }

                if (blocked.Rejected.Count > 0)
                {
                    _log($"[guard] refused {blocked.Rejected.Count} executable(s): session rule limit reached");
                }

                return blocked.Added.Count > 0 || blocked.AlreadyBlocked.Count > 0;
            }
            catch (Exception ex)
            {
                _log($"[guard] blocking failed: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>Lifts blocking for a while, then it comes back on its own.</summary>
    [SuppressMessage(
        "Design",
        "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "Reported to the user through the log rather than by propagating.")]
    public bool TryUnlock(TimeSpan duration)
    {
        lock (_gate)
        {
            if (_sessionId == Guid.Empty)
            {
                return false;
            }

            try
            {
                if (Send(new UnlockRequest(_sessionId, (int)duration.TotalSeconds))
                    is not UnlockResponse unlocked)
                {
                    return false;
                }

                UnlockUntilUtc = unlocked.ExpiresUtc;
                _log($"[guard] network allowed until {unlocked.ExpiresUtc.ToLocalTime():HH:mm:ss}");
                return true;
            }
            catch (Exception ex)
            {
                _log($"[guard] could not lift blocking: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>Restores blocking immediately, ending an unlock early.</summary>
    [SuppressMessage(
        "Design",
        "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "Reported to the user through the log rather than by propagating.")]
    public bool TryRearm()
    {
        lock (_gate)
        {
            if (_sessionId == Guid.Empty)
            {
                return false;
            }

            try
            {
                if (Send(new RearmRequest(_sessionId)) is not RearmResponse rearmed)
                {
                    return false;
                }

                UnlockUntilUtc = null;
                _log($"[guard] blocking restored — {rearmed.RulesRestored} rule(s) back in place");
                return true;
            }
            catch (Exception ex)
            {
                _log($"[guard] could not restore blocking: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Gives up the session. Closing the pipe alone would do it — the guard treats a dropped
    /// connection as teardown — but asking first means the log records a clean end rather than a
    /// dropped client, which is the difference between a normal stop and something going wrong.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "Teardown is best-effort; dropping the pipe achieves the same thing.")]
    public void EndSession()
    {
        lock (_gate)
        {
            if (_sessionId == Guid.Empty)
            {
                return;
            }

            try
            {
                if (Send(new EndSessionRequest(_sessionId)) is EndSessionResponse ended)
                {
                    _log($"[guard] session closed — {ended.RulesRemoved} rule(s) removed");
                }
            }
            catch (Exception ex)
            {
                _log($"[guard] session closed uncleanly ({ex.Message}); " +
                     "the guard removes the rules when the connection drops");
            }
            finally
            {
                Disconnect();
            }
        }
    }

    private bool Handshake()
    {
        var response = Send(new HelloRequest(GuardProtocolVersion.Current));

        if (response is not HelloResponse hello)
        {
            Availability = GuardAvailability.VersionMismatch;
            _log("[guard] the installed guard service speaks a different protocol; " +
                 "reinstall so the tray and the service match");
            return false;
        }

        Availability = GuardAvailability.Connected;
        Health = hello.Health;

        if (hello.Health.DegradedReason is { } reason)
        {
            _log($"[guard] WARNING: {reason}");
        }

        return true;
    }

    /// <summary>
    /// Sends one request and reads its reply. The protocol is strict request/response — the
    /// guard's own log lines ride back on the heartbeat rather than arriving unbidden — so there
    /// is nothing to demultiplex and the lock is enough to keep callers from interleaving.
    /// </summary>
    private GuardResponse? Send(GuardRequest request)
    {
        if (_pipe is null || _reader is null)
        {
            return null;
        }

        var id = ++_nextRequestId;
        MessageFraming.Write(_pipe, MessageFraming.Serialize(request with { Id = id }));

        var line = _reader.ReadLine();
        if (line is null)
        {
            throw new InvalidOperationException("The guard service closed the connection.");
        }

        var response = MessageFraming.DeserializeResponse(line);

        if (response is ErrorResponse error)
        {
            _log($"[guard] {error.Code}: {error.Message}");
            return null;
        }

        return response.Id == id
            ? response
            : throw new InvalidOperationException(
                $"The guard answered request {response.Id} while {id} was outstanding.");
    }

    [SuppressMessage(
        "Design",
        "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "A missed heartbeat is retried on the next tick; a throw here would kill the timer.")]
    private void Beat()
    {
        lock (_gate)
        {
            if (_sessionId == Guid.Empty)
            {
                return;
            }

            try
            {
                if (Send(new HeartbeatRequest(_sessionId)) is not HeartbeatResponse beat)
                {
                    return;
                }

                // The guard re-arms on its own when an unlock expires, so this is how the tray
                // finds out the countdown is over.
                if (!beat.Unlocked)
                {
                    UnlockUntilUtc = null;
                }

                foreach (var line in beat.LogLines)
                {
                    _log($"[guard] {line}");
                }
            }
            catch (Exception ex)
            {
                // Losing the guard mid-session is not fatal: ComfyUI keeps running with
                // environment-variable isolation, and the guard has already removed the rules its
                // end. Say so plainly rather than leaving the user believing it is still enforced.
                _log($"[guard] lost contact with the guard service ({ex.Message}). " +
                     "Firewall blocking is no longer in force; environment-variable isolation " +
                     "still applies. Restart ComfyUI to re-establish it.");
                Disconnect();
            }
        }
    }

    private static NamedPipeClientStream? Connect(TimeSpan timeout)
    {
        // Impersonation level matters: without it the service cannot call
        // GetImpersonationUserName to record who connected.
        var pipe = new NamedPipeClientStream(
            ".",
            PipeServerName,
            PipeDirection.InOut,
            PipeOptions.WriteThrough,
            TokenImpersonationLevel.Impersonation);

        try
        {
            pipe.Connect((int)timeout.TotalMilliseconds);
            return pipe;
        }
        catch (TimeoutException)
        {
            pipe.Dispose();
            return null;
        }
        catch (System.IO.IOException)
        {
            // Every instance busy. Treat as unavailable rather than retrying here.
            pipe.Dispose();
            return null;
        }
    }

    /// <summary>Matches <c>PipeServer.PipeName</c> in the guard service.</summary>
    private const string PipeServerName = "ComfyTrayGuard";

    /// <summary>Matches <c>GuardService.ServiceNameConstant</c>.</summary>
    private const string ServiceName = "ComfyTrayGuard";

    [SuppressMessage(
        "Design",
        "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "An unreadable registry is indistinguishable from an absent service here.")]
    private static bool IsServiceRegistered()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
            return key is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Describe(GuardAvailability availability) => availability switch
    {
        GuardAvailability.NotInstalled =>
            "not installed — run the ComfyTray installer again and tick the firewall guard to enable enforcement",
        GuardAvailability.Stopped =>
            "installed but not running — start the ComfyTrayGuard service",
        GuardAvailability.VersionMismatch => "a different version to this tray",
        _ => availability.ToString(),
    };

    private void Disconnect()
    {
        _heartbeat?.Dispose();
        _heartbeat = null;
        _sessionId = Guid.Empty;
        UnlockUntilUtc = null;
        _reader = null;
        _pipe?.Dispose();
        _pipe = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            EndSession();
            _disposed = true;
        }
    }
}
