using System;
using System.Collections.Generic;
using System.Linq;

namespace ComfyTray.Tests;

/// <summary>
/// An in-memory <see cref="IFirewallRuleStore"/>. Everything the guard does to a machine's
/// firewall goes through this interface, so substituting it makes the session logic — the part
/// where a bug leaves someone's Python silently offline — testable on any platform.
/// </summary>
internal sealed class FakeFirewallRuleStore : IFirewallRuleStore
{
    private readonly Dictionary<string, GuardRuleRecord> _rules = new(StringComparer.Ordinal);

    /// <summary>What <see cref="GetHealth"/> reports. Defaults to a fully effective firewall.</summary>
    public FirewallHealth Health { get; set; } = new(true, true, true, true);

    /// <summary>When set, <see cref="Add"/> throws for this path, standing in for a COM failure.</summary>
    public string? FailAddingPath { get; set; }

    /// <summary>
    /// Paths currently blocked, sorted ordinally so assertions are deterministic rather than
    /// dependent on dictionary iteration order.
    /// </summary>
    public IReadOnlyList<string> BlockedPaths =>
        _rules.Values.Select(r => r.ImagePath).OrderBy(p => p, StringComparer.Ordinal).ToList();

    public IReadOnlyList<GuardRuleRecord> ListGuardRules() => _rules.Values.ToList();

    public void Add(GuardRuleRecord rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (string.Equals(rule.ImagePath, FailAddingPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Simulated failure adding {rule.ImagePath}.");
        }

        _rules[rule.RuleName] = rule;
    }

    public void Remove(string ruleName) => _rules.Remove(ruleName);

    public FirewallHealth GetHealth() => Health;

    /// <summary>Paths blocked on behalf of one session.</summary>
    public IReadOnlyList<string> BlockedPathsFor(Guid sessionId) =>
        _rules.Values.Where(r => r.SessionId == sessionId)
                     .Select(r => r.ImagePath)
                     .OrderBy(p => p, StringComparer.Ordinal)
                     .ToList();

    /// <summary>Plants a rule that is not the guard's, to prove cleanup leaves it alone.</summary>
    public void AddForeignRule(string ruleName) =>
        _rules[ruleName] = new GuardRuleRecord(ruleName, Guid.Empty, @"c:\other\thing.exe");
}
