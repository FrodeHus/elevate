using System.Globalization;
using Elevate.Audit.Model;

namespace Elevate.Audit.Rules;

/// <summary>Shared wording and arithmetic for the three unused-eligibility rules.</summary>
internal static class EligibilityUse
{
    internal const string Remedy = "Remove the eligibility, or move it behind an access review.";

    internal static int DaysBetween(DateTimeOffset from, DateTimeOffset to) => (int)Math.Floor((to - from).TotalDays);

    internal static string Date(DateTimeOffset value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Medium for a privileged role, Low for anything only <c>--all-roles</c> brought in.</summary>
    internal static Severity SeverityOf(EligibilityView eligibility) => eligibility.Privileged ? Severity.Medium : Severity.Low;

    internal static Finding Finding(string code, Severity severity, RuleContext context, EligibilityView eligibility, string remedy, DateTimeOffset? lastActivated, int? ageDays) =>
        new(code, severity, context.Principal(eligibility.PrincipalId), eligibility.Role, eligibility.FindingScope, [], remedy, eligibility.PortalUrl,
            eligibility.Evidence with { LastActivatedAt = lastActivated, AgeDays = ageDays });

    /// <summary>
    /// The eligibilities whose use can be judged: the role system's history must have been readable, or
    /// "no activation found" would be indistinguishable from "history not readable" and every eligibility
    /// in the tenant would be reported as never used. Eligibilities of a principal that cannot use them at
    /// all are left to ELIGIBLE-ORPHANED, so one eligibility never produces two rows saying the same thing.
    /// </summary>
    internal static IEnumerable<EligibilityView> WithHistory(RuleContext context) =>
        context.Activations.History is null
            ? []
            : context.Eligibilities().Where(e => context.Activations.Covers(e.System) && OrphanOf(context, e) is null);

    /// <summary>
    /// Why the eligibility's principal cannot use it — disabled, blocked or gone — or null when it can.
    /// A principal that did not resolve counts as deleted only when principal resolution itself worked;
    /// otherwise every unresolved principal in a degraded scan would look like a leaver.
    /// </summary>
    internal static string? OrphanOf(RuleContext context, EligibilityView eligibility)
    {
        var principal = context.PrincipalRecordOf(eligibility.PrincipalId);
        var name = principal.DisplayName ?? principal.Id;
        if (principal.Type == PrincipalType.Unknown && !context.Snapshot.Skipped.Any(s => string.Equals(s.Source, "principals", StringComparison.OrdinalIgnoreCase)))
        {
            return $"{name} no longer resolves in the directory, and the eligibility outlived the principal.";
        }

        if (principal.AccountEnabled == false)
        {
            return principal.IsGuest
                ? $"{name} is a guest whose account is blocked, and still holds this eligibility."
                : $"{name}'s account is disabled, and still holds this eligibility.";
        }

        return null;
    }
}

/// <summary>
/// An eligibility that existed before the lookback window opened and was never activated inside it.
/// Granted inside the window it is left alone: the tenant's audit-log retention, not the eligibility, is
/// why nothing was found.
/// </summary>
public sealed class EligibleNeverActivatedRule : IRule
{
    public string Code => "ELIGIBLE-NEVER-ACTIVATED";

    public IEnumerable<Finding> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Activations.History is not { } history)
        {
            yield break;
        }

        foreach (var e in EligibilityUse.WithHistory(context))
        {
            if (context.Activations.LastActivation(e) is not null || e.StartDateTime is not { } granted || granted > history.Since)
            {
                continue;
            }

            var age = EligibilityUse.DaysBetween(granted, history.Until);
            yield return EligibilityUse.Finding(
                Code,
                EligibilityUse.SeverityOf(e),
                context,
                e,
                $"Granted {EligibilityUse.Date(granted)} ({age} days ago) and never activated in the {history.Days} days examined (since {EligibilityUse.Date(history.Since)}). {EligibilityUse.Remedy}",
                null,
                age);
        }
    }
}

/// <summary>An eligibility that was activated inside the window, but not for <c>--dormant-after</c> days.</summary>
public sealed class EligibleDormantRule : IRule
{
    public string Code => "ELIGIBLE-DORMANT";

    public IEnumerable<Finding> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Activations.History is not { } history)
        {
            yield break;
        }

        foreach (var e in EligibilityUse.WithHistory(context))
        {
            if (context.Activations.LastActivation(e) is not { } last)
            {
                continue;
            }

            var idle = EligibilityUse.DaysBetween(last, history.Until);
            if (idle < context.Options.DormantAfterDays)
            {
                continue;
            }

            var age = e.StartDateTime is { } granted ? EligibilityUse.DaysBetween(granted, history.Until) : (int?)null;
            yield return EligibilityUse.Finding(
                Code,
                EligibilityUse.SeverityOf(e),
                context,
                e,
                $"Last activated {EligibilityUse.Date(last)}, {idle} days ago{(age is { } days ? $"; granted {days} days ago" : string.Empty)}. {EligibilityUse.Remedy}",
                last,
                age);
        }
    }
}

/// <summary>
/// An eligibility whose principal cannot use it any more: a disabled account, or one that no longer
/// resolves in the directory at all. It needs no activation history, so it is the one unused-eligibility
/// rule that still runs when the history was not readable.
/// </summary>
public sealed class EligibleOrphanedRule : IRule
{
    public string Code => "ELIGIBLE-ORPHANED";

    public IEnumerable<Finding> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var e in context.Eligibilities())
        {
            if (EligibilityUse.OrphanOf(context, e) is not { } why)
            {
                continue;
            }

            var age = e.StartDateTime is { } granted ? EligibilityUse.DaysBetween(granted, context.Snapshot.ScannedAt) : (int?)null;
            yield return EligibilityUse.Finding(Code, Severity.High, context, e, $"{why} {EligibilityUse.Remedy}", context.Activations.LastActivation(e), age);
        }
    }
}
