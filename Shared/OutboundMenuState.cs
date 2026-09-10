using System;
using System.Globalization;

namespace ComfyTray;

/// <summary>
/// Everything the tray's "Outbound network" submenu displays, worked out from the current
/// settings and the guard's state.
///
/// <para>
/// Separated from the menu itself because the interesting part is not the WPF — it is deciding
/// what to tell someone whose blocking is not doing what they think. A degraded firewall, a
/// service that was never installed, a service that is installed but stopped, and blocking that
/// is temporarily lifted all look identical from the outside if the wording is careless.
/// </para>
/// </summary>
/// <param name="Checked">Which of the three modes carries the tick.</param>
/// <param name="FirewallEnabled">
/// Whether the firewall option can be chosen. It stays selectable when the guard is missing, so
/// that choosing it produces an explanation rather than a control that does nothing for reasons
/// the user cannot see.
/// </param>
/// <param name="UnlockEnabled">Whether there is anything to lift right now.</param>
/// <param name="UnlockHeader">Label for the unlock item, counting down while lifted.</param>
/// <param name="StatusHeader">One line saying what the guard is actually doing.</param>
internal sealed record OutboundMenuState(
    OutboundMode Checked,
    bool FirewallEnabled,
    bool UnlockEnabled,
    string UnlockHeader,
    string StatusHeader)
{
    /// <summary>How long "Allow network" lifts blocking for.</summary>
    public static readonly TimeSpan UnlockDuration = TimeSpan.FromMinutes(10);

    public static OutboundMenuState For(
        OutboundMode mode,
        GuardAvailability availability,
        bool hasSession,
        DateTimeOffset? unlockUntilUtc,
        DateTimeOffset nowUtc,
        FirewallHealth? health = null)
    {
        var unlocked = unlockUntilUtc is { } until && until > nowUtc;

        return new OutboundMenuState(
            Checked: mode,
            FirewallEnabled: true,
            UnlockEnabled: hasSession,
            UnlockHeader: unlocked
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    "Re-block now ({0} left)",
                    Describe(unlockUntilUtc!.Value - nowUtc))
                : string.Format(
                    CultureInfo.CurrentCulture,
                    "Allow network for {0} minutes",
                    (int)UnlockDuration.TotalMinutes),
            StatusHeader: DescribeStatus(mode, availability, hasSession, unlocked, health));
    }

    private static string DescribeStatus(
        OutboundMode mode,
        GuardAvailability availability,
        bool hasSession,
        bool unlocked,
        FirewallHealth? health)
    {
        if (mode != OutboundMode.Firewall)
        {
            return mode == OutboundMode.None
                ? "Guard: not in use"
                : "Guard: not in use (best-effort blocking only)";
        }

        if (availability != GuardAvailability.Connected)
        {
            return "Guard: " + availability switch
            {
                GuardAvailability.NotInstalled => "not installed — re-run the installer to add it",
                GuardAvailability.Stopped => "installed but not running",
                GuardAvailability.VersionMismatch => "version mismatch — reinstall to match the tray",
                _ => "unknown",
            };
        }

        // Reached the guard, but it may still not be enforcing anything. A rule created while the
        // firewall is off is accepted and does nothing, and one created where group policy owns
        // the profile is ignored outright — both silent unless something says so.
        if (health is { IsFullyEffective: false })
        {
            return "Guard: connected, but NOT enforcing — see the log";
        }

        if (unlocked)
        {
            return "Guard: blocking temporarily lifted";
        }

        return hasSession ? "Guard: enforcing" : "Guard: ready (applies at next start)";
    }

    /// <summary>Renders a remaining duration the way a countdown should read.</summary>
    private static string Describe(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        return remaining.TotalMinutes >= 1
            ? string.Format(CultureInfo.CurrentCulture, "{0:%m}m {0:%s}s", remaining)
            : string.Format(CultureInfo.CurrentCulture, "{0}s", (int)remaining.TotalSeconds);
    }
}
