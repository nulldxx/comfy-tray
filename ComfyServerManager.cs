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

    /// <summary>
    /// The connection to the ComfyTray Guard service, used when the outbound policy is
    /// <see cref="OutboundMode.Firewall"/>. Owned by the UI; null means firewall enforcement is
    /// unavailable and the server falls back to environment-variable isolation.
    /// </summary>
    public GuardClient? Guard { get; set; }

    /// <summary>
    /// The job object holding the ComfyUI process tree while firewall enforcement is on. Created
    /// kill-on-close, so disposing it takes the tree with it.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage", "CA2213:Disposable fields should be disposed",
        Justification = "Owned for the lifetime of a run; disposed by Stop and OnProcessExited.")]
    private JobObject? _job;

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage", "CA2213:Disposable fields should be disposed",
        Justification = "Owned for the lifetime of a run; disposed by Stop and OnProcessExited.")]
    private JobProcessTracker? _tracker;

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

            // Stack the child's environment against reaching out. Deliberately scoped to this
            // process (and whatever it spawns) rather than to the interpreter on disk: the same
            // python.exe is a general-purpose tool that must keep working everywhere else.
            if (effective.BlockOutboundNetwork)
            {
                NetworkIsolation.Apply(psi.Environment);
                AppendLog(
                    $"[comfy-tray] outbound blocking ON: {NetworkIsolation.VariableCount} environment " +
                    "variables set (dead proxy + offline switches). Best effort only — it stops " +
                    "libraries that honour proxy/offline settings, not a node using a raw socket. " +
                    "Loopback and the inbound port are unaffected.");
            }

            // Ask the guard for rules *before* the process exists. Windows Firewall filters new
            // connections, so a socket opened before its rule lands keeps working — and the
            // interpreter is both the overwhelmingly common exfiltration route and the one path
            // known ahead of time. Blocking it here closes that window completely; everything
            // discovered later is inherently a step behind.
            IReadOnlyList<string> preBlocked = [];
            if (effective.Mode == OutboundMode.Firewall)
            {
                preBlocked = StartGuardSessionLocked(effective);
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

            if (effective.Mode == OutboundMode.Firewall && Guard?.HasSession == true)
            {
                StartTrackingLocked(process, preBlocked);
            }

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
    /// Opens a guard session and blocks the interpreter before it runs. Caller must hold
    /// <see cref="_gate"/>.
    ///
    /// <para>
    /// Fails soft unless the user has asked otherwise. A missing or stopped guard service means
    /// the launch proceeds with environment-variable isolation and a log line saying so — killing
    /// a launch because a service is not running would be a poor trade for most people, and the
    /// isolation that remains is exactly what they had before this feature existed.
    /// <see cref="ComfyConfig.RequireFirewallGuard"/> reverses that for anyone who would rather
    /// not run at all than run unenforced.
    /// </para>
    /// </summary>
    private IReadOnlyList<string> StartGuardSessionLocked(ComfyConfig effective)
    {
        var guard = Guard;

        // The PID is not known yet — that is the point of blocking at this moment — so the
        // session is opened without one.
        if (guard != null && guard.TryBeginSession(comfyPid: 0, out _))
        {
            // Short paths are expanded first: a firewall rule matches the executable path
            // literally, so one created for C:\PROGRA~1\... never matches the process it meant.
            var images = PythonImagePaths.ForInterpreter(
                effective.ResolvedPythonPath, expandPath: TrayNativeMethods.GetLongPath);

            if (guard.TryBlockImages(images))
            {
                AppendLog(
                    $"[comfy-tray] firewall enforcement ON: {images.Count} interpreter image(s) " +
                    "blocked outbound before launch. Loopback and the inbound port are unaffected.");
                return images;
            }
        }

        var reason = guard is null ? "unavailable" : guard.Availability.ToString();
        if (effective.RequireFirewallGuard)
        {
            throw new InvalidOperationException(
                $"Firewall enforcement is required but the guard service is {reason}.\n\n" +
                "Install the ComfyTray Guard service, or turn off \"Require firewall enforcement\" " +
                "in the configuration.");
        }

        AppendLog(
            $"[comfy-tray] firewall enforcement requested but the guard service is {reason}; " +
            "falling back to environment-variable isolation only. Blocking is best effort until " +
            "the guard is available.");
        return [];
    }

    /// <summary>
    /// Puts the server into a job object and starts watching it for the executables custom nodes
    /// spawn. Caller must hold <see cref="_gate"/>.
    ///
    /// <para>
    /// Failing here is survivable, and says so rather than failing the launch: the interpreter is
    /// already blocked, so the common case is covered, and what is lost is the executables a node
    /// starts later.
    /// </para>
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "Tracking enhances pre-blocking; losing it is logged, never fatal to a launch.")]
    private void StartTrackingLocked(Process process, IReadOnlyList<string> preBlocked)
    {
        JobObject? job = null;
        try
        {
            // Kill-on-close ties the tree's life to the tray's. The guard drops its rules the
            // moment our pipe does, and a ComfyUI still running after that would be an unguarded
            // server the user believes is contained.
            job = new JobObject(killOnClose: true);
            job.Assign(process);

            var tracker = new JobProcessTracker(job, Guard!, AppendLog);
            tracker.Start(preBlocked);

            _job = job;
            _tracker = tracker;
        }
        catch (Exception ex)
        {
            job?.Dispose();
            AppendLog(
                $"[comfy-tray] could not track the ComfyUI process tree: {ex.Message}. " +
                "The interpreter is still blocked, but executables started by custom nodes " +
                "will not be.");
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
        effective.CopyRuntimePreferencesFrom(requested);
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
        JobProcessTracker? tracker;
        JobObject? job;
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
            tracker = _tracker;
            _tracker = null;
            job = _job;
            _job = null;
        }

        // Before the tree comes down, so a scan in flight cannot ask the guard to block
        // something that is already gone.
        tracker?.Dispose();

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

        // Closing the job kills anything the tree left behind, since it was created
        // kill-on-close. Belt and braces alongside the Kill above, not a replacement for it.
        job?.Dispose();

        // Last, so that nothing is ever left running unguarded in the gap between the rules
        // being removed and the process actually dying.
        Guard?.EndSession();

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
        JobProcessTracker? tracker;
        JobObject? job;
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
            tracker = _tracker;
            _tracker = null;
            job = _job;
            _job = null;
            SetStateLocked(ComfyState.Stopped);
        }

        // A crash is a teardown too: rules must not outlive the process they were created for.
        tracker?.Dispose();
        job?.Dispose();
        Guard?.EndSession();

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

    /// <summary>
    /// Writes a line into the same buffer and event the server's own output uses, so a component
    /// the UI owns — the guard client, say — shows up in the Logs window without its own plumbing.
    /// </summary>
    public void AppendExternalLog(string line) => AppendLog(line);

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
