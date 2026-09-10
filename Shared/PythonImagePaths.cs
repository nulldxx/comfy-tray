using System;
using System.Collections.Generic;
using System.IO;

namespace ComfyTray;

/// <summary>
/// Works out which interpreter executables to block before ComfyUI is launched.
///
/// <para>
/// This is the single most valuable mitigation in the whole design. Blocking is otherwise
/// reactive: a process starts, the tracker notices it, a rule is created, and anything the
/// process did in between got through. The interpreter is the overwhelmingly common exfiltration
/// route and its path is known <b>before</b> <c>CreateProcess</c>, so blocking it up front closes
/// that window entirely for the case that matters most.
/// </para>
///
/// <para>
/// A virtual environment's <c>python.exe</c> is usually a small launcher that runs the base
/// interpreter recorded in <c>pyvenv.cfg</c>, and it is the base interpreter's image that a
/// child process reports. Blocking only the venv's copy would therefore miss it, so the base is
/// resolved and included too.
/// </para>
///
/// <para>
/// Filesystem access is injected rather than called directly, so the resolution logic is
/// exercised on Linux against Windows-shaped paths without any temp-directory theatre.
/// </para>
/// </summary>
internal static class PythonImagePaths
{
    /// <summary>
    /// Returns the normalised interpreter paths worth blocking for a given configured
    /// interpreter: the interpreter itself, its windowed sibling, and — when it is a virtual
    /// environment — the same pair under the base installation.
    /// </summary>
    /// <param name="interpreterPath">The configured interpreter, as ComfyConfig resolved it.</param>
    /// <param name="exists">Probe for a file's existence. Defaults to the real filesystem.</param>
    /// <param name="readText">Reads a file, returning null when it cannot be read.</param>
    public static IReadOnlyList<string> ForInterpreter(
        string interpreterPath,
        Func<string, bool>? exists = null,
        Func<string, string?>? readText = null)
    {
        exists ??= File.Exists;
        readText ??= TryReadAllText;

        var interpreter = GuardPathSet.Normalize(interpreterPath);
        if (interpreter.Length == 0)
        {
            return [];
        }

        var found = new List<string> { interpreter };
        var scriptsDirectory = GuardPathSet.DirectoryName(interpreter);

        AddSiblingLauncher(found, scriptsDirectory, interpreter, exists);

        // pyvenv.cfg sits one level above Scripts\ in a virtual environment.
        var venvRoot = GuardPathSet.DirectoryName(scriptsDirectory);
        if (venvRoot.Length == 0)
        {
            return GuardPathSet.Admit(found);
        }

        var config = GuardPathSet.Combine(venvRoot, "pyvenv.cfg");
        if (!exists(config))
        {
            return GuardPathSet.Admit(found);
        }

        var home = ParseHome(readText(config));
        if (home is null)
        {
            return GuardPathSet.Admit(found);
        }

        var baseInterpreter = GuardPathSet.Combine(home, "python.exe");
        if (exists(baseInterpreter))
        {
            found.Add(baseInterpreter);
        }

        AddSiblingLauncher(found, home, baseInterpreter, exists);

        return GuardPathSet.Admit(found);
    }

    /// <summary>
    /// Adds <c>pythonw.exe</c> next to an interpreter. It is the same runtime without a console,
    /// so a node that wants to stay quiet reaches for it.
    /// </summary>
    private static void AddSiblingLauncher(
        List<string> found, string directory, string interpreter, Func<string, bool> exists)
    {
        if (directory.Length == 0)
        {
            return;
        }

        var windowed = GuardPathSet.Combine(directory, "pythonw.exe");
        if (!string.Equals(windowed, interpreter, StringComparison.Ordinal) && exists(windowed))
        {
            found.Add(windowed);
        }
    }

    /// <summary>
    /// Pulls the <c>home</c> value out of a <c>pyvenv.cfg</c>. The format is a flat list of
    /// <c>key = value</c> lines with unspecified spacing, so the parsing is deliberately lax.
    /// </summary>
    private static string? ParseHome(string? config)
    {
        if (string.IsNullOrEmpty(config))
        {
            return null;
        }

        foreach (var rawLine in config.Split('\n'))
        {
            var line = rawLine.Trim();
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            if (!line[..separator].Trim().Equals("home", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = GuardPathSet.Normalize(line[(separator + 1)..]);
            return value.Length == 0 ? null : value;
        }

        return null;
    }

    private static string? TryReadAllText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
