using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ComfyTray;

/// <summary>How a discovered ComfyUI installation is laid out on disk.</summary>
internal enum ComfyInstallKind
{
    /// <summary>Classic <c>ComfyUI_windows_portable</c>: embedded Python + <c>ComfyUI\main.py</c>.</summary>
    Portable,

    /// <summary>
    /// A 2026 "Standalone" managed instance under <c>%USERPROFILE%\ComfyUI-Installs\&lt;name&gt;</c>:
    /// a <c>.venv</c> and a <c>ComfyUI</c> git checkout side by side.
    /// </summary>
    DesktopStandalone,

    /// <summary>
    /// A Desktop base path (recorded as <c>basePath</c> in the app's <c>config.json</c>) whose
    /// <c>.venv</c> sits beside a <c>ComfyUI</c> checkout under the base directory.
    /// </summary>
    Desktop,

    /// <summary>
    /// Legacy v1 Desktop layout: a <c>.venv</c> under the base path, but the ComfyUI code shipped
    /// inside the Electron app's <c>resources\ComfyUI</c> directory (the layout the old hard-coded
    /// defaults targeted).
    /// </summary>
    DesktopBundled,
}

/// <summary>
/// A concrete, runnable ComfyUI installation: an interpreter and a <c>main.py</c> that both exist
/// on disk, plus the directories/flags needed to launch it. Produced by <see cref="ComfyDiscovery"/>.
/// </summary>
internal sealed record ComfyInstallation(
    ComfyInstallKind Kind,
    string PythonPath,
    string MainScript,
    string BaseDirectory,
    string? ExtraModelPathsConfig,
    string? FrontEndRoot,
    bool HasManager,
    string Source);

/// <summary>
/// Locates ComfyUI installations dynamically rather than assuming a single fixed layout.
///
/// <para>
/// Comfy Desktop is a moving target: v1 recorded a single <c>basePath</c> in
/// <c>%APPDATA%\ComfyUI\config.json</c> and ran <c>ComfyUI/main.py</c> via a uv-managed
/// <c>.venv\Scripts\python.exe</c>; the 2026 app manages multiple "Standalone" instances under
/// <c>%USERPROFILE%\ComfyUI-Installs</c> and shares models via <c>%USERPROFILE%\ComfyUI-Shared</c>.
/// Portable installs keep the classic <c>python_embeded</c> + <c>ComfyUI\main.py</c> shape.
/// Rather than track versions, discovery <em>detects capabilities</em>: a candidate is only
/// accepted when both an interpreter and a <c>main.py</c> exist.
/// </para>
///
/// <para>
/// The probe order follows documented sources first and heuristics last: the Desktop
/// <c>config.json</c> <c>basePath</c> (documented electron-store key) → well-known 2026 instance
/// and shared dirs → legacy well-known base paths → portable-layout scans. See the design notes
/// alongside this file for source citations.
/// </para>
/// </summary>
internal static class ComfyDiscovery
{
    // Relative sub-paths, kept in one place so the layout knowledge is auditable.
    private static readonly string VenvPython = Path.Combine(".venv", "Scripts", "python.exe");
    private static readonly string EmbeddedPython = Path.Combine("python_embeded", "python.exe");
    private static readonly string CheckoutMain = Path.Combine("ComfyUI", "main.py");
    private static readonly string ManagerNode = Path.Combine("custom_nodes", "ComfyUI-Manager");

    /// <summary>Electron <c>productName</c>s used across Desktop generations (→ <c>%APPDATA%\&lt;name&gt;</c>).</summary>
    private static readonly string[] DesktopProductNames = ["ComfyUI", "Comfy Desktop"];

    /// <summary>The best (highest-priority) discovered installation, or null if none was found.</summary>
    public static ComfyInstallation? DiscoverBest() => DiscoverAll(out _).FirstOrDefault();

    /// <summary>
    /// All valid installations in priority order, plus a human-readable <paramref name="report"/>
    /// of every location probed and why it was accepted or rejected — surfaced in diagnostics when
    /// discovery fails so the user can see exactly what was searched.
    /// </summary>
    public static IReadOnlyList<ComfyInstallation> DiscoverAll(out string report)
    {
        var trace = new List<string>();
        var results = DiscoverFrom(
            desktopBasePaths: ReadDesktopBasePaths(trace),
            standaloneInstanceRoots: EnumerateStandaloneInstanceRoots(trace),
            legacyBasePaths: LegacyBasePaths(),
            portableRoots: PortableRoots(),
            bundledMainScript: BundledMainScript(),
            bundledFrontEndRoot: BundledFrontEndRoot(),
            desktopExtraModelPaths: DesktopExtraModelPaths(),
            trace: trace);

        report = string.Join(Environment.NewLine, trace);
        return results;
    }

    /// <summary>
    /// Pure discovery core: given explicit candidate roots, probe each layout and return the valid,
    /// de-duplicated installations in priority order. Kept free of any Windows/environment lookups so
    /// it can be unit-tested against synthetic directory trees on any platform.
    /// </summary>
    internal static List<ComfyInstallation> DiscoverFrom(
        IEnumerable<string> desktopBasePaths,
        IEnumerable<string> standaloneInstanceRoots,
        IEnumerable<string> legacyBasePaths,
        IEnumerable<string> portableRoots,
        string? bundledMainScript,
        string? bundledFrontEndRoot,
        string? desktopExtraModelPaths,
        List<string>? trace)
    {
        var found = new List<ComfyInstallation>();

        // 1. Documented Desktop config (config.json -> basePath).
        foreach (var basePath in desktopBasePaths)
        {
            Add(found, TryDesktopBase(
                basePath, ComfyInstallKind.Desktop, bundledMainScript, bundledFrontEndRoot,
                desktopExtraModelPaths, source: $"Desktop config basePath: {basePath}", trace));
        }

        // 2. Well-known 2026 managed "Standalone" instances.
        foreach (var root in standaloneInstanceRoots)
        {
            Add(found, TryDesktopBase(
                root, ComfyInstallKind.DesktopStandalone, bundledMainScript: null, bundledFrontEndRoot: null,
                desktopExtraModelPaths, source: $"Managed instance: {root}", trace));
        }

        // 3. Legacy well-known base paths (Documents\ComfyUI, %USERPROFILE%\ComfyUI).
        foreach (var basePath in legacyBasePaths)
        {
            Add(found, TryDesktopBase(
                basePath, ComfyInstallKind.Desktop, bundledMainScript, bundledFrontEndRoot,
                desktopExtraModelPaths, source: $"Well-known base path: {basePath}", trace));
        }

        // 4. Portable-layout scans (heuristic; last resort).
        foreach (var root in portableRoots)
        {
            Add(found, TryPortable(root, trace));
        }

        return found;
    }

    /// <summary>Appends an installation if present and not already discovered at the same interpreter+script.</summary>
    private static void Add(List<ComfyInstallation> found, ComfyInstallation? candidate)
    {
        if (candidate == null)
        {
            return;
        }

        bool duplicate = found.Any(f =>
            string.Equals(f.PythonPath, candidate.PythonPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(f.MainScript, candidate.MainScript, StringComparison.OrdinalIgnoreCase));
        if (!duplicate)
        {
            found.Add(candidate);
        }
    }

    /// <summary>
    /// Probes a Desktop-style base path. The interpreter is the uv-managed <c>.venv</c> Python under
    /// the base; the script is either a <c>ComfyUI\main.py</c> checkout under the base or, for the
    /// legacy layout, the bundled copy in the Electron app resources.
    /// </summary>
    private static ComfyInstallation? TryDesktopBase(
        string basePath,
        ComfyInstallKind checkoutKind,
        string? bundledMainScript,
        string? bundledFrontEndRoot,
        string? desktopExtraModelPaths,
        string source,
        List<string>? trace)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return null;
        }

        var python = Path.Combine(basePath, VenvPython);
        if (!Exists(python))
        {
            trace?.Add($"  [skip] {source}: no venv python at {python}");
            return null;
        }

        var checkoutMain = Path.Combine(basePath, CheckoutMain);
        if (Exists(checkoutMain))
        {
            trace?.Add($"  [ok]   {source}: venv python + checkout main.py");
            return new ComfyInstallation(
                checkoutKind, python, checkoutMain, basePath,
                ExtraModelPathsIfPresent(basePath, desktopExtraModelPaths),
                FrontEndRoot: null,
                HasManager: HasManager(basePath),
                Source: source);
        }

        if (bundledMainScript != null && Exists(bundledMainScript))
        {
            trace?.Add($"  [ok]   {source}: venv python + bundled app-resources main.py");
            return new ComfyInstallation(
                ComfyInstallKind.DesktopBundled, python, bundledMainScript, basePath,
                ExtraModelPathsIfPresent(basePath, desktopExtraModelPaths),
                FrontEndRoot: bundledFrontEndRoot != null && DirExists(bundledFrontEndRoot) ? bundledFrontEndRoot : null,
                HasManager: HasManager(basePath),
                Source: source);
        }

        trace?.Add($"  [skip] {source}: venv python found but no main.py (checkout or bundled)");
        return null;
    }

    /// <summary>Probes a classic portable root: <c>python_embeded\python.exe</c> + <c>ComfyUI\main.py</c>.</summary>
    private static ComfyInstallation? TryPortable(string root, List<string>? trace)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        var python = Path.Combine(root, EmbeddedPython);
        var main = Path.Combine(root, CheckoutMain);
        if (!Exists(python) || !Exists(main))
        {
            trace?.Add($"  [skip] Portable: {root} (embedded python or ComfyUI\\main.py missing)");
            return null;
        }

        var baseDir = Path.Combine(root, "ComfyUI");
        var extra = Path.Combine(baseDir, "extra_model_paths.yaml");
        trace?.Add($"  [ok]   Portable: {root}");
        return new ComfyInstallation(
            ComfyInstallKind.Portable, python, main, baseDir,
            ExtraModelPathsConfig: Exists(extra) ? extra : null,
            FrontEndRoot: null,
            HasManager: HasManager(baseDir),
            Source: $"Portable install: {root}");
    }

    private static string? ExtraModelPathsIfPresent(string basePath, string? desktopExtraModelPaths)
    {
        // Prefer the Desktop-managed extra_models_config.yaml; fall back to one beside the base path.
        if (desktopExtraModelPaths != null && Exists(desktopExtraModelPaths))
        {
            return desktopExtraModelPaths;
        }

        var local = Path.Combine(basePath, "extra_model_paths.yaml");
        return Exists(local) ? local : null;
    }

    private static bool HasManager(string baseDir) => DirExists(Path.Combine(baseDir, ManagerNode));

    // --- Windows environment wiring (not exercised by the pure-core unit tests) ---

    /// <summary>
    /// Reads the Desktop electron-store <c>config.json</c> files and extracts any <c>basePath</c>.
    /// v1 productName is "ComfyUI" (→ <c>%APPDATA%\ComfyUI\config.json</c>); the 2026 app uses
    /// "Comfy Desktop" (→ <c>%APPDATA%\Comfy Desktop\config.json</c>). Both are probed defensively.
    /// </summary>
    private static IEnumerable<string> ReadDesktopBasePaths(List<string> trace)
    {
        var appData = SafeFolder(Environment.SpecialFolder.ApplicationData);
        if (appData == null)
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var product in DesktopProductNames)
        {
            var configPath = Path.Combine(appData, product, "config.json");
            var basePath = ReadBasePath(configPath, trace);
            if (basePath != null && seen.Add(basePath))
            {
                yield return basePath;
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "A malformed or unreadable Desktop config must never break discovery; it is logged and skipped.")]
    private static string? ReadBasePath(string configPath, List<string> trace)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("basePath", out var bp) &&
                bp.ValueKind == JsonValueKind.String)
            {
                var value = bp.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    trace.Add($"  [read] {configPath}: basePath = {value}");
                    return value;
                }
            }

            trace.Add($"  [read] {configPath}: no usable basePath");
            return null;
        }
        catch (Exception ex)
        {
            trace.Add($"  [warn] {configPath}: {ex.Message}");
            return null;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "Enumerating the instances directory is best-effort; failures are logged and ignored.")]
    private static IReadOnlyList<string> EnumerateStandaloneInstanceRoots(List<string> trace)
    {
        var userProfile = SafeEnv("USERPROFILE");
        if (userProfile == null)
        {
            return [];
        }

        var installs = Path.Combine(userProfile, "ComfyUI-Installs");
        try
        {
            return Directory.Exists(installs) ? Directory.GetDirectories(installs) : [];
        }
        catch (Exception ex)
        {
            trace.Add($"  [warn] {installs}: {ex.Message}");
            return [];
        }
    }

    private static IEnumerable<string> LegacyBasePaths()
    {
        var userProfile = SafeEnv("USERPROFILE");
        if (userProfile == null)
        {
            yield break;
        }

        yield return Path.Combine(userProfile, "Documents", "ComfyUI");
        yield return Path.Combine(userProfile, "ComfyUI");
    }

    private static IEnumerable<string> PortableRoots()
    {
        var userProfile = SafeEnv("USERPROFILE");
        if (userProfile != null)
        {
            yield return Path.Combine(userProfile, "ComfyUI_windows_portable");
            yield return Path.Combine(userProfile, "Documents", "ComfyUI_windows_portable");
            yield return Path.Combine(userProfile, "Downloads", "ComfyUI_windows_portable");
            yield return Path.Combine(userProfile, "Desktop", "ComfyUI_windows_portable");
        }

        // Common manual-extract locations. Cheap existence checks; flagged as heuristic.
        yield return @"C:\ComfyUI_windows_portable";
        yield return @"D:\ComfyUI_windows_portable";
    }

    private static string? BundledMainScript()
    {
        var localAppData = SafeFolder(Environment.SpecialFolder.LocalApplicationData);
        return localAppData == null
            ? null
            : Path.Combine(localAppData, "Programs", "ComfyUI", "resources", "ComfyUI", "main.py");
    }

    private static string? BundledFrontEndRoot()
    {
        var localAppData = SafeFolder(Environment.SpecialFolder.LocalApplicationData);
        return localAppData == null
            ? null
            : Path.Combine(localAppData, "Programs", "ComfyUI", "resources", "ComfyUI", "web_custom_versions", "desktop_app");
    }

    private static string? DesktopExtraModelPaths()
    {
        var appData = SafeFolder(Environment.SpecialFolder.ApplicationData);
        return appData == null ? null : Path.Combine(appData, "ComfyUI", "extra_models_config.yaml");
    }

    private static bool Exists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool DirExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static string? SafeEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? SafeFolder(Environment.SpecialFolder folder)
    {
        var value = Environment.GetFolderPath(folder);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
