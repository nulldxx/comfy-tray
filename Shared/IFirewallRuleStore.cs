using System;
using System.Collections.Generic;

namespace ComfyTray;

/// <summary>
/// The guard's whole view of Windows Firewall, narrowed to what it is allowed to do.
///
/// <para>
/// <b>Every method here either creates a block rule or removes one of our own.</b> There is no
/// way to express an allow rule, to widen an existing one, or to touch a rule the guard did
/// not create. That is not an oversight to be filled in later: the guard runs as LocalSystem
/// and takes requests from a pipe any interactive user can reach, and this interface is the
/// reason that is acceptable. A caller who fully controls the protocol still cannot obtain
/// network access the machine did not already grant them — the worst they can do is remove
/// blocking, which returns the machine to its normal state.
/// </para>
///
/// <para>
/// Implementations talk COM and are therefore Windows-only and untestable off-box; everything
/// above this line is pure and is not. That split is deliberate — see
/// <see cref="FirewallPlan"/>.
/// </para>
/// </summary>
internal interface IFirewallRuleStore
{
    /// <summary>
    /// Every rule the guard owns, across all sessions. Ownership is decided by both the name
    /// prefix and the grouping; anything else on the machine is invisible here.
    /// </summary>
    IReadOnlyList<GuardRuleRecord> ListGuardRules();

    /// <summary>
    /// Creates an outbound block rule for <paramref name="rule"/>. Idempotent: re-adding a rule
    /// that already exists leaves the existing one alone.
    /// </summary>
    void Add(GuardRuleRecord rule);

    /// <summary>
    /// Removes a rule by name. Silently does nothing when the name is absent or is not one of
    /// ours, so cleanup can be run speculatively.
    /// </summary>
    void Remove(string ruleName);

    /// <summary>Reports whether the firewall is in a state where the rules will actually bite.</summary>
    FirewallHealth GetHealth();
}
