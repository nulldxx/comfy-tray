namespace ComfyTray;

/// <summary>
/// How hard the tray tries to stop the ComfyUI process tree reaching the network.
///
/// <para>
/// The three states are cumulative rather than alternatives: <see cref="Firewall"/> applies
/// everything <see cref="EnvironmentOnly"/> does and adds enforcement on top. The environment
/// variables are not made redundant by the firewall — they are in place at
/// <c>CreateProcess</c> time, before any rule can exist, and they stop a well-behaved library
/// before it opens a socket rather than after.
/// </para>
/// </summary>
internal enum OutboundMode
{
    /// <summary>No restriction. ComfyUI reaches the network like any other program.</summary>
    None = 0,

    /// <summary>
    /// Environment variables only: a dead proxy plus the offline switches that the common
    /// Python HTTP and model-fetching libraries honour. Best effort — a node that opens a raw
    /// socket walks straight past it.
    /// </summary>
    EnvironmentOnly = 1,

    /// <summary>
    /// The environment variables plus Windows Firewall block rules on every executable in the
    /// process tree, created and removed by the guard service. Enforced rather than requested,
    /// but see the guard's documentation for the residual window between a process starting and
    /// its rule landing.
    /// </summary>
    Firewall = 2,
}
