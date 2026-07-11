using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace ComfyTray.Tests;

/// <summary>
/// Tests for <see cref="ComfyConfig"/> argument building — specifically the gating that lets a
/// portable/older ComfyUI (which may not know the newer flags, or lack the bundled front-end)
/// launch without being handed arguments it can't satisfy.
/// </summary>
public sealed class ComfyConfigTests : IDisposable
{
    private readonly string _root;

    public ComfyConfigTests() =>
        _root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "comfycfg-" + Guid.NewGuid().ToString("N"))).FullName;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "Best-effort temp cleanup.")]
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static bool HasFlag(List<string> args, string flag) => args.Contains(flag);

    [Fact]
    public void AlwaysEmitsBaseAndDirectoryFlags()
    {
        var args = new ComfyConfig().BuildArguments();
        Assert.True(HasFlag(args, "--base-directory"));
        Assert.True(HasFlag(args, "--user-directory"));
        Assert.True(HasFlag(args, "--output-directory"));
    }

    [Fact]
    public void OmitsFrontEndRoot_WhenEmpty()
    {
        var cfg = new ComfyConfig { FrontEndRoot = string.Empty };
        Assert.False(HasFlag(cfg.BuildArguments(), "--front-end-root"));
    }

    [Fact]
    public void OmitsFrontEndRoot_WhenDirectoryMissing()
    {
        var cfg = new ComfyConfig { FrontEndRoot = Path.Combine(_root, "no-such-frontend") };
        Assert.False(HasFlag(cfg.BuildArguments(), "--front-end-root"));
    }

    [Fact]
    public void EmitsFrontEndRoot_WhenDirectoryExists()
    {
        var frontEnd = Directory.CreateDirectory(Path.Combine(_root, "frontend")).FullName;
        var cfg = new ComfyConfig { FrontEndRoot = frontEnd };
        var args = cfg.BuildArguments();
        Assert.True(HasFlag(args, "--front-end-root"));
        Assert.Contains(frontEnd, args);
    }

    [Fact]
    public void OmitsExtraModelPaths_WhenFileMissing()
    {
        var cfg = new ComfyConfig { ExtraModelPathsConfig = Path.Combine(_root, "no-such.yaml") };
        Assert.False(HasFlag(cfg.BuildArguments(), "--extra-model-paths-config"));
    }

    [Fact]
    public void EmitsExtraModelPaths_WhenFileExists()
    {
        var yaml = Path.Combine(_root, "extra.yaml");
        File.WriteAllText(yaml, "config: {}");
        var cfg = new ComfyConfig { ExtraModelPathsConfig = yaml };
        Assert.True(HasFlag(cfg.BuildArguments(), "--extra-model-paths-config"));
    }

    [Fact]
    public void OmitsDatabaseUrl_WhenUnset_ButEmitsWhenExplicit()
    {
        Assert.False(HasFlag(new ComfyConfig { DatabaseUrl = null }.BuildArguments(), "--database-url"));

        var withDb = new ComfyConfig { DatabaseUrl = "sqlite:///C:/db/comfyui.db" };
        Assert.True(HasFlag(withDb.BuildArguments(), "--database-url"));
    }

    [Fact]
    public void EmitsEnableManager_OnlyWhenRequested()
    {
        Assert.True(HasFlag(new ComfyConfig { EnableManager = true }.BuildArguments(), "--enable-manager"));
        Assert.False(HasFlag(new ComfyConfig { EnableManager = false }.BuildArguments(), "--enable-manager"));
    }

    [Fact]
    public void WatchForUserLogon_DefaultsOff()
    {
        Assert.False(new ComfyConfig().WatchForUserLogon);
    }
}
