using System;
using System.Collections.Generic;

namespace ComfyTray;

/// <summary>
/// Per-connection state. A session belongs to the connection that opened it, and dies with it.
/// </summary>
internal sealed class GuardConnection
{
    /// <summary>How the caller identified itself, for the log. Never used to make a decision.</summary>
    public string ClientDescription { get; init; } = "unknown";

    /// <summary>Set once <see cref="HelloRequest"/> has agreed a protocol version.</summary>
    public bool HelloCompleted { get; set; }

    /// <summary>The session this connection owns, if it has opened one.</summary>
    public Guid? SessionId { get; set; }
}

/// <summary>
/// Turns a request into a response. All of the guard's actual request handling lives here rather
/// than in the pipe server, so that it can be tested without a pipe.
///
/// <para>
/// The rule worth stating plainly: <b>a connection may only act on the session it opened.</b>
/// Session ids travel on the wire, so without this check any interactive user could name another
/// user's session and tear down their blocking, or lift it. The id in the message is checked
/// against the connection's own rather than trusted.
/// </para>
/// </summary>
internal sealed class GuardRequestDispatcher(
    GuardSessionManager sessions,
    IFirewallRuleStore store,
    string serviceVersion)
{
    private readonly GuardSessionManager _sessions =
        sessions ?? throw new ArgumentNullException(nameof(sessions));

    private readonly IFirewallRuleStore _store =
        store ?? throw new ArgumentNullException(nameof(store));

    private readonly string _serviceVersion =
        serviceVersion ?? throw new ArgumentNullException(nameof(serviceVersion));

    /// <summary>
    /// Handles one request. Never throws for a caller's mistake — a bad request becomes an
    /// <see cref="ErrorResponse"/>, because a service running as LocalSystem should not drop a
    /// connection over a malformed message.
    /// </summary>
    public GuardResponse Dispatch(GuardConnection connection, GuardRequest request)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return Handle(connection, request) with { Id = request.Id };
        }
        catch (GuardRequestException ex)
        {
            return new ErrorResponse(ex.Code, ex.Message) { Id = request.Id };
        }
    }

    /// <summary>
    /// Ends whatever this connection was holding. Called when the pipe drops, which is the
    /// primary teardown path: it is driven by the kernel, so it fires even when the client is
    /// gone without having said anything.
    /// </summary>
    public int Close(GuardConnection connection, SessionEndReason reason)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (connection.SessionId is not { } sessionId)
        {
            return 0;
        }

        connection.SessionId = null;
        return _sessions.EndSession(sessionId, reason);
    }

    private GuardResponse Handle(GuardConnection connection, GuardRequest request) => request switch
    {
        HelloRequest hello => Hello(connection, hello),

        // Status is deliberately available without a handshake or a session: the tray asks for it
        // to render its menu, including before it has any reason to open a session.
        StatusRequest => new StatusResponse(_sessions.GetSummaries(), _store.GetHealth()),

        BeginSessionRequest begin => BeginSession(connection, begin),
        BlockImagesRequest block => BlockImages(connection, block),
        UnlockRequest unlock => Unlock(connection, unlock),
        RearmRequest rearm => new RearmResponse(_sessions.Rearm(Owned(connection, rearm.SessionId))),
        HeartbeatRequest heartbeat => Heartbeat(connection, heartbeat),
        EndSessionRequest end => EndSession(connection, end),

        _ => throw new GuardRequestException(
            "UnsupportedRequest", $"The guard does not handle {request.GetType().Name}."),
    };

    private GuardResponse Hello(GuardConnection connection, HelloRequest request)
    {
        if (request.ProtocolVersion != GuardProtocolVersion.Current)
        {
            throw new GuardRequestException(
                "ProtocolMismatch",
                $"The guard speaks protocol {GuardProtocolVersion.Current}, the client speaks " +
                $"{request.ProtocolVersion}. Install matching versions of the tray and the guard.");
        }

        connection.HelloCompleted = true;
        return new HelloResponse(GuardProtocolVersion.Current, _serviceVersion, _store.GetHealth());
    }

    private GuardResponse BeginSession(GuardConnection connection, BeginSessionRequest request)
    {
        if (!connection.HelloCompleted)
        {
            throw new GuardRequestException(
                "HandshakeRequired", "Send a hello before opening a session.");
        }

        if (connection.SessionId is not null)
        {
            throw new GuardRequestException(
                "SessionAlreadyOpen", "This connection already owns a session.");
        }

        var (sessionId, lease) = _sessions.BeginSession(
            request.ClientVersion, request.ComfyPid, request.LeaseSeconds);

        connection.SessionId = sessionId;
        return new BeginSessionResponse(sessionId, lease);
    }

    private GuardResponse BlockImages(GuardConnection connection, BlockImagesRequest request)
    {
        var (added, already, rejected) = _sessions.BlockImages(
            Owned(connection, request.SessionId), request.Paths ?? []);

        return new BlockImagesResponse(added, already, rejected);
    }

    private GuardResponse Unlock(GuardConnection connection, UnlockRequest request) =>
        new UnlockResponse(_sessions.Unlock(Owned(connection, request.SessionId), request.Seconds));

    private GuardResponse Heartbeat(GuardConnection connection, HeartbeatRequest request)
    {
        var (expires, unlocked, lines) = _sessions.Heartbeat(Owned(connection, request.SessionId));
        return new HeartbeatResponse(expires, unlocked, lines);
    }

    private GuardResponse EndSession(GuardConnection connection, EndSessionRequest request)
    {
        var sessionId = Owned(connection, request.SessionId);
        connection.SessionId = null;
        return new EndSessionResponse(_sessions.EndSession(sessionId, SessionEndReason.Requested));
    }

    /// <summary>
    /// Confirms the connection is talking about its own session, and returns it.
    ///
    /// <para>
    /// The failure is reported as an unknown session rather than as a refusal, deliberately: a
    /// caller probing for other people's session ids learns nothing from the answer.
    /// </para>
    /// </summary>
    private static Guid Owned(GuardConnection connection, Guid claimed)
    {
        if (connection.SessionId is { } owned && owned == claimed)
        {
            return owned;
        }

        throw new GuardRequestException(
            "UnknownSession", $"Session {claimed:N} is not open on this connection.");
    }
}
