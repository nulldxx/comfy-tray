using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace ComfyTray;

/// <summary>
/// <see cref="IFirewallRuleStore"/> over the Windows Firewall COM API.
///
/// <para>
/// COM rather than shelling out to <c>netsh</c>, for one decisive reason: cleanup.
/// <c>netsh advfirewall firewall delete rule</c> filters on name, program, port and profile but
/// not on group, so "delete everything this service ever created" would need a list of rule
/// names persisted somewhere — and that list is exactly what goes stale when the service dies
/// unexpectedly, which is the case the cleanup exists to handle. Enumerating the live policy
/// needs no state and cannot drift.
/// </para>
///
/// <para>
/// All COM access is serialised through <see cref="_gate"/>. Every thread the guard creates is
/// MTA (the .NET default), so the objects can be shared across them without apartment
/// marshalling; the lock is there to keep concurrent sessions from interleaving reads and
/// writes of the same policy object.
/// </para>
/// </summary>
internal sealed class ComFirewallRuleStore : IFirewallRuleStore, IDisposable
{
    private const string PolicyProgId = "HNetCfg.FwPolicy2";
    private const string RuleProgId = "HNetCfg.FWRule";

    private readonly object _gate = new();
    private INetFwPolicy2? _policy;
    private bool _disposed;

    /// <summary>
    /// Connects to the local firewall policy. Throws when the Windows Firewall service is not
    /// running, which the caller should report rather than swallow — the guard cannot do its job
    /// without it.
    /// </summary>
    public ComFirewallRuleStore() => _policy = CreatePolicy();

    public IReadOnlyList<GuardRuleRecord> ListGuardRules()
    {
        lock (_gate)
        {
            var found = new List<GuardRuleRecord>();
            var rules = Policy.Rules;
            var enumerator = (IEnumVARIANT)rules.NewEnum;
            var buffer = new object[1];
            var fetched = Marshal.AllocCoTaskMem(sizeof(int));

            try
            {
                // Next returns S_OK (0) while it yields an item and S_FALSE (1) at the end.
                while (enumerator.Next(1, buffer, fetched) == 0)
                {
                    if (buffer[0] is not INetFwRule rule)
                    {
                        continue;
                    }

                    try
                    {
                        if (TryDescribe(rule, out var record))
                        {
                            found.Add(record);
                        }
                    }
                    finally
                    {
                        Release(rule);
                    }
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(fetched);
                Release(enumerator);
            }

            return found;
        }
    }

    public void Add(GuardRuleRecord rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        lock (_gate)
        {
            var type = Type.GetTypeFromProgID(RuleProgId, throwOnError: true)!;
            var created = (INetFwRule)Activator.CreateInstance(type)!;

            try
            {
                // Windows Firewall validates and commits on every property set, and the API
                // documents that protocol must be established before anything port-shaped. There
                // are no ports here, but the order costs nothing and matches the guidance.
                created.Protocol = (int)NetFwIpProtocol.Any;
                created.Name = rule.RuleName;
                created.Description = GuardRuleNaming.DescribeFor(
                    rule.SessionId, rule.ImagePath, DateTimeOffset.UtcNow);
                created.ApplicationName = rule.ImagePath;
                created.Direction = NetFwRuleDirection.Out;
                created.Action = NetFwAction.Block;
                created.Profiles = (int)NetFwProfileType2.All;
                created.Grouping = GuardRuleNaming.Group;
                created.Enabled = true;

                Policy.Rules.Add(created);
            }
            finally
            {
                Release(created);
            }
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "Removal is speculative by design: cleanup runs over names that may already be gone.")]
    public void Remove(string ruleName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleName);

        // Only ever remove something we are certain is ours. The bulk cleanup paths call this
        // over whatever ListGuardRules returned, but a caller could pass anything, and deleting
        // a rule the guard did not create would be unforgivable.
        if (!GuardRuleNaming.TryParse(ruleName, out _))
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                Policy.Rules.Remove(ruleName);
            }
            catch (Exception)
            {
                // Remove throws when the name is absent. That is the desired end state, so a
                // second teardown pass over the same rules is a no-op rather than an error.
            }
        }
    }

    /// <summary>
    /// Reports whether the firewall will actually act on the rules we create. Both questions it
    /// asks are invisible failures otherwise: a rule added while the firewall is off is accepted
    /// and enforces nothing, and one added where group policy owns the profile is ignored
    /// outright.
    /// </summary>
    public FirewallHealth GetHealth()
    {
        lock (_gate)
        {
            return new FirewallHealth(
                DomainProfileEnabled: IsEnabled(NetFwProfileType2.Domain),
                PrivateProfileEnabled: IsEnabled(NetFwProfileType2.Private),
                PublicProfileEnabled: IsEnabled(NetFwProfileType2.Public),
                LocalRulesApply: LocalRulesApply());
        }
    }

    private INetFwPolicy2 Policy =>
        _policy ?? throw new ObjectDisposedException(nameof(ComFirewallRuleStore));

    private static INetFwPolicy2 CreatePolicy()
    {
        // Activate by ProgID so the coclass CLSID never has to be hard-coded; the cast performs
        // the QueryInterface for INetFwPolicy2's IID, which is declared on the interface.
        var type = Type.GetTypeFromProgID(PolicyProgId, throwOnError: true)!;
        return (INetFwPolicy2)Activator.CreateInstance(type)!;
    }

    /// <summary>
    /// Decides whether an arbitrary firewall rule is one of ours, and reads back what it blocks.
    /// Ownership needs <b>both</b> the name prefix and the grouping — either alone could plausibly
    /// belong to something else.
    /// </summary>
    private static bool TryDescribe(INetFwRule rule, [NotNullWhen(true)] out GuardRuleRecord? record)
    {
        record = null;

        if (!GuardRuleNaming.TryParse(rule.Name, out var sessionId))
        {
            return false;
        }

        if (!string.Equals(rule.Grouping, GuardRuleNaming.Group, StringComparison.Ordinal))
        {
            return false;
        }

        record = new GuardRuleRecord(
            rule.Name, sessionId, GuardPathSet.Normalize(rule.ApplicationName));
        return true;
    }

    /// <summary>
    /// <c>FirewallEnabled</c> is a parameterised property, which C# cannot express as a named
    /// indexer on an interface, so it is read through IDispatch by name instead.
    /// </summary>
    private bool IsEnabled(NetFwProfileType2 profile) =>
        InvokeOnPolicy("FirewallEnabled", profile) is bool enabled && enabled;

    private bool LocalRulesApply()
    {
        // Same parameterised-property shape as FirewallEnabled. Group policy taking over a
        // profile is reported as GP_OVERRIDE, and means locally created rules are stored but
        // never evaluated.
        foreach (var profile in new[]
                 {
                     NetFwProfileType2.Domain, NetFwProfileType2.Private, NetFwProfileType2.Public,
                 })
        {
            if (InvokeOnPolicy("LocalPolicyModifyState", profile) is int state &&
                state == (int)NetFwModifyState.GroupPolicyOverride)
            {
                return false;
            }
        }

        return true;
    }

    private object? InvokeOnPolicy(string member, NetFwProfileType2 profile) =>
        Policy.GetType().InvokeMember(
            member,
            BindingFlags.GetProperty,
            binder: null,
            target: Policy,
            args: [(int)profile],
            culture: CultureInfo.InvariantCulture);

    /// <summary>
    /// Releases a runtime callable wrapper. The enumeration path creates one per rule, and a
    /// long-running service that leaked them would accumulate COM proxies for its whole uptime.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "Releasing a wrapper is best-effort cleanup; a failure here must not mask the caller's work.")]
    private static void Release(object? comObject)
    {
        if (comObject is null || !Marshal.IsComObject(comObject))
        {
            return;
        }

        try
        {
            Marshal.FinalReleaseComObject(comObject);
        }
        catch (Exception)
        {
            // Nothing useful to do, and nothing depends on it having happened.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            Release(_policy);
            _policy = null;
            _disposed = true;
        }
    }
}
