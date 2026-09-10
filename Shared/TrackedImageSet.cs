using System;
using System.Collections.Generic;
using System.Linq;

namespace ComfyTray;

/// <summary>
/// Remembers which processes in the ComfyUI job have already been looked at, and reports the
/// executables that are new.
///
/// <para>
/// Resolving a process id to an image path is a system call, and the same ids recur on every
/// scan, so each one is resolved once. Ids that leave the job are forgotten, so that if Windows
/// later reuses the number for a different process the new one is resolved rather than mistaken
/// for the old.
/// </para>
///
/// <para>
/// The gap that leaves is inherent to polling: a process that starts <b>and</b> exits between
/// two scans is never seen at all, and if its id is reused in that window the replacement is
/// mistaken for it. Nothing short of kernel notification closes that, which is why the
/// interpreter is blocked before it launches rather than relying on this.
/// </para>
/// </summary>
internal sealed class TrackedImageSet
{
    private readonly HashSet<int> _resolvedPids = [];
    private readonly HashSet<string> _reportedPaths = new(StringComparer.Ordinal);

    /// <summary>Every distinct executable reported so far, in the order first seen.</summary>
    private readonly List<string> _order = [];

    /// <summary>Executables seen so far, oldest first.</summary>
    public IReadOnlyList<string> Reported => _order;

    /// <summary>
    /// Takes the job's current process ids and returns the executables not previously reported.
    /// </summary>
    /// <param name="pids">Every process currently in the job.</param>
    /// <param name="resolveImage">
    /// Maps a process id to its image path, or null when the process has already exited or
    /// cannot be opened — both of which are ordinary and are skipped silently.
    /// </param>
    public IReadOnlyList<string> Observe(
        IReadOnlyCollection<int> pids, Func<int, string?> resolveImage)
    {
        ArgumentNullException.ThrowIfNull(pids);
        ArgumentNullException.ThrowIfNull(resolveImage);

        // Forget ids that have left the job, so a reused number is treated as a new process.
        _resolvedPids.IntersectWith(pids);

        var candidates = new List<string?>();
        foreach (var pid in pids)
        {
            if (!_resolvedPids.Add(pid))
            {
                continue;
            }

            candidates.Add(resolveImage(pid));
        }

        var fresh = GuardPathSet.Admit(candidates)
            .Where(path => !_reportedPaths.Contains(path))
            .ToList();

        foreach (var path in fresh)
        {
            _reportedPaths.Add(path);
            _order.Add(path);
        }

        return fresh;
    }

    /// <summary>
    /// Marks executables as already reported without having observed them — used for the
    /// interpreter images blocked before the process was launched, so the first scan does not
    /// report them a second time.
    /// </summary>
    public void MarkReported(IEnumerable<string> normalizedPaths)
    {
        ArgumentNullException.ThrowIfNull(normalizedPaths);

        foreach (var path in normalizedPaths.Where(p => _reportedPaths.Add(p)))
        {
            _order.Add(path);
        }
    }
}

/// <summary>
/// How often to scan the job for new processes.
///
/// <para>
/// Custom nodes do their spawning during import, in the first seconds of a run, so the scan is
/// frequent while that is happening and backs off once the server is up. The alternative — one
/// steady rate — is either wasteful for the hours ComfyUI sits idle or too slow during the
/// window that actually matters.
/// </para>
/// </summary>
internal static class TrackerSchedule
{
    /// <summary>How long the frequent scanning lasts.</summary>
    public static readonly TimeSpan BusyWindow = TimeSpan.FromSeconds(30);

    /// <summary>Scan interval during startup, while custom nodes are importing.</summary>
    public static readonly TimeSpan BusyInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>Scan interval once the server has settled.</summary>
    public static readonly TimeSpan SteadyInterval = TimeSpan.FromSeconds(1);

    /// <summary>The interval to use, given how long the server has been running.</summary>
    public static TimeSpan IntervalAt(TimeSpan sinceStart) =>
        sinceStart < BusyWindow ? BusyInterval : SteadyInterval;
}
