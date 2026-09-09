using System;
using System.Collections.Generic;
using Xunit;

namespace ComfyTray.Tests;

/// <summary>
/// Tests for <see cref="NetworkIsolation"/>. The feature is best-effort by design, so what is
/// worth pinning down is that it is applied consistently: every proxy spelling covered, loopback
/// left reachable, inherited values overwritten rather than merged, and the proxy target kept on
/// the loopback adapter.
/// </summary>
public sealed class NetworkIsolationTests
{
    private static Dictionary<string, string?> Applied()
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        NetworkIsolation.Apply(env);
        return env;
    }

    [Theory]
    [InlineData("HTTP_PROXY")]
    [InlineData("http_proxy")]
    [InlineData("HTTPS_PROXY")]
    [InlineData("https_proxy")]
    [InlineData("ALL_PROXY")]
    [InlineData("all_proxy")]
    [InlineData("FTP_PROXY")]
    [InlineData("ftp_proxy")]
    public void RoutesEveryProxySpellingToTheDeadProxy(string name)
    {
        Assert.Equal(NetworkIsolation.DeadProxy, Applied()[name]);
    }

    [Fact]
    public void DeadProxyTargetsLoopback()
    {
        // A routable address here would turn a block into an exfiltration path, so hold the
        // invariant explicitly rather than trusting the constant to stay put.
        Assert.StartsWith("http://127.0.0.1:", NetworkIsolation.DeadProxy, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("NO_PROXY")]
    [InlineData("no_proxy")]
    public void ExemptsLoopbackFromTheProxy(string name)
    {
        var value = Assert.IsType<string>(Applied()[name]);
        Assert.Contains("127.0.0.1", value, StringComparison.Ordinal);
        Assert.Contains("localhost", value, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("HF_HUB_OFFLINE", "1")]
    [InlineData("HF_DATASETS_OFFLINE", "1")]
    [InlineData("TRANSFORMERS_OFFLINE", "1")]
    [InlineData("PIP_NO_INDEX", "1")]
    [InlineData("DO_NOT_TRACK", "1")]
    public void SetsOfflineSwitches(string name, string expected)
    {
        Assert.Equal(expected, Applied()[name]);
    }

    [Fact]
    public void OverwritesAnInheritedProxy()
    {
        // ProcessStartInfo.Environment arrives pre-populated from the tray's own environment.
        // A proxy the user has set machine-wide must not survive into the child.
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["HTTP_PROXY"] = "http://corporate-proxy.example:8080",
            ["HF_HUB_OFFLINE"] = "0",
        };

        NetworkIsolation.Apply(env);

        Assert.Equal(NetworkIsolation.DeadProxy, env["HTTP_PROXY"]);
        Assert.Equal("1", env["HF_HUB_OFFLINE"]);
    }

    [Fact]
    public void LeavesUnrelatedVariablesAlone()
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = @"C:\Windows",
            ["CUDA_VISIBLE_DEVICES"] = "0",
        };

        NetworkIsolation.Apply(env);

        Assert.Equal(@"C:\Windows", env["PATH"]);
        Assert.Equal("0", env["CUDA_VISIBLE_DEVICES"]);
    }

    [Fact]
    public void VariableCountMatchesWhatIsWritten()
    {
        Assert.Equal(NetworkIsolation.VariableCount, Applied().Count);
    }

    [Fact]
    public void ApplyRejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => NetworkIsolation.Apply(null!));
    }
}
