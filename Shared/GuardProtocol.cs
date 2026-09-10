using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ComfyTray;

// The wire protocol between the tray and the guard service.
//
// ---------------------------------------------------------------------------------------------
// THE INVARIANT THAT MAKES THIS SAFE:
//
// The guard runs as LocalSystem and listens on a named pipe that any interactive user can reach.
// What keeps that from being a privilege-escalation route is not the authorisation checks — a
// user owns the process on the other end and can do as they like with it — but the fact that
// THERE IS NOTHING IN THIS FILE THAT CAN GRANT NETWORK ACCESS.
//
// No message carries a rule name, an action, a direction, a port, a remote address or a profile.
// The guard's entire vocabulary is "block these executables within my session" and "remove the
// things you created for me". A caller who fully controls the protocol still cannot obtain
// access the machine did not already grant them; the worst they can achieve is removing
// blocking, which returns the machine to its normal state.
//
// Any change that adds an allow-shaped message breaks that, whatever else it does.
// GuardProtocolTests.NoRequestCanGrantNetworkAccess exists to make that hard to do by accident.
// ---------------------------------------------------------------------------------------------

/// <summary>Wire-format version. Bumped when a change is not backwards compatible.</summary>
internal static class GuardProtocolVersion
{
    public const int Current = 1;
}

/// <summary>Base of every message the tray sends the guard.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(HelloRequest), "hello")]
[JsonDerivedType(typeof(BeginSessionRequest), "beginSession")]
[JsonDerivedType(typeof(BlockImagesRequest), "blockImages")]
[JsonDerivedType(typeof(UnlockRequest), "unlock")]
[JsonDerivedType(typeof(RearmRequest), "rearm")]
[JsonDerivedType(typeof(HeartbeatRequest), "heartbeat")]
[JsonDerivedType(typeof(EndSessionRequest), "endSession")]
[JsonDerivedType(typeof(StatusRequest), "status")]
internal abstract record GuardRequest
{
    /// <summary>Correlation id echoed by the matching response.</summary>
    public long Id { get; init; }
}

/// <summary>Opens the conversation and agrees a protocol version.</summary>
internal sealed record HelloRequest(int ProtocolVersion) : GuardRequest;

/// <summary>Claims a session. Its rules live exactly as long as it does.</summary>
internal sealed record BeginSessionRequest(string ClientVersion, int ComfyPid, int LeaseSeconds)
    : GuardRequest;

/// <summary>Asks for outbound blocking on these executables. Idempotent.</summary>
internal sealed record BlockImagesRequest(Guid SessionId, IReadOnlyList<string> Paths)
    : GuardRequest;

/// <summary>Temporarily removes this session's rules, e.g. to let ComfyUI-Manager update.</summary>
internal sealed record UnlockRequest(Guid SessionId, int Seconds) : GuardRequest;

/// <summary>Ends an unlock early, restoring every path the session knows about.</summary>
internal sealed record RearmRequest(Guid SessionId) : GuardRequest;

/// <summary>Extends the lease. A session that stops sending these is torn down.</summary>
internal sealed record HeartbeatRequest(Guid SessionId) : GuardRequest;

/// <summary>Gives up the session and its rules.</summary>
internal sealed record EndSessionRequest(Guid SessionId) : GuardRequest;

/// <summary>Reports what the guard is doing. Allowed without a session.</summary>
internal sealed record StatusRequest : GuardRequest;

/// <summary>Base of every message the guard sends the tray.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(HelloResponse), "hello")]
[JsonDerivedType(typeof(BeginSessionResponse), "beginSession")]
[JsonDerivedType(typeof(BlockImagesResponse), "blockImages")]
[JsonDerivedType(typeof(UnlockResponse), "unlock")]
[JsonDerivedType(typeof(RearmResponse), "rearm")]
[JsonDerivedType(typeof(HeartbeatResponse), "heartbeat")]
[JsonDerivedType(typeof(EndSessionResponse), "endSession")]
[JsonDerivedType(typeof(StatusResponse), "status")]
[JsonDerivedType(typeof(ErrorResponse), "error")]
[JsonDerivedType(typeof(LogNotification), "log")]
internal abstract record GuardResponse
{
    /// <summary>The <see cref="GuardRequest.Id"/> this answers. Zero for an unsolicited message.</summary>
    public long Id { get; init; }
}

internal sealed record HelloResponse(int ProtocolVersion, string ServiceVersion, FirewallHealth Health)
    : GuardResponse;

internal sealed record BeginSessionResponse(Guid SessionId, int GrantedLeaseSeconds)
    : GuardResponse;

/// <summary>
/// What became of a block request. <paramref name="Rejected"/> carries paths refused because the
/// session hit its rule cap, so the tray can say so rather than quietly blocking less than the
/// user believes.
/// </summary>
internal sealed record BlockImagesResponse(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> AlreadyBlocked,
    IReadOnlyList<string> Rejected) : GuardResponse;

internal sealed record UnlockResponse(DateTimeOffset ExpiresUtc) : GuardResponse;

internal sealed record RearmResponse(int RulesRestored) : GuardResponse;

/// <summary>
/// Carries the guard's log lines back to the tray so they appear in the Logs window. They ride
/// the heartbeat rather than a second channel: one pipe, one ordering, nothing to demultiplex.
/// </summary>
internal sealed record HeartbeatResponse(
    DateTimeOffset LeaseExpiresUtc,
    bool Unlocked,
    IReadOnlyList<string> LogLines) : GuardResponse;

internal sealed record EndSessionResponse(int RulesRemoved) : GuardResponse;

internal sealed record StatusResponse(
    IReadOnlyList<GuardSessionSummary> Sessions,
    FirewallHealth Health) : GuardResponse;

internal sealed record ErrorResponse(string Code, string Message) : GuardResponse;

/// <summary>An unsolicited log line. <see cref="GuardResponse.Id"/> is zero.</summary>
internal sealed record LogNotification(string Text) : GuardResponse;

/// <summary>One live session, as reported by <see cref="StatusRequest"/>.</summary>
internal sealed record GuardSessionSummary(
    Guid SessionId,
    int ComfyPid,
    int RuleCount,
    bool Unlocked,
    DateTimeOffset LeaseExpiresUtc);
