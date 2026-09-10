using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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

    [Fact]
    public void BlockOutboundNetwork_DefaultsOff()
    {
        Assert.False(new ComfyConfig().BlockOutboundNetwork);
    }

    /// <summary>
    /// Properties that describe where ComfyUI is installed rather than how the user wants it
    /// run. These are the ones discovery is allowed to replace, so they are the only ones
    /// <see cref="ComfyConfig.CopyRuntimePreferencesFrom"/> may leave behind.
    /// </summary>
    private static readonly string[] InstallPathProperties =
    [
        nameof(ComfyConfig.PythonPath),
        nameof(ComfyConfig.MainScript),
        nameof(ComfyConfig.BaseDirectory),
        nameof(ComfyConfig.UserDirectory),
        nameof(ComfyConfig.InputDirectory),
        nameof(ComfyConfig.OutputDirectory),
        nameof(ComfyConfig.TempDirectory),
        nameof(ComfyConfig.FrontEndRoot),
        nameof(ComfyConfig.ExtraModelPathsConfig),
        nameof(ComfyConfig.DatabaseUrl),
        nameof(ComfyConfig.WorkingDirectory),
    ];

    /// <summary>
    /// Every settable property is either an install path or a runtime preference. A preference
    /// added to <see cref="ComfyConfig"/> but forgotten in
    /// <see cref="ComfyConfig.CopyRuntimePreferencesFrom"/> would silently revert to its default
    /// the first time discovery relocated ComfyUI — a bug with no visible symptom at the point it
    /// is introduced. Reflecting over the type is what makes that impossible to miss.
    /// </summary>
    [Fact]
    public void CopyRuntimePreferencesFrom_CopiesEveryNonInstallProperty()
    {
        var preferences = typeof(ComfyConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && !InstallPathProperties.Contains(p.Name))
            .ToList();

        Assert.NotEmpty(preferences);

        var source = new ComfyConfig();
        foreach (var property in preferences)
        {
            property.SetValue(source, Mutate(property.GetValue(source)));
        }

        var target = new ComfyConfig();
        target.CopyRuntimePreferencesFrom(source);

        foreach (var property in preferences)
        {
            Assert.Equal(property.GetValue(source), property.GetValue(target));
        }
    }

    /// <summary>Returns a value of the same type that differs from <paramref name="current"/>.</summary>
    private static object Mutate(object? current) => current switch
    {
        bool b => !b,
        int i => i + 1,
        string s => s + "-changed",
        List<string> => new List<string> { "--changed" },
        null => "changed",
        _ => throw new NotSupportedException(
            $"ComfyConfig gained a property of type {current.GetType()}; teach Mutate about it."),
    };

    /// <summary>
    /// The carry-across only replaces preferences, so a rediscovered installation keeps the
    /// paths discovery found for it.
    /// </summary>
    [Fact]
    public void CopyRuntimePreferencesFrom_LeavesInstallPathsAlone()
    {
        var source = new ComfyConfig { PythonPath = @"C:\old\python.exe", Port = 9999 };
        var target = new ComfyConfig { PythonPath = @"C:\discovered\python.exe" };

        target.CopyRuntimePreferencesFrom(source);

        Assert.Equal(@"C:\discovered\python.exe", target.PythonPath);
        Assert.Equal(9999, target.Port);
    }
}
