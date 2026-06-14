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
    private static readonly TimeSpan DeleteDelay = TimeSpan.FromSeconds(10);

    private readonly string _directory;
    private readonly Action<string> _log;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _cts;

    internal OutputWatcher(string directory, Action<string> log)
    {
        _directory = directory;
        _log = log;
    }

    internal void Start()
    {
        if (!Directory.Exists(_directory))
        {
            _log($"[comfy-tray] output watcher: directory not found, skipping: {_directory}");
            return;
        }

        _cts = new CancellationTokenSource();
        _watcher = new FileSystemWatcher(_directory)
        {
            NotifyFilter = NotifyFilters.FileName,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };
        _watcher.Created += OnCreated;
        _watcher.Error += OnError;

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
            _log("[comfy-tray] output watcher stopped.");
        }
    }

    public void Dispose() => Stop();

    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        var token = _cts?.Token ?? CancellationToken.None;
        var path = e.FullPath;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DeleteDelay, token).ConfigureAwait(false);
                File.Delete(path);
                _log($"[comfy-tray] deleted output file: {Path.GetFileName(path)}");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log($"[comfy-tray] could not delete {Path.GetFileName(path)}: {ex.Message}");
            }
        }, CancellationToken.None);
    }

    private void OnError(object sender, ErrorEventArgs e) =>
        _log($"[comfy-tray] output watcher error: {e.GetException()?.Message}");
}
