using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace ComfyTray.Tests;

/// <summary>
/// Tests for the wire protocol. The round-trips matter, but the assertion that earns its keep is
/// <see cref="NoRequestCanGrantNetworkAccess"/>.
/// </summary>
public sealed class GuardProtocolTests
{
    private static readonly Guid Session = Guid.Parse("3f2a1b4c-5d6e-4f70-8192-a3b4c5d6e7f8");

    private static T RoundTripRequest<T>(T request) where T : GuardRequest =>
        Assert.IsType<T>(MessageFraming.DeserializeRequest(MessageFraming.Serialize(request)));

    private static T RoundTripResponse<T>(T response) where T : GuardResponse =>
        Assert.IsType<T>(MessageFraming.DeserializeResponse(MessageFraming.Serialize(response)));

    [Fact]
    public void HelloRoundTrips() =>
        Assert.Equal(7, RoundTripRequest(new HelloRequest(7)).ProtocolVersion);

    [Fact]
    public void BeginSessionRoundTrips()
    {
        var result = RoundTripRequest(new BeginSessionRequest("1.2.3", 4242, 60) { Id = 9 });

        Assert.Equal("1.2.3", result.ClientVersion);
        Assert.Equal(4242, result.ComfyPid);
        Assert.Equal(60, result.LeaseSeconds);
        Assert.Equal(9, result.Id);
    }

    [Fact]
    public void BlockImagesRoundTrips()
    {
        var result = RoundTripRequest(
            new BlockImagesRequest(Session, [@"c:\a\python.exe", @"c:\b\git.exe"]));

        Assert.Equal(Session, result.SessionId);
        Assert.Equal([@"c:\a\python.exe", @"c:\b\git.exe"], result.Paths);
    }

    [Theory]
    [InlineData(typeof(UnlockRequest))]
    [InlineData(typeof(RearmRequest))]
    [InlineData(typeof(HeartbeatRequest))]
    [InlineData(typeof(EndSessionRequest))]
    [InlineData(typeof(StatusRequest))]
    public void EveryRequestTypeIsRegistered(Type requestType) =>
        Assert.Contains(
            requestType,
            typeof(GuardRequest).GetCustomAttributes<JsonDerivedTypeAttribute>()
                .Select(a => a.DerivedType));

    [Fact]
    public void HeartbeatResponseCarriesLogLinesBack()
    {
        var result = RoundTripResponse(
            new HeartbeatResponse(DateTimeOffset.UnixEpoch, true, ["one", "two"]));

        Assert.True(result.Unlocked);
        Assert.Equal(["one", "two"], result.LogLines);
    }

    [Fact]
    public void HelloResponseCarriesFirewallHealth()
    {
        var result = RoundTripResponse(
            new HelloResponse(1, "0.2.0", new FirewallHealth(true, false, true, true)));

        Assert.False(result.Health.PrivateProfileEnabled);
        Assert.NotNull(result.Health.DegradedReason);
    }

    [Fact]
    public void ErrorResponseRoundTrips()
    {
        var result = RoundTripResponse(new ErrorResponse("UnknownSession", "gone") { Id = 3 });

        Assert.Equal("UnknownSession", result.Code);
        Assert.Equal(3, result.Id);
    }

    /// <summary>An unsolicited notification has no request to correlate with.</summary>
    [Fact]
    public void LogNotificationHasNoCorrelationId() =>
        Assert.Equal(0, RoundTripResponse(new LogNotification("hello")).Id);

    [Fact]
    public void UnknownDiscriminatorIsRejected() =>
        Assert.Throws<JsonException>(() =>
            MessageFraming.DeserializeRequest("""{"$type":"deleteEverything"}"""));

    [Fact]
    public void MalformedJsonIsRejected() =>
        Assert.Throws<JsonException>(() => MessageFraming.DeserializeRequest("{not json"));

    /// <summary>
    /// The guard runs as LocalSystem and takes requests from a pipe any interactive user can
    /// reach. What makes that acceptable is not the authorisation checks — a user owns the
    /// process at the other end — but that the protocol has no way to express "allow". Its whole
    /// vocabulary is "block these executables in my session" and "undo what you did for me", so
    /// the worst a hostile caller achieves is removing blocking, which returns the machine to
    /// its normal state.
    ///
    /// <para>
    /// This test is a tripwire on that property. A request type that gains a field naming a rule,
    /// an action, a direction, a port, an address or a profile is the shape of an escalation, and
    /// should have to argue with a failing test before it lands.
    /// </para>
    /// </summary>
    [Fact]
    public void NoRequestCanGrantNetworkAccess()
    {
        string[] escalationShaped =
        [
            "action", "allow", "permit", "grant", "direction", "port",
            "address", "profile", "rulename", "enabled", "exempt",
        ];

        var requestTypes = typeof(GuardRequest)
            .GetCustomAttributes<JsonDerivedTypeAttribute>()
            .Select(a => a.DerivedType)
            .ToList();

        Assert.NotEmpty(requestTypes);

        var offenders =
            (from type in requestTypes
             from property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
             from token in escalationShaped
             where property.Name.Contains(token, StringComparison.OrdinalIgnoreCase)
             select $"{type.Name}.{property.Name} looks like '{token}'").ToList();

        Assert.True(
            offenders.Count == 0,
            "The guard's protocol must not be able to grant network access. Offending members:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }
}
