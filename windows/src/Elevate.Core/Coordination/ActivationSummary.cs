using System.Globalization;
using Elevate.Core.Models;
using Elevate.Core.Support;

namespace Elevate.Core.Coordination;

/// <summary>
/// The one-line body of the notification an activation run posts. Lives in Core so the wording is
/// testable without the app: the caller supplies the outcomes, how many requests were attempted and
/// a way to name a role, and gets back the sentence to show.
/// </summary>
public static class ActivationSummary
{
    /// <param name="outcomes">What the coordinator reported, possibly empty.</param>
    /// <param name="attempted">
    /// How many requests the run set out to make. Zero means there was nothing to do; a positive
    /// count with no outcomes means the run was abandoned or skipped every request.
    /// </param>
    /// <param name="names">A display name for a role key, used only when several outcomes must be listed.</param>
    /// <param name="now">The reference time for "starts in" countdowns.</param>
    public static string Body(
        IReadOnlyList<ActivationOutcome> outcomes, int attempted, Func<RoleKey, string> names, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(names);
        if (attempted <= 0)
        {
            return "Nothing to do";
        }

        if (outcomes.Count == 0)
        {
            return "Not completed; open Elevate for details";
        }

        return outcomes.Count == 1 ? Single(outcomes[0], now ?? DateTimeOffset.UtcNow) : Several(outcomes, names);
    }

    private static string Single(ActivationOutcome outcome, DateTimeOffset now)
    {
        switch (outcome.Result)
        {
            case ActivationResult.Activated a:
            {
                if (a.Assignment.EndDateTime is not { } end)
                {
                    return "Active";
                }

                var seconds = (long)(end - a.Assignment.StartDateTime).TotalSeconds;
                return seconds > 0 ? $"Active for {Countdown.Label(TimeSpan.FromSeconds(seconds))}" : "Active";
            }

            case ActivationResult.Scheduled s:
                return $"Scheduled to start in {Countdown.Until(s.Assignment.StartDateTime, now)}";
            case ActivationResult.PendingApproval:
                return "Awaiting approval";
            case ActivationResult.Failed f:
                return $"Failed: {f.Error.UserMessage}";
            default:
                return "Active";
        }
    }

    private static string Several(IReadOnlyList<ActivationOutcome> outcomes, Func<RoleKey, string> names)
    {
        int ok = 0, scheduled = 0, pending = 0;
        var failures = new List<string>();
        foreach (var outcome in outcomes)
        {
            switch (outcome.Result)
            {
                case ActivationResult.Activated:
                    ok++;
                    break;
                case ActivationResult.Scheduled:
                    scheduled++;
                    break;
                case ActivationResult.PendingApproval:
                    pending++;
                    break;
                // The outcome carries the key the provider resolved to, which is the one the app knows.
                case ActivationResult.Failed f:
                    failures.Add($"{names(outcome.RoleKey)}: {f.Error.UserMessage}");
                    break;
                default:
                    break;
            }
        }

        var parts = new List<string>();

        // Only the first clause names the noun: "2 roles activated, 1 scheduled, 1 failed: …".
        void Append(int count, string suffix)
        {
            if (count <= 0)
            {
                return;
            }

            parts.Add(parts.Count == 0
                ? $"{RoleCount(count)} {suffix}"
                : string.Create(CultureInfo.InvariantCulture, $"{count} {suffix}"));
        }

        Append(ok, "activated");
        Append(scheduled, "scheduled");
        Append(pending, "awaiting approval");
        if (failures.Count > 0)
        {
            var more = failures.Count - 1;
            var detail = more > 0 ? string.Create(CultureInfo.InvariantCulture, $"{failures[0]} and {more} more") : failures[0];
            Append(failures.Count, $"failed: {detail}");
        }

        return parts.Count == 0 ? "Nothing to do" : string.Join(", ", parts);
    }

    /// <summary>"1 role" / "2 roles".</summary>
    private static string RoleCount(int n) => n == 1 ? "1 role" : string.Create(CultureInfo.InvariantCulture, $"{n} roles");
}
