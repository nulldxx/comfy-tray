using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace ComfyTray;

internal enum ComfyState
{
    Stopped,
    Running,
}

/// <summary>
/// Owns the lifecycle of the headless ComfyUI server process: starting it with no
/// visible window, capturing its stdout/stderr into a bounded ring buffer, and
/// stopping it (including any child processes). Events may be raised on thread-pool
/// threads, so subscribers must marshal to the UI thread themselves.
/// </summary>
internal sealed class ComfyServerManager : IDisposable
{
    private const int MaxLogLines = 5000;

    private readonly object _gate = new();
    private readonly LinkedList<string> _log = new();

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:DisposableFieldsShouldBeDisposed", Justification = "Disposed via Stop(), which Dispose() calls.")]
    private Process? _process;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:DisposableFieldsShouldBeDisposed", Justification = "Disposed via Stop(), which Dispose() calls.")]
    private OutputWatcher? _outputWatcher;
    private bool _stopping;

    public ComfyState State { get; private set; } = ComfyState.Stopped;

    /// <summary>Raised whenever <see cref="State"/> changes.</summary>
    public event EventHandler<ComfyState>? StateChanged;

    /// <summary>Raised for each new log line (already appended to the buffer).</summary>
    public event EventHandler<string>? LogReceived;

    public bool IsRunning => State == ComfyState.Running;

    /// <summary>Snapshot of the current log buffer, oldest first.</summary>
    public IReadOnlyList<string> GetLogSnapshot()
    {
        lock (_gate)
        {
            return _log.ToList();
        }
    }

    /// <summary>
    /// Starts the server. Throws <see cref="InvalidOperationException"/> if the
    /// interpreter or script cannot be found, so the caller can surface a message.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "Any launch failure is wrapped and surfaced to the user.")]
    public void Start(ComfyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        lock (_gate)
        {
            if (_process != null)
            {
                return;
            }

            var python = config.ResolvedPythonPath;
            var script = config.ResolvedMainScript;

            if (!File.Exists(python))
            {
                throw new InvalidOperationException(
                    $"Python interpreter not found:\n{python}\n\nEdit {ComfyConfig.ConfigPath} to correct PythonPath.");
            }

            if (!File.Exists(script))
            {
                throw new InvalidOperationException(
                    $"ComfyUI main.py not found:\n{script}\n\nEdit {ComfyConfig.ConfigPath} to correct MainScript.");
            }

            var psi = new ProcessStartInfo
            {
                FileName = python,
                WorkingDirectory = config.ResolvedWorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            foreach (var arg in config.BuildArguments())
            {
                psi.ArgumentList.Add(arg);
            }

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += OnOutput;
            process.ErrorDataReceived += OnOutput;
            process.Exited += OnProcessExited;

            _stopping = false;
            AppendLog($"[comfy-tray] starting: {config.DescribeCommandLine()}");

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                process.Dispose();
                throw new InvalidOperationException($"Failed to start ComfyUI: {ex.Message}", ex);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            _process = process;
            SetStateLocked(ComfyState.Running);

            _outputWatcher = new OutputWatcher(config.ResolvedOutputDirectory, AppendLog);
            _outputWatcher.Start();
        }
    }

    /// <summary>Stops the server and its child processes. Safe to call when stopped.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "Shutdown is best-effort; failures are logged, not thrown.")]
    public void Stop()
    {
        Process? process;
        OutputWatcher? watcher;
        lock (_gate)
        {
            process = _process;
            if (process == null)
            {
                return;
            }

            _stopping = true;
            _process = null;
            watcher = _outputWatcher;
            _outputWatcher = null;
        }

        watcher?.Stop();
        AppendLog("[comfy-tray] stopping server...");
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[comfy-tray] error stopping server: {ex.Message}");
        }
        finally
        {
            process.Dispose();
        }

        lock (_gate)
        {
            SetStateLocked(ComfyState.Stopped);
        }
    }

    private void OnOutput(object sender, DataReceivedEventArgs e)
    {
        if (e.Data != null)
        {
            AppendLog(e.Data);
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        OutputWatcher? watcher;
        lock (_gate)
        {
            // Stop() handles its own state transition; ignore the resulting Exited.
            // ReferenceEquals guards against a stale callback from a previous process
            // firing after Stop()+Start() have already replaced _process.
            if (_stopping || _process == null || !ReferenceEquals(sender, _process))
            {
                return;
            }

            int code = sender is Process p ? SafeExitCode(p) : -1;
            _process.Dispose();
            _process = null;
            AppendLog($"[comfy-tray] server exited unexpectedly (exit code {code}).");
            watcher = _outputWatcher;
            _outputWatcher = null;
            SetStateLocked(ComfyState.Stopped);
        }

        watcher?.Stop();
    }

    private static int SafeExitCode(Process p)
    {
        try
        {
            return p.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private void AppendLog(string line)
    {
        lock (_gate)
        {
            _log.AddLast(line);
            while (_log.Count > MaxLogLines)
            {
                _log.RemoveFirst();
            }
        }

        LogReceived?.Invoke(this, line);
    }

    private void SetStateLocked(ComfyState newState)
    {
        if (State == newState)
        {
            return;
        }

        State = newState;
        StateChanged?.Invoke(this, newState);
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
