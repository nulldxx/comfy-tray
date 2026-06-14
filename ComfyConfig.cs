using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SampleTrayApp;

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
        @"%APPDATA%\uv\python\cpython-3.12.9-windows-x86_64-none\python.exe";

    /// <summary>Path to ComfyUI's <c>main.py</c>.</summary>
    public string MainScript { get; set; } =
        @"%LOCALAPPDATA%\Programs\ComfyUI\resources\ComfyUI\main.py";

    /// <summary>ComfyUI base/data directory.</summary>
    public string BaseDirectory { get; set; } = @"%USERPROFILE%\Documents\ComfyUI";

    public string UserDirectory { get; set; } = @"%USERPROFILE%\Documents\ComfyUI\user";

    public string InputDirectory { get; set; } = @"%USERPROFILE%\Documents\ComfyUI\input";

    public string OutputDirectory { get; set; } = @"%USERPROFILE%\Documents\ComfyUI\output";

    public string FrontEndRoot { get; set; } =
        @"%LOCALAPPDATA%\Programs\ComfyUI\resources\ComfyUI\web_custom_versions\desktop_app";

    public string ExtraModelPathsConfig { get; set; } =
        @"%APPDATA%\ComfyUI\extra_models_config.yaml";

    /// <summary>
    /// SQLite database URL. When null/empty it is derived from <see cref="BaseDirectory"/>.
    /// </summary>
    public string? DatabaseUrl { get; set; }

    /// <summary>Address to bind. Defaults to all interfaces as requested.</summary>
    public string Host { get; set; } = "0.0.0.0";

    public int Port { get; set; } = 8000;

    public bool EnableManager { get; set; } = true;

    public bool LogStdout { get; set; } = true;

    /// <summary>
    /// Working directory for the server process. When null/empty it defaults to the
    /// directory containing <see cref="MainScript"/>.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Any additional raw arguments to append verbatim.</summary>
    public List<string> ExtraArguments { get; set; } = [];

    private static string Expand(string value) =>
        System.Environment.ExpandEnvironmentVariables(value);

    public string ResolvedPythonPath => Expand(PythonPath);

    public string ResolvedMainScript => Expand(MainScript);

    public string ResolvedWorkingDirectory =>
        string.IsNullOrWhiteSpace(WorkingDirectory)
            ? (Path.GetDirectoryName(ResolvedMainScript) ?? ".")
            : Expand(WorkingDirectory);

    private string ResolvedDatabaseUrl =>
        string.IsNullOrWhiteSpace(DatabaseUrl)
            ? "sqlite:///" + Expand(BaseDirectory).Replace('\\', '/') + "/user/comfyui.db"
            : Expand(DatabaseUrl);

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
            "--front-end-root", Expand(FrontEndRoot),
            "--base-directory", Expand(BaseDirectory),
            "--database-url", ResolvedDatabaseUrl,
            "--extra-model-paths-config", Expand(ExtraModelPathsConfig),
        };

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
                    return cfg;
                }

                loadError = "Config file was empty; using defaults.";
            }

            var defaults = new ComfyConfig();
            defaults.TrySave(out _);
            return defaults;
        }
        catch (System.Exception ex)
        {
            loadError = $"Failed to read config ({path}): {ex.Message}. Using defaults.";
            return new ComfyConfig();
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
