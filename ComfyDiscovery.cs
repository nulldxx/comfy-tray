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
    /// A managed "Standalone" instance. The current app keeps these under
    /// <c>%LOCALAPPDATA%\Comfy-Desktop\ComfyUI-Installs\&lt;name&gt;</c> as a self-contained
    /// <c>ComfyUI</c> checkout with an in-tree <c>.venv</c>; older instances lived under
    /// <c>%USERPROFILE%\ComfyUI-Installs</c> with the <c>.venv</c> beside the checkout.
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
    string? InputDirectory,
    string? OutputDirectory,
    bool HasManager,
    string Source);

/// <summary>
/// Locates ComfyUI installations dynamically rather than assuming a single fixed layout.
///
/// <para>
/// Comfy Desktop is a moving target: v1 recorded a single <c>basePath</c> in
/// <c>%APPDATA%\ComfyUI\config.json</c> and ran <c>ComfyUI/main.py</c> via a uv-managed
/// <c>.venv\Scripts\python.exe</c>; the current "Comfy Desktop 2" app manages multiple instances
/// under <c>%LOCALAPPDATA%\Comfy-Desktop\ComfyUI-Installs\&lt;name&gt;</c>, each a self-contained
/// <c>ComfyUI</c> checkout whose <c>.venv</c> and data folders live inside the checkout itself.
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
    public static ComfyInstallation? DiscoverBest()
    {
        var all = DiscoverAll(out _);
        return all.Count > 0 ? all[0] : null;
    }

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

        // 2. Well-known 2026 managed "Standalone" instances. The current app ships each instance as a
        //    self-contained checkout with an in-tree .venv (TryInstanceCheckout); older managed
        //    instances kept the .venv one level above the checkout (TryDesktopBase). Try the current
        //    shape first, then fall back.
        foreach (var root in standaloneInstanceRoots)
        {
            var source = $"Managed instance: {root}";
            Add(found,
                TryInstanceCheckout(root, desktopExtraModelPaths, source, trace)
                ?? TryDesktopBase(
                    root, ComfyInstallKind.DesktopStandalone, bundledMainScript: null, bundledFrontEndRoot: null,
                    desktopExtraModelPaths, source, trace));
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
                InputDirectory: null,
                OutputDirectory: null,
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
                InputDirectory: null,
                OutputDirectory: null,
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
            InputDirectory: null,
            OutputDirectory: null,
            HasManager: HasManager(baseDir),
            Source: $"Portable install: {root}");
    }

    /// <summary>
    /// Probes the current (2026 "Comfy Desktop 2") managed-instance layout, where an instance root
    /// under <c>%LOCALAPPDATA%\Comfy-Desktop\ComfyUI-Installs\&lt;name&gt;</c> contains a self-contained
    /// <c>ComfyUI</c> git checkout: <c>main.py</c>, an in-tree uv <c>.venv</c>, and the data folders
    /// (<c>models</c>/<c>output</c>/<c>user</c>/<c>input</c>/<c>temp</c>/<c>custom_nodes</c>) all sit
    /// together in that one directory — so the checkout itself is the base directory.
    /// </summary>
    private static ComfyInstallation? TryInstanceCheckout(
        string instanceRoot, string? desktopExtraModelPaths, string source, List<string>? trace)
    {
        if (string.IsNullOrWhiteSpace(instanceRoot))
        {
            return null;
        }

        var checkout = Path.Combine(instanceRoot, "ComfyUI");
        var main = Path.Combine(checkout, "main.py");
        var python = Path.Combine(checkout, VenvPython);
        if (!Exists(main) || !Exists(python))
        {
            trace?.Add($"  [skip] {source}: no in-tree checkout ({checkout}) with .venv python + main.py");
            return null;
        }

        var (sharedInput, sharedOutput) = SharedIoDirectories(instanceRoot, trace);
        trace?.Add($"  [ok]   {source}: in-tree checkout .venv python + main.py");
        return new ComfyInstallation(
            ComfyInstallKind.DesktopStandalone, python, main, checkout,
            // Managed instances keep their models in the Desktop-managed shared folders, mapped in by
            // the Desktop shared_model_paths.yaml — so prefer that over any in-tree extra_model_paths.yaml.
            ExtraModelPathsIfPresent(checkout, desktopExtraModelPaths),
            FrontEndRoot: null,
            InputDirectory: sharedInput,
            OutputDirectory: sharedOutput,
            HasManager: HasManager(checkout),
            Source: source);
    }

    /// <summary>
    /// For a Desktop-managed instance, the input/output folders are shared across instances in a
    /// sibling <c>ComfyUI-Shared</c> directory:
    /// <c>…\Comfy-Desktop\ComfyUI-Installs\&lt;name&gt;</c> → <c>…\Comfy-Desktop\ComfyUI-Shared\{input,output}</c>,
    /// exactly as the Desktop app passes via <c>--input-directory</c>/<c>--output-directory</c>. Each is
    /// returned only when it exists on disk; a missing folder falls back to the checkout-relative default.
    /// </summary>
    private static (string? Input, string? Output) SharedIoDirectories(string instanceRoot, List<string>? trace)
    {
        // instanceRoot = …\ComfyUI-Installs\<name>; its grandparent is the Desktop root that also holds
        // ComfyUI-Shared. GetParent does pure path arithmetic (no IO), so this stays unit-testable.
        var desktopRoot = Directory.GetParent(instanceRoot)?.Parent;
        if (desktopRoot == null)
        {
            return (null, null);
        }

        var shared = Path.Combine(desktopRoot.FullName, "ComfyUI-Shared");
        var input = Path.Combine(shared, "input");
        var output = Path.Combine(shared, "output");
        var hasInput = DirExists(input);
        var hasOutput = DirExists(output);
        if (hasInput || hasOutput)
        {
            trace?.Add($"  [ok]   Shared I/O: {shared} (input={hasInput}, output={hasOutput})");
        }

        return (hasInput ? input : null, hasOutput ? output : null);
    }

    private static string? ExtraModelPathsIfPresent(string basePath, string? desktopExtraModelPaths)
    {
        // Prefer the Desktop-managed shared model-paths YAML; fall back to one beside the base path.
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
    private static string[] EnumerateStandaloneInstanceRoots(List<string> trace)
    {
        // Where the various Desktop generations keep their managed instances:
        //  - current "Comfy Desktop 2": %LOCALAPPDATA%\Comfy-Desktop\ComfyUI-Installs
        //  - product-named electron dirs (defensive): %LOCALAPPDATA%\<productName>\ComfyUI-Installs
        //  - older/legacy: %USERPROFILE%\ComfyUI-Installs
        var parents = new List<string>();

        var localAppData = SafeFolder(Environment.SpecialFolder.LocalApplicationData);
        if (localAppData != null)
        {
            parents.Add(Path.Combine(localAppData, "Comfy-Desktop", "ComfyUI-Installs"));
            foreach (var product in DesktopProductNames)
            {
                parents.Add(Path.Combine(localAppData, product, "ComfyUI-Installs"));
            }
        }

        var userProfile = SafeEnv("USERPROFILE");
        if (userProfile != null)
        {
            parents.Add(Path.Combine(userProfile, "ComfyUI-Installs"));
        }

        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parent in parents)
        {
            if (!seen.Add(parent))
            {
                continue;
            }

            try
            {
                if (Directory.Exists(parent))
                {
                    roots.AddRange(Directory.GetDirectories(parent));
                }
            }
            catch (Exception ex)
            {
                trace.Add($"  [warn] {parent}: {ex.Message}");
            }
        }

        return [.. roots];
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

    /// <summary>
    /// Locates the Desktop-managed "extra model paths" YAML that maps in the shared model directories.
    /// The current "Comfy Desktop 2" app writes <c>%APPDATA%\Comfy Desktop\shared_model_paths.yaml</c>;
    /// v1 wrote <c>%APPDATA%\ComfyUI\extra_models_config.yaml</c>. Both file names are probed under each
    /// known Desktop product directory; the first that exists wins.
    /// </summary>
    private static string? DesktopExtraModelPaths()
    {
        var appData = SafeFolder(Environment.SpecialFolder.ApplicationData);
        if (appData == null)
        {
            return null;
        }

        string[] fileNames = ["shared_model_paths.yaml", "extra_models_config.yaml"];
        foreach (var product in DesktopProductNames)
        {
            foreach (var fileName in fileNames)
            {
                var candidate = Path.Combine(appData, product, fileName);
                if (Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
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
