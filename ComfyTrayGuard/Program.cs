using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;

namespace ComfyTray;

/// <summary>
/// Console entry point for ComfyTray Guard.
///
/// <para>
/// At this stage the guard is a command-line tool rather than a service: it proves the firewall
/// half of the design — the COM interop, the rule shape, the ownership test and the bulk purge —
/// on its own, before any of the IPC that will eventually drive it exists.
/// </para>
///
/// <para>
/// <c>--purge</c> is not only a development convenience. It is the documented escape hatch for
/// the one failure this design cannot fully close: if the service is killed rather than stopped
/// while rules are live, they persist until something removes them, and a user left with a
/// mysteriously offline Python needs a way out that takes ten seconds to find.
/// </para>
/// </summary>
internal static class Program
{
    /// <summary>
    /// The session the command-line tool attributes its rules to. Fixed rather than random so
    /// that blocking the same path twice from the shell is idempotent, and so that rules created
    /// by hand are visibly distinct from a real session's.
    /// </summary>
    private static readonly Guid ConsoleSessionId =
        Guid.Parse("c0f7c0de-0000-4000-8000-000000000001");

    [MTAThread]
    [SuppressMessage(
        "Design",
        "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "Top-level handler: every failure is reported to the console and becomes an exit code.")]
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        try
        {
            using var store = new ComFirewallRuleStore();

            return args[0].ToLowerInvariant() switch
            {
                "--list" => List(store),
                "--block" => Block(store, args.Skip(1).ToList()),
                "--purge" => Purge(store),
                "--health" => Health(store),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"comfytrayguard: {ex.Message}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                "The Windows Firewall service must be running, and creating or removing rules " +
                "requires administrator rights. Try again from an elevated prompt.");
            return 1;
        }
    }

    private static int List(IFirewallRuleStore store)
    {
        var rules = store.ListGuardRules();
        if (rules.Count == 0)
        {
            Console.WriteLine("No ComfyTray Guard rules on this machine.");
            return 0;
        }

        Console.WriteLine(
            string.Format(CultureInfo.InvariantCulture, "{0} ComfyTray Guard rule(s):", rules.Count));

        foreach (var session in rules.GroupBy(r => r.SessionId).OrderBy(g => g.Key))
        {
            Console.WriteLine(
                string.Format(CultureInfo.InvariantCulture, "  session {0:N}", session.Key));
            foreach (var rule in session.OrderBy(r => r.ImagePath, StringComparer.Ordinal))
            {
                Console.WriteLine($"    blocked outbound: {rule.ImagePath}");
            }
        }

        return 0;
    }

    private static int Block(IFirewallRuleStore store, IReadOnlyList<string> requested)
    {
        if (requested.Count == 0)
        {
            Console.Error.WriteLine("comfytrayguard: --block needs at least one executable path.");
            return 2;
        }

        var paths = GuardPathSet.Admit(requested);
        foreach (var rejected in requested.Where(r => !paths.Contains(GuardPathSet.Normalize(r))))
        {
            Console.WriteLine($"  skipped: {rejected} (empty, duplicated, or never blocked)");
        }

        var existing = store.ListGuardRules();
        var plan = FirewallPlan.Compute(ConsoleSessionId, [.. GetDesired(existing, paths)], existing);

        foreach (var add in plan.Adds)
        {
            store.Add(add);
            Console.WriteLine($"  blocked outbound: {add.ImagePath}");
        }

        if (plan.Adds.Count == 0)
        {
            Console.WriteLine("Nothing to do — every path given is already blocked.");
        }

        WarnIfIneffective(store);
        return 0;
    }

    /// <summary>
    /// The console tool adds to what the console session already blocks rather than replacing
    /// it, so a second <c>--block</c> does not tear down the first.
    /// </summary>
    private static IEnumerable<string> GetDesired(
        IReadOnlyCollection<GuardRuleRecord> existing, IEnumerable<string> incoming) =>
        existing.Where(r => r.SessionId == ConsoleSessionId)
                .Select(r => r.ImagePath)
                .Concat(incoming)
                .Distinct(StringComparer.Ordinal);

    private static int Purge(IFirewallRuleStore store)
    {
        var rules = store.ListGuardRules();
        foreach (var rule in rules)
        {
            store.Remove(rule.RuleName);
        }

        Console.WriteLine(
            string.Format(
                CultureInfo.InvariantCulture,
                "Removed {0} ComfyTray Guard rule(s). Nothing this service created remains.",
                rules.Count));
        return 0;
    }

    private static int Health(IFirewallRuleStore store)
    {
        var health = store.GetHealth();

        Console.WriteLine($"  domain profile firewall:  {OnOff(health.DomainProfileEnabled)}");
        Console.WriteLine($"  private profile firewall: {OnOff(health.PrivateProfileEnabled)}");
        Console.WriteLine($"  public profile firewall:  {OnOff(health.PublicProfileEnabled)}");
        Console.WriteLine($"  local rules honoured:     {OnOff(health.LocalRulesApply)}");
        Console.WriteLine();

        if (health.DegradedReason is { } reason)
        {
            Console.WriteLine($"Blocking would be degraded: {reason}.");
            return 1;
        }

        Console.WriteLine("Blocking would be fully effective.");
        return 0;
    }

    private static void WarnIfIneffective(IFirewallRuleStore store)
    {
        if (store.GetHealth().DegradedReason is { } reason)
        {
            Console.WriteLine();
            Console.WriteLine($"WARNING: the rules were created, but {reason}.");
        }
    }

    private static string OnOff(bool value) => value ? "yes" : "NO";

    private static int Unknown(string argument)
    {
        Console.Error.WriteLine($"comfytrayguard: unknown option '{argument}'.");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("ComfyTray Guard — outbound firewall blocking for the ComfyUI process tree.");
        Console.WriteLine();
        Console.WriteLine("  --list             show every rule this service owns");
        Console.WriteLine("  --block <path>...  block outbound traffic from the given executables");
        Console.WriteLine("  --purge            remove every rule this service owns");
        Console.WriteLine("  --health           report whether the firewall will honour these rules");
        Console.WriteLine();
        Console.WriteLine("Creating or removing rules requires administrator rights.");
    }
}
