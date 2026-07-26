using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace ComfyTray;

/// <summary>
/// Parsing, validation and hidden execution of the user's "before start" / "after stop"
/// hook commands. A hook is a Windows-style command line: an executable — bare, or quoted
/// when its path contains spaces — optionally followed by arguments that are passed to the
/// process verbatim. Values may contain <c>%VAR%</c> environment tokens, like the paths in
/// <see cref="ComfyConfig"/>.
/// </summary>
internal static class HookCommand
{
    private static readonly char[] WhitespaceChars = [' ', '\t'];

    /// <summary>
    /// Splits a command line at its first whitespace, honouring a leading quoted path.
    /// This is the naive split; <see cref="TryResolve"/> additionally probes longer prefixes
    /// so an unquoted path containing spaces can still be matched, as CreateProcess does.
    /// </summary>
    public static (string Executable, string Arguments) Split(string? commandLine)
    {
        var text = (commandLine ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return (string.Empty, string.Empty);
        }

        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end < 0
                ? (text[1..], string.Empty) // unterminated quote: treat the remainder as the path
                : (text[1..end], text[(end + 1)..].TrimStart());
        }

        var ws = text.IndexOfAny(WhitespaceChars);
        return ws < 0 ? (text, string.Empty) : (text[..ws], text[(ws + 1)..].TrimStart());
    }

    /// <summary>
    /// Resolves the executable named by <paramref name="commandLine"/> to a file on disk,
    /// searching <c>PATH</c> (with <c>PATHEXT</c> extensions) when it isn't given as a path.
    /// Returns false with a human-readable <paramref name="error"/> when nothing matches, which
    /// is what the configuration dialog blocks a save on.
    /// </summary>
    public static bool TryResolve(
        string? commandLine,
        out string executablePath,
        out string arguments,
        out string? error)
    {
        executablePath = string.Empty;
        arguments = string.Empty;
        error = null;

        var text = (commandLine ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            error = "The command is empty.";
            return false;
        }

        foreach (var (candidate, tail) in Candidates(text))
        {
            var resolved = ResolveExecutable(candidate);
            if (resolved != null)
            {
                executablePath = resolved;
                arguments = tail;
                return true;
            }
        }

        var (named, _) = Split(text);
        error = $"'{Environment.ExpandEnvironmentVariables(named)}' was not found on disk or on PATH.";
        return false;
    }

    /// <summary>
    /// Candidate (executable, arguments) splits, shortest executable first. A quoted path has
    /// exactly one candidate; an unquoted one is also tried at each later whitespace boundary so
    /// <c>C:\Program Files\thing\run.exe -x</c> resolves without the user having to quote it.
    /// </summary>
    private static IEnumerable<(string Executable, string Arguments)> Candidates(string text)
    {
        if (text[0] == '"')
        {
            yield return Split(text);
            yield break;
        }

        var from = 0;
        while (true)
        {
            var ws = text.IndexOfAny(WhitespaceChars, from);
            if (ws < 0)
            {
                yield return (text, string.Empty);
                yield break;
            }

            yield return (text[..ws], text[(ws + 1)..].TrimStart());
            from = ws + 1;
        }
    }

    /// <summary>
    /// Returns the full path of <paramref name="token"/> as an executable file, or null when it
    /// names nothing that exists.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "A malformed path must resolve to 'not found', never throw at the caller.")]
    private static string? ResolveExecutable(string token)
    {
        var name = token.Trim().Trim('"');
        if (name.Length == 0)
        {
            return null;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(name);
            if (expanded.Length == 0)
            {
                return null;
            }

            if (Path.IsPathRooted(expanded) ||
                expanded.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                expanded.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                return Probe(expanded);
            }

            foreach (var dir in SearchDirectories())
            {
                var hit = Probe(Path.Combine(dir, expanded));
                if (hit != null)
                {
                    return hit;
                }
            }

            return null;
        }
        catch (Exception)
        {
            // Invalid characters, a path too long, an unreadable PATH entry: all mean "not found".
            return null;
        }
    }

    /// <summary>Checks <paramref name="path"/> as given, then with each <c>PATHEXT</c> suffix.</summary>
    private static string? Probe(string path)
    {
        if (File.Exists(path))
        {
            return Path.GetFullPath(path);
        }

        foreach (var ext in Extensions())
        {
            var candidate = path + ext;
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    /// <summary>Executable extensions from <c>PATHEXT</c>; empty off Windows, where none apply.</summary>
    private static IEnumerable<string> Extensions()
    {
        var pathExt = Environment.GetEnvironmentVariable("PATHEXT");
        if (string.IsNullOrWhiteSpace(pathExt))
        {
            yield break;
        }

        foreach (var ext in pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return ext.StartsWith('.') ? ext : "." + ext;
        }
    }

    private static IEnumerable<string> SearchDirectories()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return dir.Trim('"');
        }
    }

    /// <summary>
    /// Runs <paramref name="commandLine"/> with no window of any kind — no console is allocated
    /// (<c>CreateNoWindow</c> without a shell) and stdout/stderr are redirected into
    /// <paramref name="log"/> — and waits up to <paramref name="timeout"/> for it to exit. A hook
    /// that outlives the timeout is left running rather than killed; a hook that fails to start,
    /// or names something that isn't there, is logged and otherwise ignored so it can never stop
    /// the tray from starting or stopping ComfyUI. Does nothing when the command is blank.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "A user command is arbitrary; any failure is logged, never thrown at the server lifecycle.")]
    public static void Run(string label, string? commandLine, Action<string> log, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(log);

        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return;
        }

        if (!TryResolve(commandLine, out var exe, out var args, out var error))
        {
            log($"[comfy-tray] {label} command not run: {error}");
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            // Passed through verbatim: the user's own quoting is theirs to get right.
            Arguments = Environment.ExpandEnvironmentVariables(args),
            WorkingDirectory = Path.GetDirectoryName(exe) ?? ".",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        log($"[comfy-tray] running {label} command: {exe} {psi.Arguments}".TrimEnd());
        try
        {
            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => LogHookOutput(log, label, e.Data);
            process.ErrorDataReceived += (_, e) => LogHookOutput(log, label, e.Data);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                // The timed overload doesn't wait for the async readers to drain; the
                // parameterless one does, so the last lines of output aren't lost.
                process.WaitForExit();
                log($"[comfy-tray] {label} command finished (exit code {process.ExitCode}).");
            }
            else
            {
                // Left running: the Process object is disposed on the way out (which only
                // releases our handles and stops capturing its output — it does not kill it).
                log($"[comfy-tray] {label} command still running after " +
                    $"{(int)timeout.TotalSeconds}s; continuing without it.");
            }
        }
        catch (Exception ex)
        {
            log($"[comfy-tray] {label} command failed: {ex.Message}");
        }
    }

    private static void LogHookOutput(Action<string> log, string label, string? data)
    {
        if (data != null)
        {
            log($"[{label}] {data}");
        }
    }
}
