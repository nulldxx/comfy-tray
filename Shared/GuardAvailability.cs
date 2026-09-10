namespace ComfyTray;

/// <summary>
/// Whether the guard service can be reached, and if not, why.
///
/// <para>
/// The distinctions matter because the tray says different things for each. "Not installed" is
/// an invitation to install it; "stopped" is a service to start; "version mismatch" is an
/// upgrade that went half-done. A single "unavailable" would leave the user guessing.
/// </para>
/// </summary>
internal enum GuardAvailability
{
    /// <summary>Not looked yet.</summary>
    Unknown = 0,

    /// <summary>No such service is registered. The guard package was declined or never installed.</summary>
    NotInstalled,

    /// <summary>Registered but not answering — stopped, disabled, or failed to start.</summary>
    Stopped,

    /// <summary>Answering, but speaking a protocol this tray does not.</summary>
    VersionMismatch,

    /// <summary>Reachable and usable.</summary>
    Connected,
}
