namespace ComfyTray;

/// <summary>
/// Whether the machine's firewall is in a state where the guard's rules will actually do
/// anything.
///
/// <para>
/// This exists because the worst outcome for a security feature is not failing — it is
/// appearing to work. A rule added while the firewall is switched off is accepted, stored and
/// enforces nothing; a rule added where group policy has turned off local policy merge is
/// ignored outright. Both are invisible unless something goes looking, so the guard reports
/// them and the tray says so in plain words rather than showing a reassuring tick.
/// </para>
/// </summary>
/// <param name="DomainProfileEnabled">Firewall on for the domain profile.</param>
/// <param name="PrivateProfileEnabled">Firewall on for the private profile.</param>
/// <param name="PublicProfileEnabled">Firewall on for the public profile.</param>
/// <param name="LocalRulesApply">
/// False when group policy has disabled local policy merge, meaning locally created rules —
/// including the guard's — are not evaluated at all.
/// </param>
internal sealed record FirewallHealth(
    bool DomainProfileEnabled,
    bool PrivateProfileEnabled,
    bool PublicProfileEnabled,
    bool LocalRulesApply)
{
    /// <summary>True when every profile is on and local rules are honoured.</summary>
    public bool IsFullyEffective =>
        DomainProfileEnabled && PrivateProfileEnabled && PublicProfileEnabled && LocalRulesApply;

    /// <summary>
    /// A short explanation of why enforcement is degraded, or null when it is not. Written for
    /// the tray log and the menu, so it names the fix rather than only the fault.
    /// </summary>
    public string? DegradedReason
    {
        get
        {
            if (!LocalRulesApply)
            {
                return "group policy has disabled local firewall rule merging, so rules created " +
                       "on this machine are ignored — blocking cannot be enforced";
            }

            if (IsFullyEffective)
            {
                return null;
            }

            var off = string.Join(", ", GetDisabledProfiles());
            return $"Windows Firewall is switched off for the {off} profile(s), so rules there " +
                   "enforce nothing — turn it on to make blocking effective";
        }
    }

    private string[] GetDisabledProfiles()
    {
        var disabled = new System.Collections.Generic.List<string>(3);
        if (!DomainProfileEnabled) { disabled.Add("domain"); }
        if (!PrivateProfileEnabled) { disabled.Add("private"); }
        if (!PublicProfileEnabled) { disabled.Add("public"); }
        return [.. disabled];
    }
}
