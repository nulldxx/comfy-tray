using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ComfyTray;

/// <summary>
/// Launch configuration for the ComfyUI server. Values may contain Windows
/// environment-variable tokens (e.g. <c>%LOCALAPPDATA%</c>); they are expanded
/// lazily when the command line is built, so the persisted JSON stays portable.
/// Defaults mirror the command line used by the ComfyUI Desktop install.
/// </summary>
internal sealed class ComfyConfig
{
    /// <summary>Full path to the Python interpreter that runs the server.</summary>
    public string PythonPath { get; set; } =
        @"%USERPROFILE%\Documents\ComfyUI\.venv\Scripts\python.exe";

    /// <summary>Path to ComfyUI's <c>main.py</c>.</summary>
    public string MainScript { get; set; } =
        @"%LOCALAPPDATA%\Programs\ComfyUI\resources\ComfyUI\main.py";

    /// <summary>ComfyUI base/data directory.</summary>
    public string BaseDirectory { get; set; } = @"%USERPROFILE%\Documents\ComfyUI";

    public string UserDirectory { get; set; } = @"%USERPROFILE%\Documents\ComfyUI\user";

    public string InputDirectory { get; set; } = @"%USERPROFILE%\Documents\ComfyUI\input";

    public string OutputDirectory { get; set; } = @"%USERPROFILE%\Documents\ComfyUI\output";

    /// <summary>
    /// Scratch directory for preview images and intermediate files written during
    /// generation. Swept like the input/output folders so partial renders don't linger.
    /// </summary>
    public string TempDirectory { get; set; } = @"%USERPROFILE%\Documents\ComfyUI\temp";

    public string FrontEndRoot { get; set; } =
        @"%LOCALAPPDATA%\Programs\ComfyUI\resources\ComfyUI\web_custom_versions\desktop_app";

    public string ExtraModelPathsConfig { get; set; } =
        @"%APPDATA%\ComfyUI\extra_models_config.yaml";

    /// <summary>
    /// Explicit SQLite database URL. When null/empty, no <c>--database-url</c> flag is passed and
    /// ComfyUI derives its own default under <see cref="UserDirectory"/>.
    /// </summary>
    public string? DatabaseUrl { get; set; }

    /// <summary>Address to bind. Defaults to all interfaces as requested.</summary>
    public string Host { get; set; } = "0.0.0.0";

    public int Port { get; set; } = 8000;

    public bool EnableManager { get; set; } = true;

    public bool LogStdout { get; set; } = true;

    /// <summary>
    /// When true (the default), the output and input folders are swept and the
    /// ComfyUI prompt history is cleared periodically while the server runs.
    /// Toggled from the tray menu.
    /// </summary>
    public bool PurgeOutputsAndHistory { get; set; } = true;

    /// <summary>
    /// When true, ComfyUI is stopped when another user takes over the physical console
    /// (fast user switching / another logon), and restarted when this session returns —
    /// only if it was running at the time. Default off. Toggled from the tray menu.
    /// </summary>
    public bool WatchForUserLogon { get; set; }

    /// <summary>
    /// When true, the server process is launched with its environment set against outbound
    /// network access (see <see cref="NetworkIsolation"/>). Default off. Toggled from the
    /// tray menu; takes effect the next time ComfyUI starts.
    /// </summary>
    public bool BlockOutboundNetwork { get; set; }

    /// <summary>
    /// When true — and only when <see cref="BlockOutboundNetwork"/> is also true — the ComfyTray
    /// Guard service is asked to add Windows Firewall block rules for the ComfyUI process tree,
    /// turning best-effort isolation into enforcement. Default off. Has no effect when the guard
    /// service is not installed, in which case the environment variables still apply.
    /// </summary>
    public bool EnforceWithFirewall { get; set; }

    /// <summary>
    /// When true, ComfyUI refuses to start unless firewall enforcement is actually in force.
    /// Default off, deliberately: losing the guard mid-render and having the server killed under
    /// you is worse for most people than dropping back to environment-variable isolation and
    /// saying so. Turn it on when the enforcement matters more than the run.
    /// </summary>
    public bool RequireFirewallGuard { get; set; }

    /// <summary>
    /// The effective outbound policy, derived from the two flags above. Not persisted — the
    /// flags are the stored state, this is how the rest of the code reads them.
    /// </summary>
    [JsonIgnore]
    public OutboundMode Mode =>
        !BlockOutboundNetwork ? OutboundMode.None
        : EnforceWithFirewall ? OutboundMode.Firewall
        : OutboundMode.EnvironmentOnly;

    /// <summary>
    /// Working directory for the server process. When null/empty it defaults to the
    /// directory containing <see cref="MainScript"/>.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Any additional raw arguments to append verbatim.</summary>
    public List<string> ExtraArguments { get; set; } = [];

    /// <summary>
    /// Copies the user's runtime preferences from <paramref name="other"/>, leaving this
    /// instance's install paths untouched. Used when discovery has relocated ComfyUI: the
    /// paths come from the newly found installation, everything the user chose comes from
    /// the config they had.
    ///
    /// <para>
    /// Every settable property is either an install path or a runtime preference, and the
    /// preferences all belong here. A property added to this class and forgotten here would
    /// silently revert the first time ComfyUI moved, so
    /// <c>ComfyConfigTests.CopyRuntimePreferencesFrom_CopiesEveryNonInstallProperty</c>
    /// reflects over the type and fails if one goes missing.
    /// </para>
    /// </summary>
    public void CopyRuntimePreferencesFrom(ComfyConfig other)
    {
        System.ArgumentNullException.ThrowIfNull(other);

        Host = other.Host;
        Port = other.Port;
        EnableManager = other.EnableManager;
        LogStdout = other.LogStdout;
        PurgeOutputsAndHistory = other.PurgeOutputsAndHistory;
        WatchForUserLogon = other.WatchForUserLogon;
        BlockOutboundNetwork = other.BlockOutboundNetwork;
        EnforceWithFirewall = other.EnforceWithFirewall;
        RequireFirewallGuard = other.RequireFirewallGuard;
        ExtraArguments = other.ExtraArguments;
    }

    private static string Expand(string value) =>
        System.Environment.ExpandEnvironmentVariables(value);

    public string ResolvedPythonPath => Expand(PythonPath);

    public string ResolvedMainScript => Expand(MainScript);

    public string ResolvedInputDirectory => Expand(InputDirectory);

    public string ResolvedOutputDirectory => Expand(OutputDirectory);

    public string ResolvedTempDirectory => Expand(TempDirectory);

    public string ResolvedWorkingDirectory =>
        string.IsNullOrWhiteSpace(WorkingDirectory)
            ? (Path.GetDirectoryName(ResolvedMainScript) ?? ".")
            : Expand(WorkingDirectory);

    /// <summary>
    /// Builds the ordered argument list passed to Python. The first element is the
    /// main script; the rest are CLI flags. Paths are expanded; quoting is handled
    /// by <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>.
    /// </summary>
    public List<string> BuildArguments()
    {
        var args = new List<string>
        {
            ResolvedMainScript,
            "--user-directory", Expand(UserDirectory),
            "--input-directory", Expand(InputDirectory),
            "--output-directory", Expand(OutputDirectory),
            "--temp-directory", Expand(TempDirectory),
            "--base-directory", Expand(BaseDirectory),
        };

        // The following flags are newer and/or install-specific. They are emitted only when set (and,
        // where relevant, when the target exists) so a portable/older ComfyUI that doesn't recognise
        // them — or lacks the bundled Desktop front-end — still launches cleanly.
        var frontEnd = Expand(FrontEndRoot);
        if (!string.IsNullOrWhiteSpace(FrontEndRoot) && Directory.Exists(frontEnd))
        {
            args.Add("--front-end-root");
            args.Add(frontEnd);
        }

        var extraModels = Expand(ExtraModelPathsConfig);
        if (!string.IsNullOrWhiteSpace(ExtraModelPathsConfig) && File.Exists(extraModels))
        {
            args.Add("--extra-model-paths-config");
            args.Add(extraModels);
        }

        // Only pass an explicit --database-url when the user set one. Left unset, ComfyUI derives its
        // own default under --user-directory (the same location we used to compute), which avoids
        // handing the flag to a ComfyUI build too old to know it.
        if (!string.IsNullOrWhiteSpace(DatabaseUrl))
        {
            args.Add("--database-url");
            args.Add(Expand(DatabaseUrl));
        }

        if (LogStdout)
        {
            args.Add("--log-stdout");
        }

        args.Add("--listen");
        args.Add(Host);
        args.Add("--port");
        args.Add(Port.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (EnableManager)
        {
            args.Add("--enable-manager");
        }

        foreach (var extra in ExtraArguments)
        {
            args.Add(Expand(extra));
        }

        return args;
    }

    /// <summary>Human-readable command line, for display in logs/About only.</summary>
    public string DescribeCommandLine()
    {
        var parts = new List<string> { Quote(ResolvedPythonPath) };
        foreach (var a in BuildArguments())
        {
            parts.Add(Quote(a));
        }

        return string.Join(' ', parts);

        static string Quote(string s) => s.Contains(' ', System.StringComparison.Ordinal) ? $"\"{s}\"" : s;
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Location of the user-editable config file.</summary>
    public static string ConfigPath
    {
        get
        {
            var dir = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "ComfyTray");
            return Path.Combine(dir, "config.json");
        }
    }

    // Migrations: old default value → replaced by the current default on load.
    private static readonly (string Old, string New)[] PythonPathMigrations =
    [
        (@"%APPDATA%\uv\python\cpython-3.12.9-windows-x86_64-none\python.exe",
         @"%USERPROFILE%\Documents\ComfyUI\.venv\Scripts\python.exe"),
    ];

    /// <summary>
    /// Loads config from <see cref="ConfigPath"/>, creating it with defaults if it
    /// does not exist. Falls back to in-memory defaults if the file is unreadable.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "A bad config file must never crash the tray; fall back to defaults.")]
    public static ComfyConfig Load(out string? loadError)
    {
        loadError = null;
        var path = ConfigPath;
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<ComfyConfig>(json, SerializerOptions);
                if (cfg != null)
                {
                    cfg.Migrate();
                    return cfg;
                }

                loadError = "Config file was empty; using defaults.";
            }

            var defaults = FromDiscovery();
            defaults.TrySave(out _);
            return defaults;
        }
        catch (System.Exception ex)
        {
            loadError = $"Failed to read config ({path}): {ex.Message}. Using defaults.";
            return new ComfyConfig();
        }
    }

    /// <summary>
    /// Builds a starting config by <see cref="ComfyDiscovery">discovering</see> a real ComfyUI
    /// installation (portable or Desktop). Falls back to the historical hard-coded defaults when
    /// nothing is found, so behaviour never regresses on a machine where discovery can't help.
    /// </summary>
    public static ComfyConfig FromDiscovery()
    {
        var best = ComfyDiscovery.DiscoverBest();
        return best == null ? new ComfyConfig() : FromInstallation(best);
    }

    /// <summary>Maps a discovered installation onto a fresh, launchable config.</summary>
    internal static ComfyConfig FromInstallation(ComfyInstallation inst)
    {
        System.ArgumentNullException.ThrowIfNull(inst);
        var baseDir = inst.BaseDirectory;
        return new ComfyConfig
        {
            PythonPath = inst.PythonPath,
            MainScript = inst.MainScript,
            BaseDirectory = baseDir,
            UserDirectory = Path.Combine(baseDir, "user"),
            // Desktop-managed instances share input/output across instances; use those when discovery
            // found them, otherwise fall back to the checkout-relative defaults.
            InputDirectory = inst.InputDirectory ?? Path.Combine(baseDir, "input"),
            OutputDirectory = inst.OutputDirectory ?? Path.Combine(baseDir, "output"),
            TempDirectory = Path.Combine(baseDir, "temp"),
            FrontEndRoot = inst.FrontEndRoot ?? string.Empty,
            ExtraModelPathsConfig = inst.ExtraModelPathsConfig ?? string.Empty,
            EnableManager = inst.HasManager,
        };
    }

    /// <summary>
    /// True when <paramref name="value"/> is still the auto-derived <c>BaseDirectory\name</c> default,
    /// i.e. the user hasn't pointed it somewhere of their own.
    /// </summary>
    private bool IsDefaultSubdir(string value, string name) =>
        string.Equals(value, Path.Combine(BaseDirectory, name), System.StringComparison.OrdinalIgnoreCase);

    private void Migrate()
    {
        bool changed = false;
        foreach (var (old, next) in PythonPathMigrations)
        {
            if (string.Equals(PythonPath, old, System.StringComparison.OrdinalIgnoreCase))
            {
                PythonPath = next;
                changed = true;
                break;
            }
        }

        // Older builds (before shared-folder discovery) seeded Desktop-managed instances with an empty
        // ExtraModelPathsConfig and checkout-relative input/output dirs. That empty ExtraModelPathsConfig
        // is the tell: re-run discovery once and adopt the Desktop shared model-paths YAML plus the shared
        // input/output folders, so an existing config self-heals without hand-editing. Input/output are
        // only rewritten while they're still the untouched checkout-relative defaults, so a user who has
        // deliberately pointed them elsewhere is left alone.
        if (string.IsNullOrWhiteSpace(ExtraModelPathsConfig))
        {
            var discovered = ComfyDiscovery.DiscoverBest();
            if (discovered != null)
            {
                if (!string.IsNullOrWhiteSpace(discovered.ExtraModelPathsConfig))
                {
                    ExtraModelPathsConfig = discovered.ExtraModelPathsConfig;
                    changed = true;
                }

                if (!string.IsNullOrWhiteSpace(discovered.InputDirectory) && IsDefaultSubdir(InputDirectory, "input"))
                {
                    InputDirectory = discovered.InputDirectory;
                    changed = true;
                }

                if (!string.IsNullOrWhiteSpace(discovered.OutputDirectory) && IsDefaultSubdir(OutputDirectory, "output"))
                {
                    OutputDirectory = discovered.OutputDirectory;
                    changed = true;
                }
            }
        }

        if (changed)
        {
            TrySave(out _);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "Saving the config is best-effort; the error is returned to the caller.")]
    public bool TrySave(out string? error)
    {
        error = null;
        try
        {
            var path = ConfigPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, SerializerOptions));
            return true;
        }
        catch (System.Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
