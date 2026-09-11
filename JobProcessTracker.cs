using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading;

namespace ComfyTray;

/// <summary>
/// Watches the ComfyUI job for processes it has not seen before and asks the guard to block them.
///
/// <para>
/// This is the half of the design that catches what pre-blocking cannot: a custom node shelling
/// out to <c>git.exe</c>, an installer spawning a second interpreter, a bundled binary nobody
/// knew was there. It is inherently a step behind — a process that starts and opens a socket
/// inside one scan interval gets through — which is why the interpreter is blocked before launch
/// rather than left to this, and why the environment-variable isolation stays on underneath.
/// </para>
/// </summary>
internal sealed class JobProcessTracker : IDisposable
{
    private readonly JobObject _job;
    private readonly GuardClient _guard;
    private readonly Action<string> _log;
    private readonly TrackedImageSet _seen = new();
    private readonly Stopwatch _running = Stopwatch.StartNew();
    private readonly object _gate = new();

    private Timer? _timer;
    private bool _disposed;

    public JobProcessTracker(JobObject job, GuardClient guard, Action<string> log)
    {
        _job = job ?? throw new ArgumentNullException(nameof(job));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Starts scanning.
    /// </summary>
    /// <param name="alreadyBlocked">
    /// Executables blocked before the process was launched, so the first scan does not report
    /// them a second time.
    /// </param>
    public void Start(System.Collections.Generic.IEnumerable<string> alreadyBlocked)
    {
        _seen.MarkReported(alreadyBlocked);
        _timer = new Timer(_ => Scan(), null, TrackerSchedule.BusyInterval, Timeout.InfiniteTimeSpan);
    }

    [SuppressMessage(
        "Design",
        "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "A failed scan must be retried on the next tick, not kill the tracker.")]
    private void Scan()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var fresh = _seen.Observe(_job.GetProcessIds(), ResolveImage);
                if (fresh.Count > 0)
                {
                    // One request per scan rather than one per executable: each rule change makes
                    // Windows reload its filtering policy, so batching matters.
                    _guard.TryBlockImages(fresh);
                }
            }
            catch (Exception ex)
            {
                _log($"[comfy-tray] process scan failed: {ex.Message}");
            }
            finally
            {
                // Rescheduled from the end of the scan rather than on a fixed period, so a slow
                // scan cannot overlap the next one.
                _timer?.Change(
                    TrackerSchedule.IntervalAt(_running.Elapsed), Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>
    /// Maps a process id to its executable. Returns null for anything that cannot be read, which
    /// is ordinary: processes exit between being listed and being looked at.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "A process that has exited or cannot be opened is skipped, not reported.")]
    private static string? ResolveImage(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.MainModule?.FileName;
        }
        catch (ArgumentException)
        {
            // Already gone.
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (ExternalException)
        {
            // Access denied, or a bitness mismatch reading the module list.
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }

        GC.SuppressFinalize(this);
    }
}
