using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ComfyTray.Tests;

/// <summary>
/// Tests for <see cref="FirewallPlan"/> — the diffing that decides what the guard actually
/// does to the machine's firewall.
/// </summary>
public sealed class FirewallPlanTests
{
    private static readonly Guid Session = Guid.Parse("3f2a1b4c-5d6e-4f70-8192-a3b4c5d6e7f8");
    private static readonly Guid OtherSession = Guid.Parse("11111111-2222-4333-8444-555555555555");

    private const string Python = @"c:\comfyui\.venv\scripts\python.exe";
    private const string Git = @"c:\program files\git\bin\git.exe";

    private static GuardRuleRecord Rule(Guid session, string path) =>
        new(GuardRuleNaming.Compose(session, path), session, path);

    [Fact]
    public void Compute_AddsEverythingWhenNothingExists()
    {
        var plan = FirewallPlan.Compute(Session, [Python, Git], []);

        Assert.Equal([Python, Git], plan.Adds.Select(a => a.ImagePath));
        Assert.Empty(plan.Removes);
    }

    /// <summary>
    /// Re-blocking a path the session already holds must be a no-op, because the tracker sends
    /// the full observed set on every tick.
    /// </summary>
    [Fact]
    public void Compute_IsIdempotent()
    {
        var plan = FirewallPlan.Compute(Session, [Python], [Rule(Session, Python)]);

        Assert.Empty(plan.Adds);
        Assert.Empty(plan.Removes);
    }

    [Fact]
    public void Compute_RemovesRulesNoLongerWanted()
    {
        var plan = FirewallPlan.Compute(Session, [Python], [Rule(Session, Python), Rule(Session, Git)]);

        Assert.Empty(plan.Adds);
        Assert.Equal([GuardRuleNaming.Compose(Session, Git)], plan.Removes);
    }

    /// <summary>
    /// Fast user switching is a first-class feature of the tray, so two logged-on users each
    /// running a session is expected rather than anomalous. Neither may disturb the other.
    /// </summary>
    [Fact]
    public void Compute_NeverTouchesAnotherSessionsRules()
    {
        var plan = FirewallPlan.Compute(
            Session, [Python], [Rule(OtherSession, Python), Rule(OtherSession, Git)]);

        Assert.Single(plan.Adds);
        Assert.Equal(Python, plan.Adds[0].ImagePath);
        Assert.Empty(plan.Removes);
    }

    [Fact]
    public void Compute_EmptyDesiredSetRemovesTheSessionsRules()
    {
        var plan = FirewallPlan.Compute(
            Session, [], [Rule(Session, Python), Rule(OtherSession, Git)]);

        Assert.Empty(plan.Adds);
        Assert.Equal([GuardRuleNaming.Compose(Session, Python)], plan.Removes);
    }

    [Fact]
    public void ApplyCap_AcceptsUpToTheLimit()
    {
        var incoming = Enumerable.Range(0, GuardPathSet.MaxRulesPerSession + 10)
            .Select(i => $@"c:\x\p{i}.exe")
            .ToList();

        var (accepted, rejected) = FirewallPlan.ApplyCap([], incoming);

        Assert.Equal(GuardPathSet.MaxRulesPerSession, accepted.Count);
        Assert.Equal(10, rejected.Count);
    }

    [Fact]
    public void ApplyCap_CountsRulesTheSessionAlreadyHolds()
    {
        var existing = Enumerable.Range(0, GuardPathSet.MaxRulesPerSession - 1)
            .Select(i => $@"c:\x\p{i}.exe")
            .ToList();

        var (accepted, rejected) = FirewallPlan.ApplyCap(existing, [@"c:\a.exe", @"c:\b.exe"]);

        Assert.Equal([@"c:\a.exe"], accepted);
        Assert.Equal([@"c:\b.exe"], rejected);
    }

    /// <summary>A path already blocked costs no budget — that is a repeat, not an overflow.</summary>
    [Fact]
    public void ApplyCap_IgnoresPathsAlreadyBlocked()
    {
        var existing = Enumerable.Range(0, GuardPathSet.MaxRulesPerSession)
            .Select(i => $@"c:\x\p{i}.exe")
            .ToList();

        var (accepted, rejected) = FirewallPlan.ApplyCap(existing, [existing[0], existing[1]]);

        Assert.Empty(accepted);
        Assert.Empty(rejected);
    }

    [Fact]
    public void ApplyCap_RejectsWhenTheSessionIsAlreadyFull()
    {
        var existing = Enumerable.Range(0, GuardPathSet.MaxRulesPerSession)
            .Select(i => $@"c:\x\p{i}.exe")
            .ToList();

        var (accepted, rejected) = FirewallPlan.ApplyCap(existing, [@"c:\new.exe"]);

        Assert.Empty(accepted);
        Assert.Equal([@"c:\new.exe"], rejected);
    }
}
