using Elevate.Core.Models;
using Elevate.Core.Support;

namespace Elevate.Core.Providers;

/// <summary>
/// The end-user rules of a PIM role management policy. Graph (Entra, groups) and ARM (Azure resources)
/// wrap them in different envelopes but the rules themselves — and how they map to a
/// <see cref="RolePolicy"/> — are identical.
/// </summary>
public static class PolicyRules
{
    public sealed record Rule(
        string Id,
        bool? IsExpirationRequired,
        string? MaximumDuration,
        IReadOnlyList<string>? EnabledRules,
        Rule.ApprovalSetting? Setting,
        bool? IsEnabled,
        string? ClaimValue)
    {
        public sealed record ApprovalSetting(bool? IsApprovalRequired);
    }

    /// <summary>Folds the end-user rules onto <see cref="RolePolicy.ManualDefault"/>; unknown rules are ignored.</summary>
    public static RolePolicy Apply(IEnumerable<Rule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var policy = RolePolicy.ManualDefault;
        foreach (var rule in rules)
        {
            switch (rule.Id)
            {
                case "Expiration_EndUser_Assignment":
                    if (rule.MaximumDuration is { } text && Iso8601Duration.Parse(text) is { } duration)
                    {
                        policy = policy with { MaximumDuration = duration, DefaultDuration = duration };
                    }

                    break;
                case "Enablement_EndUser_Assignment":
                {
                    var enabled = new HashSet<string>(rule.EnabledRules ?? [], StringComparer.Ordinal);
                    policy = policy with
                    {
                        RequiresJustification = enabled.Contains("Justification"),
                        RequiresTicket = enabled.Contains("Ticketing"),
                        RequiresMfa = enabled.Contains("MultiFactorAuthentication"),
                    };
                    break;
                }

                case "Approval_EndUser_Assignment":
                    policy = policy with { RequiresApproval = rule.Setting?.IsApprovalRequired ?? false };
                    break;
                case "AuthenticationContext_EndUser_Assignment":
                    if (rule.IsEnabled == true && rule.ClaimValue is { Length: > 0 } claim)
                    {
                        policy = policy with { AuthenticationContext = claim };
                    }

                    break;
                default:
                    break;
            }
        }

        return policy;
    }
}
