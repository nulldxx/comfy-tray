using System;
using System.Linq;
using Xunit;

namespace ComfyTray.Tests;

/// <summary>
/// Tests for <see cref="GuardPathSet"/>. These run on Linux, which is the point: the
/// normalisation is hand-written string manipulation precisely so that it does not depend on
/// the host's path semantics, and these assertions would be worthless if it did.
/// </summary>
public sealed class GuardPathSetTests
{
    [Theory]
    [InlineData(@"C:\Python\python.exe", @"c:\python\python.exe")]
    [InlineData(@"C:/Python/python.exe", @"c:\python\python.exe")]
    [InlineData(@"C:\\Python\\\python.exe", @"c:\python\python.exe")]
    [InlineData(@"  C:\Python\python.exe  ", @"c:\python\python.exe")]
    [InlineData(@"C:\Python\PYTHON.EXE", @"c:\python\python.exe")]
    [InlineData(@"C:\Python\", @"c:\python")]
    public void Normalize_CanonicalisesLocalPaths(string input, string expected) =>
        Assert.Equal(expected, GuardPathSet.Normalize(input));

    /// <summary>
    /// A bare drive letter means "the current directory on that drive", so the trailing
    /// separator on a drive root has to survive or the path changes meaning.
    /// </summary>
    [Fact]
    public void Normalize_KeepsDriveRootSeparator() =>
        Assert.Equal(@"c:\", GuardPathSet.Normalize(@"C:\"));

    /// <summary>
    /// Windows Firewall stores ApplicationName as an ordinary path and will not match an
    /// extended-length one, so the prefix has to come off.
    /// </summary>
    [Theory]
    [InlineData(@"\\?\C:\Python\python.exe", @"c:\python\python.exe")]
    [InlineData(@"\\?\UNC\server\share\python.exe", @"\\server\share\python.exe")]
    public void Normalize_StripsExtendedLengthPrefix(string input, string expected) =>
        Assert.Equal(expected, GuardPathSet.Normalize(input));

    [Fact]
    public void Normalize_PreservesUncRoot() =>
        Assert.Equal(@"\\server\share\python.exe", GuardPathSet.Normalize(@"\\server\share\python.exe"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_ReturnsEmptyForNothing(string? input) =>
        Assert.Equal(string.Empty, GuardPathSet.Normalize(input));

    [Fact]
    public void FileName_TakesLastSegment()
    {
        Assert.Equal("python.exe", GuardPathSet.FileName(@"c:\python\python.exe"));
        Assert.Equal("python.exe", GuardPathSet.FileName("python.exe"));
    }

    /// <summary>
    /// The one carve-out in an otherwise unconditional policy. Blocking svchost.exe outbound
    /// would take DNS, DHCP and Windows Update down machine-wide with nothing pointing back at
    /// ComfyUI, so it is excluded wherever it lives.
    /// </summary>
    [Theory]
    [InlineData(@"c:\windows\system32\svchost.exe")]
    [InlineData(@"c:\windows\system32\lsass.exe")]
    [InlineData(@"c:\windows\explorer.exe")]
    [InlineData(@"c:\somewhere\else\svchost.exe")]
    public void IsNeverBlocked_ExcludesWindowsCriticalBinaries(string path) =>
        Assert.True(GuardPathSet.IsNeverBlocked(path));

    [Theory]
    [InlineData(@"c:\python\python.exe")]
    [InlineData(@"c:\program files\git\bin\git.exe")]
    [InlineData(@"c:\windows\system32\curl.exe")]
    public void IsNeverBlocked_LetsEverythingElseThrough(string path) =>
        Assert.False(GuardPathSet.IsNeverBlocked(path));

    [Fact]
    public void Admit_NormalisesDeduplicatesAndPreservesOrder()
    {
        var admitted = GuardPathSet.Admit([
            @"C:\ComfyUI\.venv\Scripts\python.exe",
            @"C:/ComfyUI/.venv/Scripts/python.exe",   // same path, other spelling
            @"C:\Program Files\Git\bin\git.exe",
            @"c:\comfyui\.venv\scripts\PYTHON.EXE",   // same path again
        ]);

        Assert.Equal(
            [@"c:\comfyui\.venv\scripts\python.exe", @"c:\program files\git\bin\git.exe"],
            admitted);
    }

    [Fact]
    public void Admit_DropsEmptyAndExcludedEntries()
    {
        var admitted = GuardPathSet.Admit([
            null,
            "   ",
            @"C:\Windows\System32\svchost.exe",
            @"C:\ComfyUI\python.exe",
        ]);

        Assert.Equal([@"c:\comfyui\python.exe"], admitted);
    }

    /// <summary>
    /// Every rule add or remove triggers a Windows Filtering Platform policy reload, so a node
    /// spawning a distinct executable per operation must hit a ceiling rather than churn it.
    /// </summary>
    [Fact]
    public void MaxRulesPerSession_IsBounded() =>
        Assert.InRange(GuardPathSet.MaxRulesPerSession, 1, 256);
}
