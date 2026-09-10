using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ComfyTray;

/// <summary>
/// Naming scheme for the Windows Firewall rules the guard owns.
///
/// <para>
/// Two things have to be true of every rule the guard creates. It must be recognisable as
/// ours beyond doubt, because the cleanup paths delete in bulk and deleting somebody else's
/// firewall rule would be unforgivable. And it must be derivable from the session and the
/// image path alone, so that re-blocking a path the guard has already blocked is a no-op
/// rather than a duplicate.
/// </para>
///
/// <para>
/// A rule is ours only when its name carries <see cref="NamePrefix"/> <b>and</b> its grouping
/// is exactly <see cref="Group"/>. Nothing else is ever modified, disabled or removed.
/// </para>
/// </summary>
internal static class GuardRuleNaming
{
    /// <summary>
    /// The <c>Grouping</c> stamped on every rule. Windows Firewall documents this field as
    /// taking an indirect string (<c>@dll,-id</c>) for localisation; a plain literal is
    /// accepted and is what <c>netsh</c> and the PowerShell cmdlets write, and it is what makes
    /// the rules findable in <c>wf.msc</c> and in
    /// <c>netsh advfirewall firewall show rule group="ComfyTrayGuard"</c>.
    /// </summary>
    public const string Group = "ComfyTrayGuard";

    /// <summary>Prefix on every rule name. Half of the ownership test; the other half is <see cref="Group"/>.</summary>
    public const string NamePrefix = "ComfyTrayGuard-";

    /// <summary>Hex characters of the path digest kept in the rule name.</summary>
    private const int HashLength = 16;

    /// <summary>
    /// Builds the rule name for one image path within one session. Stable for a given
    /// (session, path) pair, so calling it twice yields the same name and the second block is
    /// recognised as already applied.
    /// </summary>
    /// <param name="sessionId">The session that owns the rule.</param>
    /// <param name="normalizedPath">A path already through <see cref="GuardPathSet.Normalize"/>.</param>
    public static string Compose(Guid sessionId, string normalizedPath)
    {
        ArgumentNullException.ThrowIfNull(normalizedPath);

        return string.Concat(
            NamePrefix,
            sessionId.ToString("N", CultureInfo.InvariantCulture),
            "-",
            HashPath(normalizedPath));
    }

    /// <summary>
    /// Recovers the owning session from a rule name. Returns false for any name that is not
    /// one of ours, which is what stops a bulk delete from straying.
    /// </summary>
    public static bool TryParse(string? ruleName, out Guid sessionId)
    {
        sessionId = Guid.Empty;

        if (ruleName is null || !ruleName.StartsWith(NamePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = ruleName[NamePrefix.Length..];

        // "<32 hex guid>-<16 hex digest>"
        if (remainder.Length != 32 + 1 + HashLength || remainder[32] != '-')
        {
            return false;
        }

        return Guid.TryParseExact(remainder[..32], "N", out sessionId);
    }

    /// <summary>
    /// The human-readable half of a rule. <c>wf.msc</c> shows this in the rule's properties and
    /// <c>netsh ... show rule</c> prints it, so it is where someone staring at a blocked
    /// interpreter finds out what put it there and when.
    /// </summary>
    public static string DescribeFor(Guid sessionId, string normalizedPath, DateTimeOffset createdUtc)
    {
        ArgumentNullException.ThrowIfNull(normalizedPath);

        // Windows Firewall rejects '|' in a name or description. Windows paths cannot contain
        // one, but the cost of being sure is a single Replace.
        var safePath = normalizedPath.Replace("|", string.Empty, StringComparison.Ordinal);

        return string.Format(
            CultureInfo.InvariantCulture,
            "Blocked by ComfyTray Guard: {0} (session {1:N}, {2:yyyy-MM-ddTHH:mm:ssZ})",
            safePath,
            sessionId,
            createdUtc.ToUniversalTime());
    }

    private static string HashPath(string normalizedPath)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath));
        return Convert.ToHexString(digest.AsSpan(0, HashLength / 2)).ToLowerInvariant();
    }
}
