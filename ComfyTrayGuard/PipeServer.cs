using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;

namespace ComfyTray;

/// <summary>
/// Accepts tray connections and pumps them through <see cref="GuardRequestDispatcher"/>.
///
/// <para>
/// Deliberately thin. Everything that decides anything lives in <c>Shared/</c> and is tested;
/// this class only moves bytes and owns threads, because none of that can be tested off Windows
/// and the less of it there is the better.
/// </para>
///
/// <para>
/// Synchronous throughout — a thread per connection doing blocking reads. There is no
/// synchronisation context here and nothing to gain from async, while <c>ConfigureAwait</c>
/// discipline under <c>AnalysisLevel=latest-All</c> would be pure cost.
/// </para>
/// </summary>
internal sealed class PipeServer : IDisposable
{
    /// <summary>The pipe's name. The full path is <c>\\.\pipe\ComfyTrayGuard</c>.</summary>
    public const string PipeName = "ComfyTrayGuard";

    /// <summary>
    /// Concurrent pipe instances. One per possible session, plus headroom for the short-lived
    /// connections the tray makes to probe status while rendering its menu.
    /// </summary>
    private const int MaxInstances = GuardSessionManager.MaxSessions + 2;

    private readonly GuardRequestDispatcher _dispatcher;
    private readonly GuardEventLog _log;
    private readonly CancellationTokenSource _stopping = new();
    private Thread? _acceptThread;

    public PipeServer(GuardRequestDispatcher dispatcher, GuardEventLog log)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public void Start()
    {
        _acceptThread = new Thread(AcceptLoop)
        {
            IsBackground = true,
            Name = "ComfyTrayGuard pipe",
        };
        _acceptThread.Start();
    }

    public void Stop()
    {
        _stopping.Cancel();

        // Unblock the accept loop, which is parked in WaitForConnection, by connecting to it.
        NudgeAcceptLoop();

        _acceptThread?.Join(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// The pipe's access control.
    ///
    /// <para>
    /// The explicit deny for <c>NETWORK</c> is the important line: named pipes are reachable over
    /// SMB, so without it this service would be listening to the network rather than to the
    /// machine. <c>INTERACTIVE</c> means logged on at the console or over RDP, which is exactly
    /// the set of people who can run the tray. Neither <c>ChangePermissions</c> nor
    /// <c>TakeOwnership</c> is granted to anyone but SYSTEM and the administrators group.
    /// </para>
    /// </summary>
    private static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Deny));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            AccessControlType.Allow));

        return security;
    }

    [SuppressMessage(
        "Design",
        "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "The accept loop is the service's heart; no single failed connection may stop it.")]
    private void AcceptLoop()
    {
        _log.Info($@"listening on \\.\pipe\{PipeName}");

        while (!_stopping.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(
                    PipeName,
                    PipeDirection.InOut,
                    MaxInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.WriteThrough,
                    inBufferSize: 16 * 1024,
                    outBufferSize: 16 * 1024,
                    CreateSecurity());

                pipe.WaitForConnection();

                if (_stopping.IsCancellationRequested)
                {
                    pipe.Dispose();
                    return;
                }

                var connected = pipe;
                pipe = null;

                var worker = new Thread(() => HandleConnection(connected))
                {
                    IsBackground = true,
                    Name = "ComfyTrayGuard connection",
                };
                worker.Start();
            }
            catch (Exception ex)
            {
                pipe?.Dispose();

                if (_stopping.IsCancellationRequested)
                {
                    return;
                }

                _log.Error($"pipe accept failed: {ex.Message}");

                // Back off rather than spin: if the pipe cannot be created at all, a tight loop
                // would fill the event log faster than anyone could read it.
                Thread.Sleep(TimeSpan.FromSeconds(2));
            }
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "A misbehaving client must lose its own connection and nothing else.")]
    private void HandleConnection(NamedPipeServerStream pipe)
    {
        var connection = new GuardConnection { ClientDescription = Describe(pipe) };
        _log.Info($"connection opened by {connection.ClientDescription}");

        try
        {
            var reader = new FrameReader(pipe);

            while (reader.ReadLine() is { } line)
            {
                MessageFraming.Write(pipe, MessageFraming.Serialize(Respond(connection, line)));
            }
        }
        catch (Exception ex)
        {
            _log.Info($"connection from {connection.ClientDescription} ended: {ex.Message}");
        }
        finally
        {
            // The primary teardown path. It is driven by the kernel closing the pipe, so it runs
            // even when the tray died without a word — a crash, a kill, a logoff.
            var removed = _dispatcher.Close(connection, SessionEndReason.Disconnected);
            if (removed > 0)
            {
                _log.Warning(
                    $"connection from {connection.ClientDescription} dropped; " +
                    $"removed {removed} firewall rule(s)");
            }

            pipe.Dispose();
        }
    }

    private GuardResponse Respond(GuardConnection connection, string line)
    {
        try
        {
            return _dispatcher.Dispatch(connection, MessageFraming.DeserializeRequest(line));
        }
        catch (JsonException ex)
        {
            // A malformed message is the client's problem, not grounds for dropping a connection
            // that may be holding rules.
            _log.Warning($"malformed request from {connection.ClientDescription}: {ex.Message}");
            return new ErrorResponse("MalformedRequest", ex.Message);
        }
    }

    /// <summary>
    /// Describes the caller for the log: the account it runs as, and the program it is.
    ///
    /// <para>
    /// This is an audit record, not a security control, and it is worth being blunt about why.
    /// The user owns the process at the other end of this pipe — they can rename it, replace it
    /// in their own profile, attach a debugger to it or inject into it. No check performed here
    /// can survive that. What makes the guard safe is that its protocol cannot express "allow";
    /// what this gives is a name in the log when somebody has to work out what happened.
    /// </para>
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "Identifying the caller is diagnostic; failing to must not refuse the connection.")]
    private static string Describe(NamedPipeServerStream pipe)
    {
        string account;
        try
        {
            account = pipe.GetImpersonationUserName();
        }
        catch (Exception)
        {
            account = "unknown account";
        }

        try
        {
            if (!NativeMethods.TryGetClientProcessId(pipe.SafePipeHandle, out var pid))
            {
                return account;
            }

            using var process = Process.GetProcessById((int)pid);
            var image = process.MainModule?.FileName ?? process.ProcessName;
            return $"{account} ({image}, pid {pid})";
        }
        catch (Exception)
        {
            return account;
        }
    }

    /// <summary>
    /// Wakes the accept loop so it can notice it has been asked to stop.
    /// <c>WaitForConnection</c> has no cancellation, so the only way out is a connection.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "Best-effort shutdown nudge; the join below has a timeout regardless.")]
    private static void NudgeAcceptLoop()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            client.Connect(1000);
        }
        catch (Exception)
        {
            // Nothing listening, which is the state we wanted anyway.
        }
    }

    public void Dispose()
    {
        _stopping.Dispose();
    }
}
