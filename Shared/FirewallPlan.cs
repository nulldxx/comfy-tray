using System;
using System.Collections.Generic;
using System.Linq;

namespace ComfyTray;

/// <summary>
/// Works out which rules to add and which to remove to make a session's firewall state match
/// the set of images it wants blocked.
///
/// <para>
/// This is the part of the firewall logic worth testing, so it is separated from the COM that
/// carries it out: given what is desired and what already exists, decide the difference. It
/// never proposes removing a rule belonging to another session, and never proposes anything
/// for a rule it does not recognise as the guard's.
/// </para>
/// </summary>
internal static class FirewallPlan
{
    /// <summary>
    /// Computes the change set for one session.
    /// </summary>
    /// <param name="sessionId">The session being reconciled.</param>
    /// <param name="desiredPaths">
    /// Normalised image paths the session wants blocked, already through
    /// <see cref="GuardPathSet.Admit"/>.
    /// </param>
    /// <param name="existing">Every rule the guard currently owns, across all sessions.</param>
    public static FirewallChangeSet Compute(
        Guid sessionId,
        IReadOnlyCollection<string> desiredPaths,
        IReadOnlyCollection<GuardRuleRecord> existing)
    {
        ArgumentNullException.ThrowIfNull(desiredPaths);
        ArgumentNullException.ThrowIfNull(existing);

        // Only this session's rules are in scope. Another session's rules are none of our
        // business, and a concurrent session for a second logged-on user is an expected
        // configuration rather than an anomaly.
        var mine = existing.Where(r => r.SessionId == sessionId).ToList();
        var mineByName = mine.ToDictionary(r => r.RuleName, StringComparer.Ordinal);

        var wanted = new Dictionary<string, GuardRuleRecord>(StringComparer.Ordinal);
        foreach (var path in desiredPaths)
        {
            var name = GuardRuleNaming.Compose(sessionId, path);
            wanted[name] = new GuardRuleRecord(name, sessionId, path);
        }

        var adds = wanted.Values.Where(r => !mineByName.ContainsKey(r.RuleName)).ToList();
        var removes = mine.Where(r => !wanted.ContainsKey(r.RuleName)).Select(r => r.RuleName).ToList();

        return new FirewallChangeSet(adds, removes);
    }

    /// <summary>
    /// The rules to create when a session's desired set is capped. Returns the paths that fit
    /// within <see cref="GuardPathSet.MaxRulesPerSession"/> and the ones that did not, so the
    /// caller can report the overflow rather than silently dropping it.
    /// </summary>
    public static (List<string> Accepted, List<string> Rejected) ApplyCap(
        IReadOnlyCollection<string> alreadyBlocked,
        IReadOnlyCollection<string> incoming)
    {
        ArgumentNullException.ThrowIfNull(alreadyBlocked);
        ArgumentNullException.ThrowIfNull(incoming);

        var budget = GuardPathSet.MaxRulesPerSession - alreadyBlocked.Count;
        var known = new HashSet<string>(alreadyBlocked, StringComparer.Ordinal);

        var accepted = new List<string>();
        var rejected = new List<string>();

        foreach (var path in incoming)
        {
            // A path already blocked costs no budget — re-blocking is a no-op, not an overflow.
            if (known.Contains(path))
            {
                continue;
            }

            if (accepted.Count < budget)
            {
                accepted.Add(path);
                known.Add(path);
            }
            else
            {
                rejected.Add(path);
            }
        }

        return (accepted, rejected);
    }
}

/// <summary>The outcome of <see cref="FirewallPlan.Compute"/>.</summary>
/// <param name="Adds">Rules to create.</param>
/// <param name="Removes">Rule names to delete.</param>
internal sealed record FirewallChangeSet(
    IReadOnlyList<GuardRuleRecord> Adds,
    IReadOnlyList<string> Removes);
