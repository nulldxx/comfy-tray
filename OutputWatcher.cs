using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyTray;

/// <summary>
/// Watches a directory for new files and deletes each one after a fixed delay.
/// Used for both the ComfyUI output and input folders, which differ only in their
/// delete delay and the label that appears in the log. Thread-safe; safe to
/// Start/Stop from any thread.
///
/// Also watches the parent directory so that if the target folder is deleted and
/// re-created (e.g. by ComfyUI resetting its workspace), the inner watcher is
/// automatically restarted and the folder is swept again after a short delay.
///
/// As a safety net against a FileSystemWatcher that has silently stopped raising
/// events, the inner watcher is also torn down and restarted on a fixed interval.
/// Each restart re-enumerates the directory and schedules every file present for
/// deletion, so a missed event is recovered on the next sweep.
/// </summary>
internal sealed class OutputWatcher : IDisposable
{
    private static readonly TimeSpan DefaultDeleteDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultPeriodicRestartInterval = TimeSpan.FromMinutes(10);

    private readonly string _directory;
    private readonly string? _parentDirectory;
    private readonly string _directoryName;
    private readonly Action<string> _log;
    private readonly TimeSpan _deleteDelay;
    private readonly TimeSpan _periodicRestartInterval;
    private readonly string _name;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:DisposableFieldsShouldBeDisposed", Justification = "Disposed via Stop(), which Dispose() calls.")]
    private FileSystemWatcher? _watcher;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:DisposableFieldsShouldBeDisposed", Justification = "Disposed via Stop(), which Dispose() calls.")]
    private CancellationTokenSource? _cts;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:DisposableFieldsShouldBeDisposed", Justification = "Disposed via Stop(), which Dispose() calls.")]
    private FileSystemWatcher? _parentWatcher;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:DisposableFieldsShouldBeDisposed", Justification = "Disposed via Stop(), which Dispose() calls.")]
    private CancellationTokenSource? _restartCts;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:DisposableFieldsShouldBeDisposed", Justification = "Disposed via Stop(), which Dispose() calls.")]
    private Timer? _periodicRestartTimer;

    internal OutputWatcher(string directory, Action<string> log, TimeSpan? deleteDelay = null, string name = "output", TimeSpan? periodicRestartInterval = null)
    {
        _directory = directory;
        _parentDirectory = Path.GetDirectoryName(directory);
        _directoryName = Path.GetFileName(directory);
        _log = log;
        _deleteDelay = deleteDelay ?? DefaultDeleteDelay;
        _periodicRestartInterval = periodicRestartInterval ?? DefaultPeriodicRestartInterval;
        _name = name;
    }

    internal void Start()
    {
        StartInnerWatcher();
        StartParentWatcher();
        _periodicRestartTimer = new Timer(_ => PeriodicRestart(), null, _periodicRestartInterval, _periodicRestartInterval);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "A failure to create the watch directory must never crash the tray; it is logged and the watcher is skipped.")]
    private void StartInnerWatcher()
    {
        // ComfyUI creates these directories lazily, so one may not exist at the instant
        // we launch the server. Create it up front rather than skipping — otherwise the
        // watcher would no-op for the whole session.
        try
        {
            Directory.CreateDirectory(_directory);
        }
        catch (Exception ex)
        {
            _log($"[comfy-tray] {_name} watcher: could not create directory, skipping: {_directory} ({ex.Message})");
            return;
        }

        var cts = new CancellationTokenSource();
        var watcher = new FileSystemWatcher(_directory)
        {
            NotifyFilter = NotifyFilters.FileName,
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
        };
        watcher.Created += OnCreated;
        watcher.Renamed += OnRenamed;
        watcher.Error += OnError;

        _cts = cts;
        _watcher = watcher;

        // Delete files that accumulated before this session.
        var token = cts.Token;
        foreach (var file in Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories))
        {
            _ = DeleteAfterDelayAsync(file, token);
        }

        _log($"[comfy-tray] {_name} watcher started: {_directory}");
    }

    private void StopInnerWatcher()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        cts?.Cancel();
        cts?.Dispose();

        var watcher = Interlocked.Exchange(ref _watcher, null);
        if (watcher != null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "Parent watcher setup is best-effort.")]
    private void StartParentWatcher()
    {
        if (_parentDirectory == null || !Directory.Exists(_parentDirectory)) return;

        try
        {
            var parentWatcher = new FileSystemWatcher(_parentDirectory)
            {
                NotifyFilter = NotifyFilters.DirectoryName,
                EnableRaisingEvents = true,
            };
            parentWatcher.Deleted += OnParentDeleted;
            parentWatcher.Created += OnParentCreated;
            parentWatcher.Error += OnParentError;
            _parentWatcher = parentWatcher;
        }
        catch (Exception ex)
        {
            _log($"[comfy-tray] {_name} parent watcher: could not start: {ex.Message}");
        }
    }

    internal void Stop()
    {
        var periodicRestartTimer = Interlocked.Exchange(ref _periodicRestartTimer, null);
        periodicRestartTimer?.Dispose();

        CancelRestart();

        var parentWatcher = Interlocked.Exchange(ref _parentWatcher, null);
        if (parentWatcher != null)
        {
            parentWatcher.EnableRaisingEvents = false;
            parentWatcher.Dispose();
        }

        var hadWatcher = _watcher != null;
        StopInnerWatcher();
        if (hadWatcher)
        {
            SweepDirectory();
            _log($"[comfy-tray] {_name} watcher stopped.");
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "The periodic restart runs on a timer thread; any failure must be logged, never crash the tray.")]
    private void PeriodicRestart()
    {
        // The FileSystemWatcher occasionally stops delivering events without raising
        // an error. Tear it down and start a fresh one; StartInnerWatcher re-enumerates
        // the directory and schedules every file present for deletion, so anything the
        // dead watcher missed is swept up here.
        try
        {
            // Skip if Stop() (or a parent-directory delete) has paused us; the timer
            // is disposed by Stop(), but a callback may already be in flight.
            if (_periodicRestartTimer == null) return;
            _log($"[comfy-tray] {_name} watcher: periodic restart + sweep.");
            StopInnerWatcher();
            StartInnerWatcher();
        }
        catch (Exception ex)
        {
            _log($"[comfy-tray] {_name} watcher periodic restart failed: {ex.Message}");
        }
    }

    private void CancelRestart()
    {
        var restartCts = Interlocked.Exchange(ref _restartCts, null);
        restartCts?.Cancel();
        restartCts?.Dispose();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "Shutdown sweep is best-effort.")]
    private void SweepDirectory()
    {
        if (!Directory.Exists(_directory)) return;
        foreach (var file in Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(file);
                _log($"[comfy-tray] deleted {_name} file: {Path.GetFileName(file)}");
            }
            catch (Exception ex)
            {
                _log($"[comfy-tray] could not delete {Path.GetFileName(file)}: {ex.Message}");
            }
        }
    }

    public void Dispose() => Stop();

    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        var cts = _cts;
        if (cts == null) return;
        _ = DeleteAfterDelayAsync(e.FullPath, cts.Token);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        var cts = _cts;
        if (cts == null) return;
        _ = DeleteAfterDelayAsync(e.FullPath, cts.Token);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        _log($"[comfy-tray] {_name} watcher error: {e.GetException()?.Message}");
        // The watched directory may have been deleted; stop cleanly so the parent
        // watcher can trigger a restart when it re-appears.
        StopInnerWatcher();
    }

    private void OnParentDeleted(object sender, FileSystemEventArgs e)
    {
        if (!string.Equals(e.Name, _directoryName, StringComparison.OrdinalIgnoreCase)) return;
        _log($"[comfy-tray] {_name} directory deleted, watcher paused.");
        StopInnerWatcher();
        CancelRestart();
    }

    private void OnParentCreated(object sender, FileSystemEventArgs e)
    {
        if (!string.Equals(e.Name, _directoryName, StringComparison.OrdinalIgnoreCase)) return;
        if (_watcher != null) return; // already watching, nothing to do
        _log($"[comfy-tray] {_name} directory re-appeared, restarting watcher in {RestartDelay.TotalSeconds:0}s.");
        ScheduleRestart();
    }

    private void OnParentError(object sender, ErrorEventArgs e) =>
        _log($"[comfy-tray] {_name} parent watcher error: {e.GetException()?.Message}");

    private void ScheduleRestart()
    {
        CancelRestart();
        var cts = new CancellationTokenSource();
        _restartCts = cts;
        _ = RestartAfterDelayAsync(cts.Token);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "Restart is best-effort.")]
    private async Task RestartAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(RestartDelay, token).ConfigureAwait(false);
            if (_watcher != null) return; // raced with another restart
            _log($"[comfy-tray] restarting {_name} watcher after directory re-appeared.");
            StartInnerWatcher();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log($"[comfy-tray] {_name} watcher restart failed: {ex.Message}");
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "File deletion is best-effort; any failure (locked, already gone, permissions) is logged, not thrown.")]
    private async Task DeleteAfterDelayAsync(string path, CancellationToken token)
    {
        try
        {
            await Task.Delay(_deleteDelay, token).ConfigureAwait(false);
            File.Delete(path);
            _log($"[comfy-tray] deleted {_name} file: {Path.GetFileName(path)}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log($"[comfy-tray] could not delete {Path.GetFileName(path)}: {ex.Message}");
        }
    }
}
