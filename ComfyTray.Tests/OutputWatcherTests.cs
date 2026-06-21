using System;
using System.IO;
using System.Threading;
using Xunit;

namespace ComfyTray.Tests;

/// <summary>
/// Tests for <see cref="OutputWatcher"/>. The watcher is compiled directly into this
/// assembly (see the .csproj) so it can be exercised without the WPF host.
/// A short delete delay is injected so the timed deletion can be observed quickly.
/// </summary>
public sealed class OutputWatcherTests : IDisposable
{
    private static readonly TimeSpan ShortDelay = TimeSpan.FromMilliseconds(150);
    private readonly string _dir;

    public OutputWatcherTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "comfytray-tests-" + Guid.NewGuid().ToString("N"));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "Best-effort temp cleanup; a leaked temp dir must not fail the suite.")]
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; a leaked temp dir must not fail the suite.
        }
    }

    private OutputWatcher NewWatcher() => new(_dir, _ => { }, ShortDelay);

    /// <summary>Polls <paramref name="condition"/> until it is true or the timeout elapses.</summary>
    private static bool WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(25);
        }

        return condition();
    }

    [Fact]
    public void Start_CreatesDirectory_WhenItDoesNotExist()
    {
        Assert.False(Directory.Exists(_dir));

        using var watcher = NewWatcher();
        watcher.Start();

        Assert.True(Directory.Exists(_dir));
    }

    [Fact]
    public void Start_DeletesPreExistingFiles_AfterDelay()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "leftover.png");
        File.WriteAllText(file, "stale");

        using var watcher = NewWatcher();
        watcher.Start();

        Assert.True(WaitFor(() => !File.Exists(file)), "Pre-existing file should be deleted after the delay.");
    }

    [Fact]
    public void DeletesFile_CreatedAfterStart()
    {
        using var watcher = NewWatcher();
        watcher.Start();

        var file = Path.Combine(_dir, "fresh.png");
        File.WriteAllText(file, "data");

        Assert.True(WaitFor(() => !File.Exists(file)), "File created after Start should be deleted by the watcher.");
    }

    [Fact]
    public void DeletesFile_InSubdirectory()
    {
        using var watcher = NewWatcher();
        watcher.Start();

        var sub = Path.Combine(_dir, "2026-06-15");
        Directory.CreateDirectory(sub);
        var file = Path.Combine(sub, "nested.png");
        File.WriteAllText(file, "data");

        Assert.True(WaitFor(() => !File.Exists(file)), "File in a subdirectory should be deleted (IncludeSubdirectories).");
    }

    [Fact]
    public void Stop_SweepsRemainingFiles_Immediately()
    {
        Directory.CreateDirectory(_dir);
        // Long delay so the timed deletion will not have fired before Stop runs.
        using var watcher = new OutputWatcher(_dir, _ => { }, TimeSpan.FromMinutes(5));
        watcher.Start();

        var file = Path.Combine(_dir, "pending.png");
        File.WriteAllText(file, "data");

        watcher.Stop();

        Assert.False(File.Exists(file), "Stop should sweep any files still present.");
    }

    [Fact]
    public void DoesNotDeleteFiles_CreatedAfterStop()
    {
        using var watcher = NewWatcher();
        watcher.Start();
        watcher.Stop();

        var file = Path.Combine(_dir, "after-stop.png");
        File.WriteAllText(file, "data");

        // Give the watcher more than its delete delay to (incorrectly) act.
        Thread.Sleep(500);
        Assert.True(File.Exists(file), "A stopped watcher must not delete newly created files.");
    }

    [Fact]
    public void PeriodicRestart_SweepsFiles_MissedByADeadWatcher()
    {
        Directory.CreateDirectory(_dir);
        // Restart frequently and delete promptly so the periodic sweep is observable quickly.
        using var watcher = new OutputWatcher(
            _dir, _ => { }, deleteDelay: ShortDelay, periodicRestartInterval: TimeSpan.FromMilliseconds(200));
        watcher.Start();

        // Drop a file in without relying on the live FileSystemWatcher event; the periodic
        // restart re-enumerates the directory and should schedule it for deletion.
        var file = Path.Combine(_dir, "missed.png");
        File.WriteAllText(file, "data");

        Assert.True(WaitFor(() => !File.Exists(file)), "Periodic restart should sweep files present in the directory.");
    }

    [Fact]
    public void Start_IsIdempotentWithStop_AndDoesNotThrow()
    {
        using var watcher = NewWatcher();
        watcher.Start();
        watcher.Stop();
        watcher.Stop(); // second Stop is a no-op
        watcher.Start(); // restart after stop
        watcher.Stop();
    }
}
