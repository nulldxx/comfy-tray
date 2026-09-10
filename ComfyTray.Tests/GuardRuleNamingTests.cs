using System;
using Xunit;

namespace ComfyTray.Tests;

/// <summary>
/// Tests for <see cref="GuardRuleNaming"/>. The naming scheme is what stands between a bulk
/// cleanup and somebody else's firewall rules, so both halves matter: our names must round-trip
/// and foreign names must be rejected.
/// </summary>
public sealed class GuardRuleNamingTests
{
    private static readonly Guid Session = Guid.Parse("3f2a1b4c-5d6e-4f70-8192-a3b4c5d6e7f8");
    private static readonly Guid OtherSession = Guid.Parse("11111111-2222-4333-8444-555555555555");

    [Fact]
    public void Compose_IsStableForTheSameInputs() =>
        Assert.Equal(
            GuardRuleNaming.Compose(Session, @"c:\comfyui\python.exe"),
            GuardRuleNaming.Compose(Session, @"c:\comfyui\python.exe"));

    [Fact]
    public void Compose_DiffersByPath() =>
        Assert.NotEqual(
            GuardRuleNaming.Compose(Session, @"c:\comfyui\python.exe"),
            GuardRuleNaming.Compose(Session, @"c:\comfyui\pythonw.exe"));

    [Fact]
    public void Compose_DiffersBySession() =>
        Assert.NotEqual(
            GuardRuleNaming.Compose(Session, @"c:\comfyui\python.exe"),
            GuardRuleNaming.Compose(OtherSession, @"c:\comfyui\python.exe"));

    [Fact]
    public void Compose_CarriesThePrefix() =>
        Assert.StartsWith(
            GuardRuleNaming.NamePrefix,
            GuardRuleNaming.Compose(Session, @"c:\comfyui\python.exe"),
            StringComparison.Ordinal);

    /// <summary>Windows Firewall rejects a rule name containing '|' or equal to "all".</summary>
    [Fact]
    public void Compose_ProducesANameWindowsFirewallAccepts()
    {
        var name = GuardRuleNaming.Compose(Session, @"c:\comfyui\python.exe");

        Assert.DoesNotContain('|', name);
        Assert.NotEqual("all", name, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_RecoversTheSession()
    {
        var name = GuardRuleNaming.Compose(Session, @"c:\comfyui\python.exe");

        Assert.True(GuardRuleNaming.TryParse(name, out var parsed));
        Assert.Equal(Session, parsed);
    }

    /// <summary>
    /// The guard deletes in bulk on startup, on shutdown and on every session teardown. Anything
    /// it does not recognise with certainty has to be left alone.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Core Networking - DNS (UDP-Out)")]
    [InlineData("ComfyTray")]
    [InlineData("ComfyTrayGuard-")]
    [InlineData("ComfyTrayGuard-nonsense-abcdef0123456789")]
    [InlineData("ComfyTrayGuard-3f2a1b4c5d6e4f708192a3b4c5d6e7f8")]
    [InlineData("ComfyTrayGuard-3f2a1b4c5d6e4f708192a3b4c5d6e7f8-tooshort")]
    [InlineData("xComfyTrayGuard-3f2a1b4c5d6e4f708192a3b4c5d6e7f8-abcdef0123456789")]
    public void TryParse_RejectsAnythingNotOurs(string? name)
    {
        Assert.False(GuardRuleNaming.TryParse(name, out var parsed));
        Assert.Equal(Guid.Empty, parsed);
    }

    [Fact]
    public void DescribeFor_NamesThePathAndTheTime()
    {
        var description = GuardRuleNaming.DescribeFor(
            Session, @"c:\comfyui\python.exe", new DateTimeOffset(2026, 9, 10, 14, 30, 0, TimeSpan.Zero));

        Assert.Contains(@"c:\comfyui\python.exe", description, StringComparison.Ordinal);
        Assert.Contains("2026-09-10T14:30:00Z", description, StringComparison.Ordinal);
        Assert.DoesNotContain('|', description);
    }

    [Fact]
    public void DescribeFor_NormalisesToUtc()
    {
        var description = GuardRuleNaming.DescribeFor(
            Session, @"c:\x.exe", new DateTimeOffset(2026, 9, 10, 15, 30, 0, TimeSpan.FromHours(1)));

        Assert.Contains("2026-09-10T14:30:00Z", description, StringComparison.Ordinal);
    }
}
