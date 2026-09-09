using System;
using System.Collections.Generic;

namespace ComfyTray;

/// <summary>
/// Best-effort suppression of outbound network access for the ComfyUI server process.
///
/// <para>
/// ComfyUI runs a large ecosystem of third-party custom nodes, any of which may fetch
/// models, check for updates or phone home. In tray mode the server only ever needs its
/// inbound port; nothing it does requires reaching out. This class stacks the child
/// process's environment against egress before it is launched.
/// </para>
///
/// <para>
/// <b>This is not a security boundary.</b> It works by setting environment variables that
/// well-behaved HTTP libraries honour (<c>requests</c>, <c>httpx</c>, <c>urllib</c>,
/// <c>huggingface_hub</c>, <c>pip</c>). A node that opens a raw socket walks straight past
/// it. Enforcing egress properly needs a Windows Firewall rule, which is machine-wide and
/// requires administrator rights — and would have to be scoped to <c>python.exe</c>, which
/// is a generic interpreter rather than anything specific to ComfyUI. The trade here is
/// deliberate: these variables are set on the <see cref="System.Diagnostics.ProcessStartInfo"/>
/// itself, so they apply to this server process and its children and to nothing else on
/// the machine.
/// </para>
/// </summary>
internal static class NetworkIsolation
{
    /// <summary>
    /// Proxy address handed to the child. Port 9 (discard) on the loopback adapter has
    /// nothing listening, so a connection attempt is refused immediately instead of
    /// hanging until a timeout expires — callers fail fast with a clear error rather than
    /// stalling a workflow for minutes.
    /// </summary>
    public const string DeadProxy = "http://127.0.0.1:9";

    /// <summary>
    /// Hosts exempted from <see cref="DeadProxy"/>. ComfyUI and its front-end talk to the
    /// server over loopback, and the tray polls its <c>/history</c> endpoint, so loopback
    /// must keep working. Windows exempts loopback from firewall filtering for the same
    /// reason.
    /// </summary>
    public const string ProxyExceptions = "127.0.0.1,localhost,::1";

    /// <summary>
    /// The variables applied to the child process, in the order they are set.
    /// </summary>
    private static readonly (string Name, string Value)[] Variables =
    [
        // Proxy variables, in both casings. requests/urllib3 accept either, while
        // urllib.request.getproxies_environment() looks for the lower-case spelling first.
        ("HTTP_PROXY", DeadProxy),
        ("http_proxy", DeadProxy),
        ("HTTPS_PROXY", DeadProxy),
        ("https_proxy", DeadProxy),
        ("ALL_PROXY", DeadProxy),
        ("all_proxy", DeadProxy),
        ("FTP_PROXY", DeadProxy),
        ("ftp_proxy", DeadProxy),
        ("NO_PROXY", ProxyExceptions),
        ("no_proxy", ProxyExceptions),

        // Offline switches. Where a library honours one of these it is strictly better than
        // the dead proxy: it skips the network entirely rather than attempting a connection
        // and reporting a failure. huggingface_hub in particular is what most model-fetching
        // custom nodes go through.
        ("HF_HUB_OFFLINE", "1"),
        ("HF_DATASETS_OFFLINE", "1"),
        ("TRANSFORMERS_OFFLINE", "1"),
        ("HF_HUB_DISABLE_TELEMETRY", "1"),
        ("DO_NOT_TRACK", "1"),
        ("GRADIO_ANALYTICS_ENABLED", "False"),

        // pip is the usual route to PyPI mid-session, because ComfyUI-Manager shells out to
        // it when installing or updating a custom node. No index means no download.
        ("PIP_NO_INDEX", "1"),
        ("PIP_DISABLE_PIP_VERSION_CHECK", "1"),
        ("PIP_NO_INPUT", "1"),
    ];

    /// <summary>How many variables <see cref="Apply"/> sets. Used for the launch log line.</summary>
    public static int VariableCount => Variables.Length;

    /// <summary>
    /// Writes the isolation variables into <paramref name="environment"/>, overwriting any
    /// existing value. Intended for <see cref="System.Diagnostics.ProcessStartInfo.Environment"/>,
    /// which is pre-populated from the tray's own environment — so a proxy the user has set
    /// machine-wide is replaced for the child rather than left to compete with ours.
    /// </summary>
    public static void Apply(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        foreach (var (name, value) in Variables)
        {
            environment[name] = value;
        }
    }
}
