using System;
using Xunit;

namespace ComfyTray.Tests;

/// <summary>
/// Tests for the outbound submenu's presentation. The wording is the whole point: a user whose
/// blocking is not actually doing anything must be able to tell that from the menu, and the four
/// ways that can happen — no guard, a stopped guard, a switched-off firewall, and a deliberate
/// unlock — must not all read the same.
/// </summary>
public sealed class OutboundMenuStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly FirewallHealth Healthy = new(true, true, true, true);

    private static OutboundMenuState For(
        OutboundMode mode,
        GuardAvailability availability = GuardAvailability.Connected,
        bool hasSession = false,
        DateTimeOffset? unlockUntil = null,
        FirewallHealth? health = null) =>
        OutboundMenuState.For(mode, availability, hasSession, unlockUntil, Now, health ?? Healthy);

    [Fact]
    public void TheChosenModeCarriesTheTick()
    {
        Assert.Equal(OutboundMode.None, For(OutboundMode.None).Checked);
        Assert.Equal(OutboundMode.EnvironmentOnly, For(OutboundMode.EnvironmentOnly).Checked);
        Assert.Equal(OutboundMode.Firewall, For(OutboundMode.Firewall).Checked);
    }

    /// <summary>
    /// Left selectable deliberately: choosing it explains what is missing, whereas a greyed-out
    /// item leaves the user guessing why.
    /// </summary>
    [Fact]
    public void FirewallStaysSelectableWithoutTheGuard() =>
        Assert.True(For(OutboundMode.EnvironmentOnly, GuardAvailability.NotInstalled).FirewallEnabled);

    [Fact]
    public void ThereIsNothingToUnlockWithoutASession()
    {
        Assert.False(For(OutboundMode.Firewall, hasSession: false).UnlockEnabled);
        Assert.True(For(OutboundMode.Firewall, hasSession: true).UnlockEnabled);
    }

    [Fact]
    public void UnlockOffersADurationWhenBlockingIsInForce() =>
        Assert.Equal(
            "Allow network for 10 minutes",
            For(OutboundMode.Firewall, hasSession: true).UnlockHeader);

    [Fact]
    public void UnlockCountsDownWhileLifted()
    {
        var state = For(
            OutboundMode.Firewall, hasSession: true, unlockUntil: Now.AddMinutes(3).AddSeconds(25));

        Assert.Equal("Re-block now (3m 25s left)", state.UnlockHeader);
    }

    [Fact]
    public void CountdownDropsToSecondsNearTheEnd() =>
        Assert.Equal(
            "Re-block now (42s left)",
            For(OutboundMode.Firewall, hasSession: true, unlockUntil: Now.AddSeconds(42)).UnlockHeader);

    /// <summary>An unlock whose moment has passed is over, whatever the timer has done yet.</summary>
    [Fact]
    public void AnExpiredUnlockReadsAsBlocking()
    {
        var state = For(
            OutboundMode.Firewall, hasSession: true, unlockUntil: Now.AddSeconds(-1));

        Assert.Equal("Allow network for 10 minutes", state.UnlockHeader);
        Assert.Equal("Guard: enforcing", state.StatusHeader);
    }

    [Fact]
    public void TheGuardIsIdleWhenEnforcementIsOff()
    {
        Assert.Equal("Guard: not in use", For(OutboundMode.None).StatusHeader);
        Assert.Equal(
            "Guard: not in use (best-effort blocking only)",
            For(OutboundMode.EnvironmentOnly).StatusHeader);
    }

    [Fact]
    public void AMissingGuardSaysWhatToDoAboutIt() =>
        Assert.Equal(
            "Guard: not installed — re-run the installer to add it",
            For(OutboundMode.Firewall, GuardAvailability.NotInstalled).StatusHeader);

    [Fact]
    public void AStoppedGuardIsDistinctFromAMissingOne() =>
        Assert.Equal(
            "Guard: installed but not running",
            For(OutboundMode.Firewall, GuardAvailability.Stopped).StatusHeader);

    [Fact]
    public void AVersionMismatchSaysSo() =>
        Assert.Equal(
            "Guard: version mismatch — reinstall to match the tray",
            For(OutboundMode.Firewall, GuardAvailability.VersionMismatch).StatusHeader);

    /// <summary>
    /// The failure this whole feature most needs to avoid: rules created against a firewall that
    /// is switched off are accepted and enforce nothing. Being connected is not the same as
    /// working, and the menu must not imply otherwise.
    /// </summary>
    [Fact]
    public void ConnectedButIneffectiveDoesNotReadAsWorking()
    {
        var state = For(
            OutboundMode.Firewall,
            hasSession: true,
            health: new FirewallHealth(true, false, true, true));

        Assert.Equal("Guard: connected, but NOT enforcing — see the log", state.StatusHeader);
    }

    [Fact]
    public void GroupPolicyOverrideAlsoReadsAsNotEnforcing()
    {
        var state = For(
            OutboundMode.Firewall,
            hasSession: true,
            health: new FirewallHealth(true, true, true, LocalRulesApply: false));

        Assert.Contains("NOT enforcing", state.StatusHeader, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnlockedSessionSaysBlockingIsLifted() =>
        Assert.Equal(
            "Guard: blocking temporarily lifted",
            For(OutboundMode.Firewall, hasSession: true, unlockUntil: Now.AddMinutes(5)).StatusHeader);

    [Fact]
    public void ReadyIsDistinctFromEnforcing()
    {
        Assert.Equal("Guard: ready (applies at next start)", For(OutboundMode.Firewall).StatusHeader);
        Assert.Equal("Guard: enforcing", For(OutboundMode.Firewall, hasSession: true).StatusHeader);
    }
}
