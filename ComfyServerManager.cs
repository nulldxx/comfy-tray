using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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

    /// <summary>How often the ComfyUI prompt history is wiped while the server runs.</summary>
    private static readonly TimeSpan HistoryClearInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a before-start/after-stop hook is waited on. A hook that outruns this is left
    /// running in the background rather than holding the tray up indefinitely.
    /// </summary>
    private static readonly TimeSpan HookTimeout = TimeSpan.FromSeconds(30);

    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly object _gate = new();
    private readonly LinkedList<string> _log = new();

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:DisposableFieldsShouldBeDisposed", Justification = "Disposed via Stop(), which Dispose() calls.")]
    private Process? _process;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:DisposableFieldsShouldBeDisposed", Justification = "Disposed via Stop(), which Dispose() calls.")]
    private OutputWatcher? _outputWatcher;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:DisposableFieldsShouldBeDisposed", Justification = "Disposed via Stop(), which Dispose() calls.")]
    private OutputWatcher? _inputWatcher;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:DisposableFieldsShouldBeDisposed", Justification = "Disposed via Stop(), which Dispose() calls.")]
    private OutputWatcher? _tempWatcher;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:DisposableFieldsShouldBeDisposed", Justification = "Disposed via Stop(), which Dispose() calls.")]
    private Timer? _historyClearTimer;
    private ComfyConfig? _config;
    private int _port;
    private bool _purgeEnabled = true;
    private bool _stopping;

    public ComfyState State { get; private set; } = ComfyState.Stopped;

    /// <summary>
    /// The user's hook commands, run before the server starts and after it stops (including
    /// after an unexpected exit). Owned by the UI, which replaces the values when the
    /// configuration dialog is saved; null means no hooks are configured.
    /// </summary>
    public HookSettings? Hooks { get; set; }

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

            // The before-start hook is the user's chance to prepare the machine (mount a share,
            // free the GPU), so it runs to completion — bounded by HookTimeout — before the
            // server. The gate is held throughout: Start is short-lived and driven from the UI
            // thread, and holding it keeps a concurrent Stop from racing the launch.
            RunHook("before start", Hooks?.BeforeStartCommand);

            // Resolve the config we will actually launch. If the configured interpreter/script are
            // missing (e.g. a Desktop update relocated things), fall back to live discovery rather
            // than failing outright — and if that also finds nothing, surface exactly what was searched.
            var effective = ResolveLaunchable(config);
            var python = effective.ResolvedPythonPath;

            var psi = new ProcessStartInfo
            {
                FileName = python,
                WorkingDirectory = effective.ResolvedWorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            foreach (var arg in effective.BuildArguments())
            {
                psi.ArgumentList.Add(arg);
            }

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += OnOutput;
            process.ErrorDataReceived += OnOutput;
            process.Exited += OnProcessExited;

            _stopping = false;
            AppendLog($"[comfy-tray] starting: {effective.DescribeCommandLine()}");

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

            _config = effective;
            _port = effective.Port;
            _purgeEnabled = effective.PurgeOutputsAndHistory;
            if (_purgeEnabled)
            {
                StartPurgingLocked();
            }
        }
    }

    /// <summary>
    /// Returns the configuration to launch. If the requested config already points at an existing
    /// interpreter and <c>main.py</c> it is used unchanged. Otherwise discovery runs: the first valid
    /// installation found is used (carrying over the user's runtime preferences — host, port, purge,
    /// logging), and the switch is logged. If nothing is found, throws with the full search report.
    /// Caller must hold <see cref="_gate"/>.
    /// </summary>
    private ComfyConfig ResolveLaunchable(ComfyConfig requested)
    {
        if (File.Exists(requested.ResolvedPythonPath) && File.Exists(requested.ResolvedMainScript))
        {
            return requested;
        }

        AppendLog("[comfy-tray] configured interpreter or main.py missing; running discovery...");
        var installs = ComfyDiscovery.DiscoverAll(out var report);
        var best = installs.Count > 0 ? installs[0] : null;
        if (best == null)
        {
            throw new InvalidOperationException(
                "Could not find a runnable ComfyUI installation.\n\n" +
                $"Configured interpreter:\n{requested.ResolvedPythonPath}\n\n" +
                $"Configured main.py:\n{requested.ResolvedMainScript}\n\n" +
                $"Locations searched:\n{report}\n\n" +
                $"Edit {ComfyConfig.ConfigPath} to set PythonPath and MainScript manually.");
        }

        var effective = ComfyConfig.FromInstallation(best);
        // Preserve the user's runtime preferences; only the install paths are taken from discovery.
        effective.Host = requested.Host;
        effective.Port = requested.Port;
        effective.LogStdout = requested.LogStdout;
        effective.PurgeOutputsAndHistory = requested.PurgeOutputsAndHistory;
        effective.ExtraArguments = requested.ExtraArguments;
        AppendLog(
            $"[comfy-tray] discovered {best.Kind} installation ({best.Source}); " +
            $"launching {effective.ResolvedPythonPath}");
        return effective;
    }

    /// <summary>
    /// Starts the folder watchers and the prompt-history clear timer. Caller must
    /// hold <see cref="_gate"/> and have set <see cref="_config"/>.
    /// </summary>
    private void StartPurgingLocked()
    {
        var config = _config;
        if (config == null)
        {
            return;
        }

        // Give finished outputs (e.g. long video renders) 30 seconds before deletion,
        // matching the input/temp grace, so a still-flushing file isn't swept too soon.
        _outputWatcher = new OutputWatcher(
            config.ResolvedOutputDirectory, AppendLog, TimeSpan.FromSeconds(30));
        _outputWatcher.Start();

        // The input folder is cleaned the same way as the output folder, but files
        // linger for 30 seconds (e.g. long enough for a workflow to consume them).
        _inputWatcher = new OutputWatcher(
            config.ResolvedInputDirectory, AppendLog, TimeSpan.FromSeconds(30), name: "input");
        _inputWatcher.Start();

        // The temp folder holds preview/intermediate renders. Give it the same 30s
        // grace as the input folder so files still in use mid-generation aren't fought
        // over; anything left behind is cleared on the periodic sweep and on stop.
        _tempWatcher = new OutputWatcher(
            config.ResolvedTempDirectory, AppendLog, TimeSpan.FromSeconds(30), name: "temp");
        _tempWatcher.Start();

        // On the same cadence as the folder sweeps, wipe the server's prompt history
        // so old runs don't accumulate. Host is the bind address (often 0.0.0.0), so
        // we always connect back over the loopback interface.
        _historyClearTimer = new Timer(
            _ => _ = ClearHistoryAsync(), null, HistoryClearInterval, HistoryClearInterval);
    }

    /// <summary>
    /// Enables or disables purging — the output/input folder sweeps and prompt-history
    /// clearing. Takes effect immediately while the server is running and is honoured on
    /// the next start. Disabling stops watching without sweeping the current contents.
    /// </summary>
    public void SetPurgeEnabled(bool enabled)
    {
        OutputWatcher? outputWatcher = null;
        OutputWatcher? inputWatcher = null;
        OutputWatcher? tempWatcher = null;
        Timer? historyClearTimer = null;
        lock (_gate)
        {
            _purgeEnabled = enabled;
            if (_process == null)
            {
                return; // not running; honoured on the next Start
            }

            if (enabled)
            {
                if (_outputWatcher == null && _inputWatcher == null && _tempWatcher == null && _historyClearTimer == null)
                {
                    StartPurgingLocked();
                }
            }
            else
            {
                outputWatcher = _outputWatcher;
                _outputWatcher = null;
                inputWatcher = _inputWatcher;
                _inputWatcher = null;
                tempWatcher = _tempWatcher;
                _tempWatcher = null;
                historyClearTimer = _historyClearTimer;
                _historyClearTimer = null;
            }
        }

        historyClearTimer?.Dispose();
        // Disabling must not delete what the user is choosing to keep.
        outputWatcher?.Stop(sweep: false);
        inputWatcher?.Stop(sweep: false);
        tempWatcher?.Stop(sweep: false);
        AppendLog($"[comfy-tray] purging {(enabled ? "enabled" : "disabled")}.");
    }

    /// <summary>Stops the server and its child processes. Safe to call when stopped.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "Shutdown is best-effort; failures are logged, not thrown.")]
    public void Stop()
    {
        Process? process;
        OutputWatcher? outputWatcher;
        OutputWatcher? inputWatcher;
        OutputWatcher? tempWatcher;
        Timer? historyClearTimer;
        lock (_gate)
        {
            process = _process;
            if (process == null)
            {
                return;
            }

            _stopping = true;
            _process = null;
            outputWatcher = _outputWatcher;
            _outputWatcher = null;
            inputWatcher = _inputWatcher;
            _inputWatcher = null;
            tempWatcher = _tempWatcher;
            _tempWatcher = null;
            historyClearTimer = _historyClearTimer;
            _historyClearTimer = null;
        }

        historyClearTimer?.Dispose();
        outputWatcher?.Stop();
        inputWatcher?.Stop();
        tempWatcher?.Stop();
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

        // Run after the state change so the tray shows stopped while the hook is still working.
        RunHook("after stop", Hooks?.AfterStopCommand);
    }

    private void RunHook(string label, string? commandLine) =>
        HookCommand.Run(label, commandLine, AppendLog, HookTimeout);

    private void OnOutput(object sender, DataReceivedEventArgs e)
    {
        if (e.Data != null)
        {
            AppendLog(e.Data);
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        OutputWatcher? outputWatcher;
        OutputWatcher? inputWatcher;
        OutputWatcher? tempWatcher;
        Timer? historyClearTimer;
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
            outputWatcher = _outputWatcher;
            _outputWatcher = null;
            inputWatcher = _inputWatcher;
            _inputWatcher = null;
            tempWatcher = _tempWatcher;
            _tempWatcher = null;
            historyClearTimer = _historyClearTimer;
            _historyClearTimer = null;
            SetStateLocked(ComfyState.Stopped);
        }

        historyClearTimer?.Dispose();
        outputWatcher?.Stop();
        inputWatcher?.Stop();
        tempWatcher?.Stop();

        // The server has stopped, however it went; the hook fires for a crash as it does for Stop().
        RunHook("after stop", Hooks?.AfterStopCommand);
    }

    /// <summary>
    /// Wipes the ComfyUI server's prompt history via its HTTP API. Best-effort: a
    /// server that is still starting, already gone, or unreachable is logged, not thrown.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "History clear runs on a timer thread; any failure must be logged, never crash the tray.")]
    private async Task ClearHistoryAsync()
    {
        try
        {
            var uri = new Uri($"http://127.0.0.1:{_port}/history");
            using var content = new StringContent("{\"clear\":true}", Encoding.UTF8, "application/json");
            using var response = await HttpClient.PostAsync(uri, content).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                AppendLog("[comfy-tray] cleared ComfyUI history.");
            }
            else
            {
                AppendLog($"[comfy-tray] history clear returned HTTP {(int)response.StatusCode}.");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[comfy-tray] could not clear ComfyUI history: {ex.Message}");
        }
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
