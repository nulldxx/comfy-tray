using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace ComfyTray.Tests;

/// <summary>
/// Guards the constraint that makes the rest of the guard's tests meaningful.
///
/// <para>
/// Everything under <c>Shared/</c> is compiled into the WPF tray, the Windows service and this
/// cross-platform test project. The service half of the design — COM, named pipes, job objects —
/// cannot be exercised anywhere but Windows, so the fail-safe logic was deliberately pushed down
/// into pure code that can be. That only holds while the pure code stays pure, and the way it
/// stops being pure is somebody reaching for <c>Path.GetFullPath</c> or a registry read six
/// months from now, in a change that builds fine on Windows and never runs here again.
/// </para>
/// </summary>
public sealed class GuardPortabilityTests
{
    /// <summary>
    /// Namespaces and types that are either Windows-only or, in the case of
    /// <see cref="System.IO.Path"/>, silently host-dependent for Windows-shaped strings.
    /// </summary>
    private static readonly string[] Forbidden =
    [
        "System.Runtime.InteropServices",
        "System.ServiceProcess",
        "System.IO.Pipes",
        "System.Management",
        "Microsoft.Win32",
        "WindowsIdentity",
        "DllImport",
        "LibraryImport",
        "ComImport",
        "Path.GetFullPath",
        "Path.Combine",
        "Path.GetFileName",
        "Path.GetDirectoryName",
        "Registry.",
    ];

    [Fact]
    public void SharedSourcesArePortable()
    {
        var shared = FindSharedDirectory();
        var sources = Directory.GetFiles(shared, "*.cs", SearchOption.AllDirectories);

        Assert.NotEmpty(sources);

        var violations = new List<string>();
        foreach (var source in sources)
        {
            var text = File.ReadAllText(source);
            var name = Path.GetFileName(source);

            violations.AddRange(
                from token in Forbidden
                where text.Contains(token, StringComparison.Ordinal)
                select $"{name} references '{token}'");
        }

        Assert.True(
            violations.Count == 0,
            "Shared/ must stay free of Windows-only and host-dependent APIs so the guard's logic " +
            "remains testable off Windows. Move the offending code into the service or the tray:" +
            Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// Locates the repository's <c>Shared</c> folder from this file's own compile-time path.
    ///
    /// <para>
    /// Deliberately not derived from <see cref="AppContext.BaseDirectory"/>: where the test
    /// binaries land depends on the configuration, the target framework and whoever invoked the
    /// build, so walking up from them is a guess. The compiler knows exactly where this source
    /// file is, and this file sits one directory below the repository root next to
    /// <c>Shared</c>.
    /// </para>
    /// </summary>
    private static string FindSharedDirectory([CallerFilePath] string thisFile = "")
    {
        var testProjectDirectory = Path.GetDirectoryName(thisFile)
            ?? throw new DirectoryNotFoundException($"No directory for {thisFile}.");
        var repositoryRoot = Path.GetDirectoryName(testProjectDirectory)
            ?? throw new DirectoryNotFoundException($"No parent for {testProjectDirectory}.");

        var shared = Path.Combine(repositoryRoot, "Shared");
        if (!File.Exists(Path.Combine(shared, "GuardPathSet.cs")))
        {
            throw new DirectoryNotFoundException(
                $"Expected the Shared folder at {shared}, resolved from {thisFile}.");
        }

        return shared;
    }
}
