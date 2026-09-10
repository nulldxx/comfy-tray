using System;
using System.Collections.Generic;
using System.Text;

namespace ComfyTray;

/// <summary>
/// Normalisation and admission rules for the executable paths the guard will block.
///
/// <para>
/// Deliberately free of Windows APIs — in particular it does <b>not</b> use
/// <see cref="System.IO.Path"/>. The test project targets plain <c>net10.0</c> and runs on
/// Linux, where <c>Path</c> applies POSIX semantics to a string like <c>C:\x</c> and produces
/// a different answer than it would on Windows. Every path operation here is explicit string
/// manipulation so that the result is identical on both, and the tests that pin it are
/// therefore worth something.
/// </para>
/// </summary>
internal static class GuardPathSet
{
    /// <summary>
    /// Upper bound on the rules a single session may hold. A custom node that spawns a
    /// distinct executable per operation would otherwise churn the Windows Filtering Platform
    /// policy store — every rule add or remove triggers a reload — so past this point the
    /// guard stops adding and says so loudly instead.
    /// </summary>
    public const int MaxRulesPerSession = 64;

    /// <summary>
    /// Executables that are never blocked, matched on filename alone and wherever they live.
    ///
    /// <para>
    /// The policy is otherwise to block every image in the ComfyUI process tree
    /// unconditionally, and this list is the one carve-out. None of these can legitimately be
    /// a descendant of the ComfyUI interpreter, so excluding them costs nothing — but if one
    /// ever did land in the job object, a block rule on it would be a catastrophe with no
    /// visible connection to ComfyUI. Blocking <c>svchost.exe</c> outbound takes DNS, DHCP and
    /// Windows Update down machine-wide, for every user, until the session ends; the user
    /// would be left with a machine that had quietly lost the internet.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> NeverBlock = new(StringComparer.Ordinal)
    {
        "svchost.exe",
        "services.exe",
        "lsass.exe",
        "wininit.exe",
        "csrss.exe",
        "smss.exe",
        "explorer.exe",
        "msmpeng.exe",
    };

    /// <summary>
    /// Reduces a Windows path to the canonical form used for rule naming and de-duplication:
    /// forward slashes become backslashes, repeated separators collapse, a <c>\\?\</c> prefix
    /// is removed, any trailing separator is dropped, and the result is lower-cased.
    ///
    /// <para>
    /// The <c>\\?\</c> prefix has to go because Windows Firewall stores a rule's
    /// <c>ApplicationName</c> as an ordinary path and will not match an extended-length one.
    /// The UNC form <c>\\?\UNC\server\share</c> is rewritten back to <c>\\server\share</c>
    /// rather than mangled into a local path.
    /// </para>
    ///
    /// <para>
    /// Returns an empty string for null, empty or whitespace input, so callers can filter
    /// rather than handle an exception per path.
    /// </para>
    /// </summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var working = path.Trim().Replace('/', '\\');

        // Strip the extended-length prefix. \\?\UNC\server\share is the UNC spelling and has to
        // keep its leading double separator, so it becomes \\server\share rather than server\share.
        if (working.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            working = @"\\" + working[8..];
        }
        else if (working.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            working = working[4..];
        }

        // A leading \\ is a UNC root and must survive the collapse below.
        var uncPrefix = working.StartsWith(@"\\", StringComparison.Ordinal);
        if (uncPrefix)
        {
            working = working[2..];
        }

        var builder = new StringBuilder(working.Length);
        var previousWasSeparator = false;
        foreach (var c in working)
        {
            if (c == '\\')
            {
                if (!previousWasSeparator)
                {
                    builder.Append(c);
                }

                previousWasSeparator = true;
                continue;
            }

            builder.Append(c);
            previousWasSeparator = false;
        }

        var collapsed = builder.ToString();

        // Drop a trailing separator, but never turn "c:\" into "c:" — a bare drive letter means
        // "the current directory on that drive", which is a different location entirely.
        if (collapsed.Length > 1 &&
            collapsed.EndsWith('\\') &&
            !(collapsed.Length == 3 && collapsed[1] == ':'))
        {
            collapsed = collapsed[..^1];
        }

        return ((uncPrefix ? @"\\" : string.Empty) + collapsed).ToLowerInvariant();
    }

    /// <summary>Returns the filename portion of an already-normalised path.</summary>
    public static string FileName(string normalizedPath)
    {
        ArgumentNullException.ThrowIfNull(normalizedPath);

        var separator = normalizedPath.LastIndexOf('\\');
        return separator < 0 ? normalizedPath : normalizedPath[(separator + 1)..];
    }

    /// <summary>
    /// Returns everything before the last separator, or an empty string when there is none.
    /// The Windows counterpart of the framework path helper, spelled out here because
    /// <c>Path</c> would apply POSIX rules to these strings when the tests run on Linux.
    /// </summary>
    public static string DirectoryName(string normalizedPath)
    {
        ArgumentNullException.ThrowIfNull(normalizedPath);

        var separator = normalizedPath.LastIndexOf('\\');
        return separator <= 0 ? string.Empty : normalizedPath[..separator];
    }

    /// <summary>Joins a directory and a name with a single separator.</summary>
    public static string Combine(string directory, string name)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(name);

        if (directory.Length == 0)
        {
            return name;
        }

        return directory.EndsWith('\\') ? directory + name : directory + "\\" + name;
    }

    /// <summary>
    /// True when the path names one of the <see cref="NeverBlock"/> executables. Expects an
    /// already-normalised path.
    /// </summary>
    public static bool IsNeverBlocked(string normalizedPath) =>
        NeverBlock.Contains(FileName(normalizedPath));

    /// <summary>
    /// Normalises, drops anything empty or excluded, and de-duplicates, preserving the order in
    /// which paths were first seen so the log reads in discovery order.
    /// </summary>
    public static List<string> Admit(IEnumerable<string?> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var admitted = new List<string>();

        foreach (var path in paths)
        {
            var normalized = Normalize(path);
            if (normalized.Length == 0 || IsNeverBlocked(normalized) || !seen.Add(normalized))
            {
                continue;
            }

            admitted.Add(normalized);
        }

        return admitted;
    }
}
