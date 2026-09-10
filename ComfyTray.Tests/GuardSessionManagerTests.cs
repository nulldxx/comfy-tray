using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ComfyTray.Tests;

/// <summary>
/// Tests for <see cref="GuardSessionManager"/> — the part of the guard where a bug leaves
/// somebody's Python interpreter silently unable to reach the network with nothing to point at.
///
/// <para>
/// The invariant under test throughout: a firewall rule exists only while its session is alive
/// and its lease is fresh. Every way a session can end has a test here, because in production
/// each of them is the one that has to work when the others have already failed.
/// </para>
/// </summary>
public sealed class GuardSessionManagerTests
{
    private const string Python = @"c:\comfyui\.venv\scripts\python.exe";
    private const string Git = @"c:\program files\git\bin\git.exe";
    private const string Curl = @"c:\windows\system32\curl.exe";

    private readonly FakeFirewallRuleStore _store = new();
    private DateTimeOffset _now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private GuardSessionManager NewManager() => new(_store, () => _now);

    private Guid OpenSession(GuardSessionManager manager, int pid = 1234) =>
        manager.BeginSession("1.0.0", pid, 60).SessionId;

    [Fact]
    public void BlockingCreatesRules()
    {
        var manager = NewManager();
        var session = OpenSession(manager);

        var (added, already, rejected) = manager.BlockImages(session, [Python, Git]);

        Assert.Equal([Python, Git], added);
        Assert.Empty(already);
        Assert.Empty(rejected);
        Assert.Equal([Python, Git], _store.BlockedPaths);
    }

    /// <summary>The tracker resends everything it can see on every scan, so repeats must be free.</summary>
    [Fact]
    public void BlockingIsIdempotent()
    {
        var manager = NewManager();
        var session = OpenSession(manager);
        manager.BlockImages(session, [Python]);

        var (added, already, _) = manager.BlockImages(session, [Python]);

        Assert.Empty(added);
        Assert.Equal([Python], already);
        Assert.Equal([Python], _store.BlockedPaths);
    }

    [Fact]
    public void BlockingSkipsWindowsCriticalBinaries()
    {
        var manager = NewManager();
        var session = OpenSession(manager);

        manager.BlockImages(session, [Python, @"c:\windows\system32\svchost.exe"]);

        Assert.Equal([Python], _store.BlockedPaths);
    }

    [Fact]
    public void EndingASessionRemovesItsRules()
    {
        var manager = NewManager();
        var session = OpenSession(manager);
        manager.BlockImages(session, [Python, Git]);

        var removed = manager.EndSession(session, SessionEndReason.Requested);

        Assert.Equal(2, removed);
        Assert.Empty(_store.BlockedPaths);
    }

    /// <summary>
    /// The teardown that matters when the pipe does <b>not</b> drop: a client still connected but
    /// no longer running. Without this, a wedged tray holds rules indefinitely.
    /// </summary>
    [Fact]
    public void LeaseExpiryTearsDownASilentSession()
    {
        var manager = NewManager();
        var session = OpenSession(manager);
        manager.BlockImages(session, [Python]);

        _now = _now.AddSeconds(61);
        manager.Tick();

        Assert.Empty(_store.BlockedPaths);
        Assert.Empty(manager.GetSummaries());
    }

    [Fact]
    public void HeartbeatKeepsASessionAlive()
    {
        var manager = NewManager();
        var session = OpenSession(manager);
        manager.BlockImages(session, [Python]);

        for (var i = 0; i < 10; i++)
        {
            _now = _now.AddSeconds(30);
            manager.Heartbeat(session);
            manager.Tick();
        }

        Assert.Equal([Python], _store.BlockedPaths);
    }

    [Fact]
    public void HeartbeatDrainsTheLogAndDoesNotRepeatIt()
    {
        var manager = NewManager();
        var session = OpenSession(manager);
        manager.BlockImages(session, [Python]);

        Assert.NotEmpty(manager.Heartbeat(session).LogLines);
        Assert.Empty(manager.Heartbeat(session).LogLines);
    }

    /// <summary>
    /// Unlock removes the rules rather than disabling them, so that a crash mid-unlock fails open
    /// instead of stranding disabled rules in the policy store.
    /// </summary>
    [Fact]
    public void UnlockLiftsBlockingButRemembersIt()
    {
        var manager = NewManager();
        var session = OpenSession(manager);
        manager.BlockImages(session, [Python, Git]);

        manager.Unlock(session, 300);
        Assert.Empty(_store.BlockedPaths);

        Assert.Equal(2, manager.Rearm(session));
        Assert.Equal([Python, Git], _store.BlockedPaths);
    }

    [Fact]
    public void UnlockExpiryRestoresBlocking()
    {
        var manager = NewManager();
        var session = OpenSession(manager);
        manager.BlockImages(session, [Python]);
        manager.Unlock(session, 60);

        _now = _now.AddSeconds(61);
        manager.Heartbeat(session);
        manager.Tick();

        Assert.Equal([Python], _store.BlockedPaths);
    }

    /// <summary>
    /// A node installed during the unlock window is exactly what the unlock was for, so re-arming
    /// has to cover what turned up while blocking was lifted rather than only what preceded it.
    /// </summary>
    [Fact]
    public void RearmingCoversPathsDiscoveredDuringTheUnlock()
    {
        var manager = NewManager();
        var session = OpenSession(manager);
        manager.BlockImages(session, [Python]);
        manager.Unlock(session, 300);

        manager.BlockImages(session, [Git, Curl]);
        Assert.Empty(_store.BlockedPaths);

        manager.Rearm(session);
        Assert.Equal([Python, Git, Curl], _store.BlockedPaths);
    }

    [Fact]
    public void EndingAnUnlockedSessionLeavesNothingBehind()
    {
        var manager = NewManager();
        var session = OpenSession(manager);
        manager.BlockImages(session, [Python]);
        manager.Unlock(session, 300);

        manager.EndSession(session, SessionEndReason.Disconnected);

        _now = _now.AddSeconds(301);
        manager.Tick();

        Assert.Empty(_store.BlockedPaths);
        Assert.Empty(manager.GetSummaries());
    }

    /// <summary>
    /// Fast user switching is a first-class feature of the tray, so two logged-on users each
    /// running a session is expected. Neither may disturb the other's rules.
    /// </summary>
    [Fact]
    public void SessionsAreIsolatedFromEachOther()
    {
        var manager = NewManager();
        var first = OpenSession(manager, pid: 1);
        var second = OpenSession(manager, pid: 2);

        manager.BlockImages(first, [Python]);
        manager.BlockImages(second, [Git]);

        manager.EndSession(first, SessionEndReason.Requested);

        Assert.Empty(_store.BlockedPathsFor(first));
        Assert.Equal([Git], _store.BlockedPathsFor(second));
    }

    [Fact]
    public void TwoSessionsBlockingTheSamePathKeepTheirOwnRules()
    {
        var manager = NewManager();
        var first = OpenSession(manager, pid: 1);
        var second = OpenSession(manager, pid: 2);

        manager.BlockImages(first, [Python]);
        manager.BlockImages(second, [Python]);

        manager.EndSession(first, SessionEndReason.Requested);

        Assert.Equal([Python], _store.BlockedPathsFor(second));
    }

    [Fact]
    public void RefusesMoreThanTheSessionLimit()
    {
        var manager = NewManager();
        for (var i = 0; i < GuardSessionManager.MaxSessions; i++)
        {
            OpenSession(manager, pid: i);
        }

        var error = Assert.Throws<GuardRequestException>(() => OpenSession(manager, pid: 99));
        Assert.Equal("TooManySessions", error.Code);
    }

    [Fact]
    public void LeasesAreClamped()
    {
        var manager = NewManager();

        Assert.Equal(
            GuardSessionManager.MinLeaseSeconds, manager.BeginSession("1.0.0", 1, 1).GrantedLeaseSeconds);
        Assert.Equal(
            GuardSessionManager.MaxLeaseSeconds, manager.BeginSession("1.0.0", 2, 99999).GrantedLeaseSeconds);
    }

    [Fact]
    public void UnlockDurationIsClamped()
    {
        var manager = NewManager();
        var session = OpenSession(manager);

        Assert.Equal(
            _now.AddSeconds(GuardSessionManager.MaxUnlockSeconds), manager.Unlock(session, 999_999));
    }

    /// <summary>
    /// Every rule add or remove triggers a Windows Filtering Platform policy reload, so a node
    /// spawning a distinct executable per operation must hit a ceiling and be reported, not churn
    /// the policy store.
    /// </summary>
    [Fact]
    public void RefusesMoreRulesThanTheCap()
    {
        var manager = NewManager();
        var session = OpenSession(manager);
        var many = Enumerable.Range(0, GuardPathSet.MaxRulesPerSession + 5)
            .Select(i => $@"c:\x\p{i}.exe")
            .ToList();

        var (added, _, rejected) = manager.BlockImages(session, many);

        Assert.Equal(GuardPathSet.MaxRulesPerSession, added.Count);
        Assert.Equal(5, rejected.Count);
        Assert.Equal(GuardPathSet.MaxRulesPerSession, _store.BlockedPaths.Count);
    }

    [Fact]
    public void UnknownSessionIsRejected()
    {
        var manager = NewManager();

        var error = Assert.Throws<GuardRequestException>(
            () => manager.BlockImages(Guid.NewGuid(), [Python]));

        Assert.Equal("UnknownSession", error.Code);
    }

    [Fact]
    public void EndingAnUnknownSessionIsHarmless() =>
        Assert.Equal(0, NewManager().EndSession(Guid.NewGuid(), SessionEndReason.Disconnected));

    /// <summary>
    /// Runs on service start, covering a reboot or crash that left rules behind, and again on
    /// stop, because a stopped service cannot clean up later.
    /// </summary>
    [Fact]
    public void PurgeAllRemovesEverythingAndForgetsEverySession()
    {
        var manager = NewManager();
        var first = OpenSession(manager, pid: 1);
        var second = OpenSession(manager, pid: 2);
        manager.BlockImages(first, [Python]);
        manager.BlockImages(second, [Git]);

        Assert.Equal(2, manager.PurgeAll());

        Assert.Empty(_store.BlockedPaths);
        Assert.Empty(manager.GetSummaries());
    }

    /// <summary>
    /// A failing rule add must surface rather than leave the manager believing it blocked
    /// something it did not.
    /// </summary>
    [Fact]
    public void AFailedAddDoesNotClaimSuccess()
    {
        var manager = NewManager();
        var session = OpenSession(manager);
        _store.FailAddingPath = Git;

        Assert.Throws<InvalidOperationException>(() => manager.BlockImages(session, [Git]));

        // The session survives, and a later block for a different path still works.
        _store.FailAddingPath = null;
        manager.BlockImages(session, [Python]);
        Assert.Contains(Python, _store.BlockedPaths);
    }

    [Fact]
    public void SummariesReportWhatIsHeld()
    {
        var manager = NewManager();
        var session = OpenSession(manager, pid: 4242);
        manager.BlockImages(session, [Python, Git]);

        var summary = Assert.Single(manager.GetSummaries());

        Assert.Equal(session, summary.SessionId);
        Assert.Equal(4242, summary.ComfyPid);
        Assert.Equal(2, summary.RuleCount);
        Assert.False(summary.Unlocked);
    }

    [Fact]
    public void SummariesReportAnUnlockedSessionAsHoldingNothing()
    {
        var manager = NewManager();
        var session = OpenSession(manager);
        manager.BlockImages(session, [Python]);
        manager.Unlock(session, 300);

        var summary = Assert.Single(manager.GetSummaries());

        Assert.True(summary.Unlocked);
        Assert.Equal(0, summary.RuleCount);
    }
}
