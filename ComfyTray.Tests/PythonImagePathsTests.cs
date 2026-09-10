using System;
using System.Collections.Generic;
using Xunit;

namespace ComfyTray.Tests;

/// <summary>
/// Tests for <see cref="PythonImagePaths"/>. Filesystem access is injected, so these run against
/// Windows-shaped paths on any host without creating a single file.
/// </summary>
public sealed class PythonImagePathsTests
{
    private const string VenvPython = @"C:\ComfyUI\.venv\Scripts\python.exe";

    private static Func<string, bool> Existing(params string[] paths)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        return set.Contains;
    }

    [Fact]
    public void IncludesTheInterpreterItself() =>
        Assert.Contains(
            @"c:\comfyui\.venv\scripts\python.exe",
            PythonImagePaths.ForInterpreter(VenvPython, Existing(), _ => null));

    [Fact]
    public void IncludesTheWindowedSiblingWhenPresent()
    {
        var paths = PythonImagePaths.ForInterpreter(
            VenvPython,
            Existing(@"c:\comfyui\.venv\scripts\pythonw.exe"),
            _ => null);

        Assert.Contains(@"c:\comfyui\.venv\scripts\pythonw.exe", paths);
    }

    [Fact]
    public void OmitsTheWindowedSiblingWhenAbsent() =>
        Assert.DoesNotContain(
            @"c:\comfyui\.venv\scripts\pythonw.exe",
            PythonImagePaths.ForInterpreter(VenvPython, Existing(), _ => null));

    /// <summary>
    /// A venv's python.exe is usually a launcher for the base interpreter, and it is the base
    /// interpreter's image a child process reports. Blocking only the venv copy would miss it.
    /// </summary>
    [Fact]
    public void ResolvesTheBaseInterpreterFromPyvenvCfg()
    {
        var paths = PythonImagePaths.ForInterpreter(
            VenvPython,
            Existing(
                @"c:\comfyui\.venv\pyvenv.cfg",
                @"c:\python312\python.exe",
                @"c:\python312\pythonw.exe"),
            _ => "home = C:\\Python312\nversion = 3.12.1\ninclude-system-site-packages = false\n");

        Assert.Contains(@"c:\python312\python.exe", paths);
        Assert.Contains(@"c:\python312\pythonw.exe", paths);
    }

    [Theory]
    [InlineData("home=C:\\Python312\n")]
    [InlineData("home   =    C:\\Python312   \n")]
    [InlineData("HOME = C:\\Python312\n")]
    [InlineData("version = 3.12.1\nhome = C:\\Python312\n")]
    public void ToleratesPyvenvCfgSpacingAndCasing(string config)
    {
        var paths = PythonImagePaths.ForInterpreter(
            VenvPython,
            Existing(@"c:\comfyui\.venv\pyvenv.cfg", @"c:\python312\python.exe"),
            _ => config);

        Assert.Contains(@"c:\python312\python.exe", paths);
    }

    [Fact]
    public void IgnoresPyvenvCfgWithoutAHomeLine()
    {
        var paths = PythonImagePaths.ForInterpreter(
            VenvPython,
            Existing(@"c:\comfyui\.venv\pyvenv.cfg"),
            _ => "version = 3.12.1\n");

        Assert.Equal([@"c:\comfyui\.venv\scripts\python.exe"], paths);
    }

    [Fact]
    public void IgnoresAnUnreadablePyvenvCfg()
    {
        var paths = PythonImagePaths.ForInterpreter(
            VenvPython, Existing(@"c:\comfyui\.venv\pyvenv.cfg"), _ => null);

        Assert.Equal([@"c:\comfyui\.venv\scripts\python.exe"], paths);
    }

    /// <summary>A system-wide interpreter has no pyvenv.cfg and needs no base resolution.</summary>
    [Fact]
    public void HandlesAnInterpreterOutsideAVirtualEnvironment()
    {
        var paths = PythonImagePaths.ForInterpreter(
            @"C:\Python312\python.exe", Existing(), _ => null);

        Assert.Equal([@"c:\python312\python.exe"], paths);
    }

    [Fact]
    public void NeverReturnsDuplicates()
    {
        var paths = PythonImagePaths.ForInterpreter(
            VenvPython,
            Existing(@"c:\comfyui\.venv\pyvenv.cfg", @"c:\comfyui\.venv\scripts\python.exe"),
            // A pyvenv.cfg pointing back at its own venv is degenerate but must not duplicate.
            _ => "home = C:\\ComfyUI\\.venv\\Scripts\n");

        Assert.Equal(paths.Count, new HashSet<string>(paths, StringComparer.Ordinal).Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ReturnsNothingForNoInterpreter(string path) =>
        Assert.Empty(PythonImagePaths.ForInterpreter(path, Existing(), _ => null));
}
