using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyTray;

/// <summary>
/// Watches a directory for new files and deletes each one after a fixed delay.
/// Thread-safe; safe to Start/Stop from any thread.
/// </summary>
internal sealed class OutputWatcher : IDisposable
{
    private static readonly TimeSpan DefaultDeleteDelay = TimeSpan.FromSeconds(10);

    private readonly string _directory;
    private readonly Action<string> _log;
    private readonly TimeSpan _deleteDelay;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:DisposableFieldsShouldBeDisposed", Justification = "Disposed via Stop(), which Dispose() calls.")]
    private FileSystemWatcher? _watcher;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:DisposableFieldsShouldBeDisposed", Justification = "Disposed via Stop(), which Dispose() calls.")]
    private CancellationTokenSource? _cts;

    internal OutputWatcher(string directory, Action<string> log, TimeSpan? deleteDelay = null)
    {
        _directory = directory;
        _log = log;
        _deleteDelay = deleteDelay ?? DefaultDeleteDelay;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "A failure to create the watch directory must never crash the tray; it is logged and the watcher is skipped.")]
    internal void Start()
    {
        // ComfyUI creates its output directory lazily on the first image save, so it
        // often does not exist at the instant we launch the server. Create it up front
        // rather than skipping — otherwise the watcher would no-op for the whole session.
        try
        {
            Directory.CreateDirectory(_directory);
        }
        catch (Exception ex)
        {
            _log($"[comfy-tray] output watcher: could not create directory, skipping: {_directory} ({ex.Message})");
            return;
        }

        _cts = new CancellationTokenSource();
        _watcher = new FileSystemWatcher(_directory)
        {
            NotifyFilter = NotifyFilters.FileName,
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
        };
        _watcher.Created += OnCreated;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;

        // Delete files that accumulated before this session.
        var token = _cts.Token;
        foreach (var file in Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories))
        {
            _ = DeleteAfterDelayAsync(file, token);
        }

        _log($"[comfy-tray] output watcher started: {_directory}");
    }

    internal void Stop()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        cts?.Cancel();
        cts?.Dispose();

        var watcher = Interlocked.Exchange(ref _watcher, null);
        if (watcher != null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            SweepDirectory();
            _log("[comfy-tray] output watcher stopped.");
        }
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
                _log($"[comfy-tray] deleted output file: {Path.GetFileName(file)}");
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

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "File deletion is best-effort; any failure (locked, already gone, permissions) is logged, not thrown.")]
    private async Task DeleteAfterDelayAsync(string path, CancellationToken token)
    {
        try
        {
            await Task.Delay(_deleteDelay, token).ConfigureAwait(false);
            File.Delete(path);
            _log($"[comfy-tray] deleted output file: {Path.GetFileName(path)}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log($"[comfy-tray] could not delete {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    private void OnError(object sender, ErrorEventArgs e) =>
        _log($"[comfy-tray] output watcher error: {e.GetException()?.Message}");
}
