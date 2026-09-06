using Elevate.Core.Models;

namespace Elevate.Core.Support;

/// <summary>
/// Short labels describing what a role's activation policy will ask of the user, in the order
/// they matter at activation time: approval first (the outcome changes), then step-up prompts.
/// Port of the Swift <c>PolicyNotes</c>, so both apps share the wording.
/// </summary>
public static class PolicyNotes
{
    public const string Approval = "approval";
    public const string Mfa = "MFA";
    public const string ConditionalAccess = "Conditional Access";

    public static IReadOnlyList<string> Labels(RolePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var labels = new List<string>(3);
        if (policy.RequiresApproval)
        {
            labels.Add(Approval);
        }

        if (policy.RequiresMfa)
        {
            labels.Add(Mfa);
        }

        if (policy.AuthenticationContext is not null)
        {
            labels.Add(ConditionalAccess);
        }

        return labels;
    }

    /// <summary>One-line caption for a row, or null when the policy asks nothing extra.</summary>
    public static string? Caption(RolePolicy policy)
    {
        var labels = Labels(policy);
        return labels.Count == 0 ? null : string.Join(" · ", labels);
    }

    /// <summary>Tooltip explaining each label, one line per label; null when the policy asks nothing extra.</summary>
    public static string? Explanation(RolePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var lines = new List<string>(3);
        if (policy.RequiresApproval)
        {
            lines.Add("An approver must accept the request before the role becomes active.");
        }

        if (policy.RequiresMfa)
        {
            lines.Add("Multi-factor authentication is required; you may be asked to sign in again.");
        }

        if (policy.AuthenticationContext is { } context)
        {
            lines.Add($"A Conditional Access policy is attached (authentication context {context}); Entra may require a step-up sign-in on activation.");
        }

        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    /// <summary>The verb for the primary action: a request that waits for someone else is not an activation.</summary>
    public static string ActionTitle(RolePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.RequiresApproval ? "Request" : "Activate";
    }
}
