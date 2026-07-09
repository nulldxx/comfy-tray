using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace ComfyTray.Tests;

/// <summary>
/// Tests for <see cref="ComfyDiscovery"/>. The Windows environment wiring (config.json reads,
/// %USERPROFILE% scans) is not exercised here; instead the pure core <c>DiscoverFrom</c> is driven
/// against synthetic directory trees so the layout/priority logic can be validated on any platform.
/// </summary>
public sealed class ComfyDiscoveryTests : IDisposable
{
    private readonly string _root;

    public ComfyDiscoveryTests() =>
        _root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "comfydisc-" + Guid.NewGuid().ToString("N"))).FullName;

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

    private string Touch(params string[] parts)
    {
        var path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    private static List<ComfyInstallation> Discover(
        IEnumerable<string>? desktopBasePaths = null,
        IEnumerable<string>? standalone = null,
        IEnumerable<string>? legacy = null,
        IEnumerable<string>? portable = null,
        string? bundledMain = null,
        string? bundledFrontEnd = null,
        string? extraModels = null) =>
        ComfyDiscovery.DiscoverFrom(
            desktopBasePaths ?? [],
            standalone ?? [],
            legacy ?? [],
            portable ?? [],
            bundledMain,
            bundledFrontEnd,
            extraModels,
            trace: null);

    [Fact]
    public void FindsNothing_WhenNoCandidatesExist()
    {
        var results = Discover(portable: new[] { Path.Combine(_root, "does-not-exist") });
        Assert.Empty(results);
    }

    [Fact]
    public void DetectsPortableInstall()
    {
        Touch("portable", "python_embeded", "python.exe");
        Touch("portable", "ComfyUI", "main.py");

        var results = Discover(portable: new[] { Path.Combine(_root, "portable") });

        var install = Assert.Single(results);
        Assert.Equal(ComfyInstallKind.Portable, install.Kind);
        Assert.Equal(Path.Combine(_root, "portable", "ComfyUI"), install.BaseDirectory);
        Assert.False(install.HasManager);
    }

    [Fact]
    public void DetectsDesktopCheckout_UnderBasePath()
    {
        var basePath = Dir("base");
        Touch("base", ".venv", "Scripts", "python.exe");
        Touch("base", "ComfyUI", "main.py");
        Dir("base", "custom_nodes", "ComfyUI-Manager");

        var results = Discover(desktopBasePaths: new[] { basePath });

        var install = Assert.Single(results);
        Assert.Equal(ComfyInstallKind.Desktop, install.Kind);
        Assert.Equal(basePath, install.BaseDirectory);
        Assert.True(install.HasManager);
        Assert.Null(install.FrontEndRoot); // checkout serves its own front-end
    }

    [Fact]
    public void DetectsLegacyBundledLayout_WhenNoCheckoutButBundledMainExists()
    {
        var basePath = Dir("base");
        Touch("base", ".venv", "Scripts", "python.exe");
        var bundledMain = Touch("app", "resources", "ComfyUI", "main.py");
        var frontEnd = Dir("app", "resources", "ComfyUI", "web_custom_versions", "desktop_app");

        var results = Discover(
            desktopBasePaths: new[] { basePath }, bundledMain: bundledMain, bundledFrontEnd: frontEnd);

        var install = Assert.Single(results);
        Assert.Equal(ComfyInstallKind.DesktopBundled, install.Kind);
        Assert.Equal(bundledMain, install.MainScript);
        Assert.Equal(frontEnd, install.FrontEndRoot);
    }

    [Fact]
    public void DetectsManagedInstance_WithInTreeVenvCheckout()
    {
        // Current "Comfy Desktop 2" shape: the instance root holds a self-contained ComfyUI checkout
        // whose .venv sits inside the checkout (beside main.py), not one level above it.
        var instanceRoot = Dir("installs", "ComfyUI");
        Touch("installs", "ComfyUI", "ComfyUI", "main.py");
        Touch("installs", "ComfyUI", "ComfyUI", ".venv", "Scripts", "python.exe");
        Dir("installs", "ComfyUI", "ComfyUI", "custom_nodes", "ComfyUI-Manager");

        var results = Discover(standalone: new[] { instanceRoot });

        var install = Assert.Single(results);
        Assert.Equal(ComfyInstallKind.DesktopStandalone, install.Kind);
        var checkout = Path.Combine(instanceRoot, "ComfyUI");
        Assert.Equal(checkout, install.BaseDirectory);
        Assert.Equal(Path.Combine(checkout, "main.py"), install.MainScript);
        Assert.Equal(Path.Combine(checkout, ".venv", "Scripts", "python.exe"), install.PythonPath);
        Assert.True(install.HasManager);
    }

    [Fact]
    public void SkipsBase_WhenVenvPythonMissing()
    {
        var basePath = Dir("base");
        Touch("base", "ComfyUI", "main.py"); // checkout present but no .venv python

        var results = Discover(desktopBasePaths: new[] { basePath });
        Assert.Empty(results);
    }

    [Fact]
    public void DeduplicatesSameInstall_AcrossSources()
    {
        var basePath = Dir("base");
        Touch("base", ".venv", "Scripts", "python.exe");
        Touch("base", "ComfyUI", "main.py");

        // Same path appears as both a config basePath and a legacy well-known path.
        var results = Discover(desktopBasePaths: new[] { basePath }, legacy: new[] { basePath });
        Assert.Single(results);
    }

    [Fact]
    public void OrdersByPriority_DesktopConfigBeforeStandaloneBeforeLegacyBeforePortable()
    {
        var configBase = Dir("config-base");
        Touch("config-base", ".venv", "Scripts", "python.exe");
        Touch("config-base", "ComfyUI", "main.py");

        var standalone = Dir("installs", "inst1");
        Touch("installs", "inst1", ".venv", "Scripts", "python.exe");
        Touch("installs", "inst1", "ComfyUI", "main.py");

        var legacy = Dir("legacy");
        Touch("legacy", ".venv", "Scripts", "python.exe");
        Touch("legacy", "ComfyUI", "main.py");

        var portableRoot = Dir("portable");
        Touch("portable", "python_embeded", "python.exe");
        Touch("portable", "ComfyUI", "main.py");

        var results = Discover(
            desktopBasePaths: new[] { configBase },
            standalone: new[] { standalone },
            legacy: new[] { legacy },
            portable: new[] { portableRoot });

        Assert.Equal(4, results.Count);
        Assert.Equal(configBase, results[0].BaseDirectory);
        Assert.Equal(ComfyInstallKind.DesktopStandalone, results[1].Kind);
        Assert.Equal(legacy, results[2].BaseDirectory);
        Assert.Equal(ComfyInstallKind.Portable, results[3].Kind);
    }

    [Fact]
    public void FromInstallation_DerivesBaseSubdirectoriesAndFlags()
    {
        Touch("portable", "python_embeded", "python.exe");
        Touch("portable", "ComfyUI", "main.py");
        var install = Assert.Single(Discover(portable: new[] { Path.Combine(_root, "portable") }));

        var cfg = ComfyConfig.FromInstallation(install);

        var baseDir = Path.Combine(_root, "portable", "ComfyUI");
        Assert.Equal(baseDir, cfg.BaseDirectory);
        Assert.Equal(Path.Combine(baseDir, "user"), cfg.UserDirectory);
        Assert.Equal(Path.Combine(baseDir, "output"), cfg.OutputDirectory);
        Assert.Equal(string.Empty, cfg.FrontEndRoot); // portable: no bundled front-end
    }
}
