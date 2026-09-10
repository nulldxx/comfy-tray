using System;
using System.Linq;
using Xunit;

namespace ComfyTray.Tests;

/// <summary>
/// Tests for <see cref="GuardRequestDispatcher"/> — the guard's request handling, exercised
/// without a pipe. The cross-connection checks are the ones that matter: session ids travel on
/// the wire, and every interactive user on the machine can open a connection.
/// </summary>
public sealed class GuardRequestDispatcherTests
{
    private const string Python = @"c:\comfyui\.venv\scripts\python.exe";

    private readonly FakeFirewallRuleStore _store = new();
    private DateTimeOffset _now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
    private readonly GuardSessionManager _sessions;
    private readonly GuardRequestDispatcher _dispatcher;

    public GuardRequestDispatcherTests()
    {
        _sessions = new GuardSessionManager(_store, () => _now);
        _dispatcher = new GuardRequestDispatcher(_sessions, _store, "0.2.0");
    }

    private static GuardConnection NewConnection() => new() { ClientDescription = "test" };

    private T Send<T>(GuardConnection connection, GuardRequest request) where T : GuardResponse =>
        Assert.IsType<T>(_dispatcher.Dispatch(connection, request));

    /// <summary>Opens a connection that has shaken hands and holds a session.</summary>
    private (GuardConnection Connection, Guid SessionId) Established(int pid = 1234)
    {
        var connection = NewConnection();
        Send<HelloResponse>(connection, new HelloRequest(GuardProtocolVersion.Current));
        var session = Send<BeginSessionResponse>(
            connection, new BeginSessionRequest("1.0.0", pid, 60));
        return (connection, session.SessionId);
    }

    [Fact]
    public void HelloAgreesTheProtocolAndReportsHealth()
    {
        var response = Send<HelloResponse>(
            NewConnection(), new HelloRequest(GuardProtocolVersion.Current));

        Assert.Equal(GuardProtocolVersion.Current, response.ProtocolVersion);
        Assert.Equal("0.2.0", response.ServiceVersion);
        Assert.True(response.Health.IsFullyEffective);
    }

    [Fact]
    public void HelloRejectsAMismatchedProtocol()
    {
        var response = Send<ErrorResponse>(
            NewConnection(), new HelloRequest(GuardProtocolVersion.Current + 1));

        Assert.Equal("ProtocolMismatch", response.Code);
    }

    [Fact]
    public void ResponsesEchoTheRequestId()
    {
        var response = _dispatcher.Dispatch(
            NewConnection(), new HelloRequest(GuardProtocolVersion.Current) { Id = 77 });

        Assert.Equal(77, response.Id);
    }

    [Fact]
    public void ErrorsEchoTheRequestIdToo()
    {
        var response = _dispatcher.Dispatch(NewConnection(), new HeartbeatRequest(Guid.NewGuid()) { Id = 5 });

        Assert.Equal(5, response.Id);
        Assert.IsType<ErrorResponse>(response);
    }

    [Fact]
    public void SessionRequiresAHandshakeFirst()
    {
        var response = Send<ErrorResponse>(
            NewConnection(), new BeginSessionRequest("1.0.0", 1, 60));

        Assert.Equal("HandshakeRequired", response.Code);
    }

    [Fact]
    public void OneSessionPerConnection()
    {
        var (connection, _) = Established();

        var response = Send<ErrorResponse>(connection, new BeginSessionRequest("1.0.0", 2, 60));

        Assert.Equal("SessionAlreadyOpen", response.Code);
    }

    /// <summary>
    /// The status of the machine is readable without a handshake or a session — the tray asks
    /// for it to render its menu, before it has any reason to open one.
    /// </summary>
    [Fact]
    public void StatusNeedsNoHandshake()
    {
        var response = Send<StatusResponse>(NewConnection(), new StatusRequest());

        Assert.Empty(response.Sessions);
        Assert.True(response.Health.IsFullyEffective);
    }

    [Fact]
    public void BlockingCreatesRules()
    {
        var (connection, session) = Established();

        var response = Send<BlockImagesResponse>(
            connection, new BlockImagesRequest(session, [Python]));

        Assert.Equal([Python], response.Added);
        Assert.Equal([Python], _store.BlockedPaths);
    }

    [Fact]
    public void BlockingWithNoPathsIsHarmless()
    {
        var (connection, session) = Established();

        var response = Send<BlockImagesResponse>(
            connection, new BlockImagesRequest(session, []));

        Assert.Empty(response.Added);
    }

    [Fact]
    public void EndSessionRemovesTheRulesAndReleasesTheConnection()
    {
        var (connection, session) = Established();
        Send<BlockImagesResponse>(connection, new BlockImagesRequest(session, [Python]));

        var response = Send<EndSessionResponse>(connection, new EndSessionRequest(session));

        Assert.Equal(1, response.RulesRemoved);
        Assert.Empty(_store.BlockedPaths);
        Assert.Null(connection.SessionId);
    }

    /// <summary>
    /// The primary teardown path. It is driven by the kernel closing the pipe, so it fires even
    /// when the client has gone without saying anything — a crash, a kill, a logoff.
    /// </summary>
    [Fact]
    public void ClosingTheConnectionTearsTheSessionDown()
    {
        var (connection, session) = Established();
        Send<BlockImagesResponse>(connection, new BlockImagesRequest(session, [Python]));

        Assert.Equal(1, _dispatcher.Close(connection, SessionEndReason.Disconnected));

        Assert.Empty(_store.BlockedPaths);
        Assert.Empty(_sessions.GetSummaries());
    }

    [Fact]
    public void ClosingAConnectionWithNoSessionIsHarmless() =>
        Assert.Equal(0, _dispatcher.Close(NewConnection(), SessionEndReason.Disconnected));

    // ---------------------------------------------------------------------------------------
    // Cross-connection isolation. Every interactive user on the machine can open a connection to
    // this pipe, and session ids are visible on the wire. A connection naming somebody else's
    // session must get nowhere, or one user could lift or tear down another's blocking.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void AConnectionCannotBlockOnAnotherSession()
    {
        var (_, victim) = Established(pid: 1);
        var attacker = NewConnection();
        Send<HelloResponse>(attacker, new HelloRequest(GuardProtocolVersion.Current));

        var response = Send<ErrorResponse>(attacker, new BlockImagesRequest(victim, [Python]));

        Assert.Equal("UnknownSession", response.Code);
    }

    [Fact]
    public void AConnectionCannotEndAnotherSession()
    {
        var (owner, victim) = Established(pid: 1);
        Send<BlockImagesResponse>(owner, new BlockImagesRequest(victim, [Python]));

        var (attacker, _) = Established(pid: 2);
        var response = Send<ErrorResponse>(attacker, new EndSessionRequest(victim));

        Assert.Equal("UnknownSession", response.Code);
        Assert.Equal([Python], _store.BlockedPaths);
    }

    /// <summary>
    /// Lifting somebody else's blocking is the most valuable thing an attacker could do here, so
    /// it gets its own test rather than being folded into the others.
    /// </summary>
    [Fact]
    public void AConnectionCannotUnlockAnotherSession()
    {
        var (owner, victim) = Established(pid: 1);
        Send<BlockImagesResponse>(owner, new BlockImagesRequest(victim, [Python]));

        var (attacker, _) = Established(pid: 2);
        var response = Send<ErrorResponse>(attacker, new UnlockRequest(victim, 600));

        Assert.Equal("UnknownSession", response.Code);
        Assert.Equal([Python], _store.BlockedPaths);
    }

    [Fact]
    public void AConnectionCannotHeartbeatAnotherSessionAlive()
    {
        var (_, victim) = Established(pid: 1);
        var (attacker, _) = Established(pid: 2);

        // The victim's owner stops sending heartbeats; a third party trying to keep it alive
        // must not be able to, or the lease watchdog could be defeated from outside.
        for (var i = 0; i < 5; i++)
        {
            _now = _now.AddSeconds(30);
            Send<ErrorResponse>(attacker, new HeartbeatRequest(victim));
            _sessions.Tick();
        }

        Assert.DoesNotContain(_sessions.GetSummaries(), s => s.SessionId == victim);
    }

    [Fact]
    public void UnlockAndRearmRoundTripOnTheOwningConnection()
    {
        var (connection, session) = Established();
        Send<BlockImagesResponse>(connection, new BlockImagesRequest(session, [Python]));

        var unlock = Send<UnlockResponse>(connection, new UnlockRequest(session, 600));
        Assert.Equal(_now.AddSeconds(600), unlock.ExpiresUtc);
        Assert.Empty(_store.BlockedPaths);

        Assert.Equal(1, Send<RearmResponse>(connection, new RearmRequest(session)).RulesRestored);
        Assert.Equal([Python], _store.BlockedPaths);
    }

    [Fact]
    public void HeartbeatCarriesTheGuardsLogLinesBack()
    {
        var (connection, session) = Established();
        Send<BlockImagesResponse>(connection, new BlockImagesRequest(session, [Python]));

        var response = Send<HeartbeatResponse>(connection, new HeartbeatRequest(session));

        Assert.Contains(response.LogLines, line => line.Contains(Python, StringComparison.Ordinal));
    }

    [Fact]
    public void StatusReportsLiveSessions()
    {
        var (connection, session) = Established(pid: 4242);
        Send<BlockImagesResponse>(connection, new BlockImagesRequest(session, [Python]));

        var response = Send<StatusResponse>(connection, new StatusRequest());

        var summary = Assert.Single(response.Sessions);
        Assert.Equal(4242, summary.ComfyPid);
        Assert.Equal(1, summary.RuleCount);
    }

    [Fact]
    public void StatusReportsADegradedFirewall()
    {
        _store.Health = new FirewallHealth(true, true, true, LocalRulesApply: false);

        var response = Send<StatusResponse>(NewConnection(), new StatusRequest());

        Assert.False(response.Health.IsFullyEffective);
        Assert.Contains("group policy", response.Health.DegradedReason!, StringComparison.Ordinal);
    }
}
