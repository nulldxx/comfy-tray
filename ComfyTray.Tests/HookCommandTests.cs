using System;
using System.IO;
using Xunit;

namespace ComfyTray.Tests;

/// <summary>
/// Tests for hook command parsing and resolution — the validation the configuration dialog
/// blocks a save on. Paths are built under a temp directory so the assertions hold on any OS
/// (the tests never execute the "programs" they resolve).
/// </summary>
public sealed class HookCommandTests : IDisposable
{
    private readonly string _root;

    public HookCommandTests() =>
        _root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "comfyhook-" + Guid.NewGuid().ToString("N"))).FullName;

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

    private string CreateProgram(string relativePath)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "not really a program");
        return full;
    }

    [Fact]
    public void Split_SeparatesBareCommandFromArguments()
    {
        var (exe, args) = HookCommand.Split("notepad.exe  a  b");
        Assert.Equal("notepad.exe", exe);
        Assert.Equal("a  b", args);
    }

    [Fact]
    public void Split_HonoursQuotedExecutable()
    {
        var (exe, args) = HookCommand.Split("\"C:\\Program Files\\App\\run.exe\" --flag \"x y\"");
        Assert.Equal(@"C:\Program Files\App\run.exe", exe);
        Assert.Equal("--flag \"x y\"", args);
    }

    [Fact]
    public void Split_QuotedWithNoArguments()
    {
        var (exe, args) = HookCommand.Split("  \"/usr/bin/some tool\"  ");
        Assert.Equal("/usr/bin/some tool", exe);
        Assert.Equal(string.Empty, args);
    }

    [Fact]
    public void Split_EmptyCommandYieldsEmptyParts()
    {
        var (exe, args) = HookCommand.Split("   ");
        Assert.Equal(string.Empty, exe);
        Assert.Equal(string.Empty, args);
    }

    [Fact]
    public void TryResolve_FindsQuotedPathWithSpaces()
    {
        var program = CreateProgram(Path.Combine("space dir", "prog"));

        Assert.True(HookCommand.TryResolve($"\"{program}\" --one --two", out var exe, out var args, out var error));
        Assert.Equal(program, exe);
        Assert.Equal("--one --two", args);
        Assert.Null(error);
    }

    [Fact]
    public void TryResolve_FindsUnquotedPathWithSpaces()
    {
        var program = CreateProgram(Path.Combine("space dir", "prog"));

        Assert.True(HookCommand.TryResolve($"{program} --one", out var exe, out var args, out _));
        Assert.Equal(program, exe);
        Assert.Equal("--one", args);
    }

    [Fact]
    public void TryResolve_ExpandsEnvironmentTokens()
    {
        var program = CreateProgram("tokenprog");
        Environment.SetEnvironmentVariable("COMFYTRAY_TEST_DIR", _root);
        try
        {
            Assert.True(HookCommand.TryResolve("%COMFYTRAY_TEST_DIR%/tokenprog -q", out var exe, out var args, out _));
            Assert.Equal(program, exe);
            Assert.Equal("-q", args);
        }
        finally
        {
            Environment.SetEnvironmentVariable("COMFYTRAY_TEST_DIR", null);
        }
    }

    [Fact]
    public void TryResolve_SearchesPath()
    {
        CreateProgram("onpath");
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", _root + Path.PathSeparator + originalPath);
        try
        {
            Assert.True(HookCommand.TryResolve("onpath --go", out var exe, out var args, out _));
            Assert.Equal(Path.Combine(_root, "onpath"), exe);
            Assert.Equal("--go", args);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Fact]
    public void TryResolve_FailsWhenExecutableMissing()
    {
        var missing = Path.Combine(_root, "not-here");

        Assert.False(HookCommand.TryResolve($"{missing} --x", out _, out _, out var error));
        Assert.Contains("not-here", error ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void TryResolve_FailsOnEmptyCommand()
    {
        Assert.False(HookCommand.TryResolve("   ", out _, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Run_DoesNothingForBlankCommand()
    {
        var lines = 0;
        HookCommand.Run("before start", "  ", _ => lines++, TimeSpan.FromSeconds(1));
        Assert.Equal(0, lines);
    }

    [Fact]
    public void Run_LogsWhenCommandCannotBeResolved()
    {
        var logged = string.Empty;
        HookCommand.Run("after stop", Path.Combine(_root, "nope"), line => logged += line, TimeSpan.FromSeconds(1));
        Assert.Contains("after stop", logged, StringComparison.Ordinal);
        Assert.Contains("nope", logged, StringComparison.Ordinal);
    }
}
