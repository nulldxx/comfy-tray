using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ComfyTray.Tests;

/// <summary>
/// Tests for <see cref="TrackedImageSet"/> — the bookkeeping behind dynamic tracking, which
/// decides what the guard is asked to block as a ComfyUI session spawns things.
/// </summary>
public sealed class TrackedImageSetTests
{
    private const string Python = @"c:\comfyui\.venv\scripts\python.exe";
    private const string Git = @"c:\program files\git\bin\git.exe";

    /// <summary>Counts how often each process id is resolved, to prove work is not repeated.</summary>
    private sealed class Resolver(Dictionary<int, string?> images)
    {
        public Dictionary<int, int> Calls { get; } = [];

        public string? Resolve(int pid)
        {
            Calls[pid] = Calls.TryGetValue(pid, out var count) ? count + 1 : 1;
            return images.TryGetValue(pid, out var image) ? image : null;
        }
    }

    [Fact]
    public void ReportsEachExecutableOnce()
    {
        var resolver = new Resolver(new() { [1] = Python, [2] = Git });
        var set = new TrackedImageSet();

        Assert.Equal([Python, Git], set.Observe([1, 2], resolver.Resolve));
        Assert.Empty(set.Observe([1, 2], resolver.Resolve));
    }

    [Fact]
    public void ReportsExecutablesThatAppearLater()
    {
        var resolver = new Resolver(new() { [1] = Python, [2] = Git });
        var set = new TrackedImageSet();

        Assert.Equal([Python], set.Observe([1], resolver.Resolve));
        Assert.Equal([Git], set.Observe([1, 2], resolver.Resolve));
    }

    /// <summary>Resolving is a system call and the same ids recur on every scan.</summary>
    [Fact]
    public void ResolvesEachProcessOnlyOnce()
    {
        var resolver = new Resolver(new() { [1] = Python });
        var set = new TrackedImageSet();

        for (var i = 0; i < 10; i++)
        {
            set.Observe([1], resolver.Resolve);
        }

        Assert.Equal(1, resolver.Calls[1]);
    }

    /// <summary>
    /// Windows reuses process ids. Once an id has left the job, the next process to carry that
    /// number is a different process and has to be looked at afresh.
    /// </summary>
    [Fact]
    public void ReResolvesAProcessIdThatLeftAndCameBack()
    {
        var images = new Dictionary<int, string?> { [7] = Python };
        var resolver = new Resolver(images);
        var set = new TrackedImageSet();

        Assert.Equal([Python], set.Observe([7], resolver.Resolve));

        set.Observe([], resolver.Resolve);

        images[7] = Git;
        Assert.Equal([Git], set.Observe([7], resolver.Resolve));
    }

    /// <summary>Two processes running the same executable need only one rule.</summary>
    [Fact]
    public void DeduplicatesAcrossProcesses()
    {
        var resolver = new Resolver(new() { [1] = Python, [2] = Python });
        var set = new TrackedImageSet();

        Assert.Equal([Python], set.Observe([1, 2], resolver.Resolve));
    }

    [Fact]
    public void NormalisesBeforeComparing()
    {
        var resolver = new Resolver(new()
        {
            [1] = @"C:\ComfyUI\.venv\Scripts\python.exe",
            [2] = @"C:/COMFYUI/.venv/scripts/PYTHON.EXE",
        });

        Assert.Single(new TrackedImageSet().Observe([1, 2], resolver.Resolve));
    }

    /// <summary>A process that exits between the scan and the lookup is ordinary, not an error.</summary>
    [Fact]
    public void SkipsProcessesThatCannotBeResolved()
    {
        var resolver = new Resolver(new() { [1] = Python, [2] = null });

        Assert.Equal([Python], new TrackedImageSet().Observe([1, 2], resolver.Resolve));
    }

    [Fact]
    public void SkipsWindowsCriticalBinaries()
    {
        var resolver = new Resolver(new()
        {
            [1] = Python,
            [2] = @"c:\windows\system32\svchost.exe",
            [3] = @"c:\windows\explorer.exe",
        });

        Assert.Equal([Python], new TrackedImageSet().Observe([1, 2, 3], resolver.Resolve));
    }

    /// <summary>
    /// The interpreter is blocked before the process is launched, so the first scan must not
    /// report it again.
    /// </summary>
    [Fact]
    public void DoesNotRepeatExecutablesAlreadyBlockedBeforeLaunch()
    {
        var resolver = new Resolver(new() { [1] = Python, [2] = Git });
        var set = new TrackedImageSet();
        set.MarkReported([Python]);

        Assert.Equal([Git], set.Observe([1, 2], resolver.Resolve));
        Assert.Equal([Python, Git], set.Reported);
    }

    [Fact]
    public void ReportedKeepsDiscoveryOrder()
    {
        var resolver = new Resolver(new() { [1] = Python, [2] = Git });
        var set = new TrackedImageSet();

        set.Observe([1], resolver.Resolve);
        set.Observe([1, 2], resolver.Resolve);

        Assert.Equal([Python, Git], set.Reported);
    }

    [Fact]
    public void EmptyJobReportsNothing() =>
        Assert.Empty(new TrackedImageSet().Observe([], _ => Python));
}

/// <summary>
/// Tests for <see cref="TrackerSchedule"/>. Custom nodes do their spawning during import, so the
/// scan is frequent while that is happening and backs off once the server has settled.
/// </summary>
public sealed class TrackerScheduleTests
{
    [Fact]
    public void ScansFrequentlyDuringStartup()
    {
        Assert.Equal(TrackerSchedule.BusyInterval, TrackerSchedule.IntervalAt(TimeSpan.Zero));
        Assert.Equal(
            TrackerSchedule.BusyInterval, TrackerSchedule.IntervalAt(TimeSpan.FromSeconds(29)));
    }

    [Fact]
    public void BacksOffAfterTheBusyWindow()
    {
        Assert.Equal(
            TrackerSchedule.SteadyInterval, TrackerSchedule.IntervalAt(TrackerSchedule.BusyWindow));
        Assert.Equal(
            TrackerSchedule.SteadyInterval, TrackerSchedule.IntervalAt(TimeSpan.FromHours(3)));
    }

    /// <summary>
    /// The busy interval is the size of the window in which a newly started process can still
    /// open a socket. Keep it honest — if it ever grows past a second the claim in the docs
    /// stops being true.
    /// </summary>
    [Fact]
    public void BusyIntervalStaysShort() =>
        Assert.True(TrackerSchedule.BusyInterval <= TimeSpan.FromMilliseconds(250));
}
