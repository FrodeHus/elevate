using Elevate.Core.Models;
using Elevate.Core.Storage;

namespace Elevate.Core.Coordination;

/// <summary>What a profile run will do with one entry. Port of Swift's <c>ProfilePlanItem.Disposition</c>.</summary>
public enum ProfilePlanDisposition { Activate, AlreadyActive, Pending, NotEligible, NotLoaded }

/// <summary>One line of a profile run: what will happen to an entry and with which duration.</summary>
public sealed record ProfilePlanItem(RoleKey RoleKey, EligibleRole? Role, TimeSpan Duration, ProfilePlanDisposition Disposition);

/// <summary>Port of the Swift <c>ProfilePlanner</c>.</summary>
public static class ProfilePlanner
{
    /// <summary>
    /// Duration: the entry's last run, else the role's remembered duration, else the policy default; never above the maximum.
    /// <paramref name="loadedTenants"/> are the tenants whose roles are known; a missing role in any other tenant
    /// is "not loaded yet", not "not eligible".
    /// </summary>
    public static IReadOnlyList<ProfilePlanItem> Plan(
        ActivationProfile profile,
        IReadOnlyDictionary<RoleKey, EligibleRole> roles,
        IReadOnlyDictionary<RoleKey, ActiveAssignment> active,
        IReadOnlyDictionary<RoleKey, RoleMemory> memory,
        IReadOnlySet<TenantKey> loadedTenants)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(loadedTenants);

        return [.. profile.Entries.Select(entry =>
        {
            var role = roles.GetValueOrDefault(entry.RoleKey);
            var policy = role?.Policy ?? RolePolicy.ManualDefault;
            var wanted = entry.LastDuration ?? memory.GetValueOrDefault(entry.RoleKey)?.LastDuration ?? policy.DefaultDuration;
            var duration = wanted < policy.MaximumDuration ? wanted : policy.MaximumDuration;
            ProfilePlanDisposition disposition;
            if (role is null)
            {
                disposition = loadedTenants.Contains(entry.RoleKey.TenantKey)
                    ? ProfilePlanDisposition.NotEligible
                    : ProfilePlanDisposition.NotLoaded;
            }
            else if (active.TryGetValue(entry.RoleKey, out var assignment))
            {
                disposition = assignment.Status.Kind switch
                {
                    AssignmentStatusKind.Active => ProfilePlanDisposition.AlreadyActive,
                    AssignmentStatusKind.PendingApproval or AssignmentStatusKind.PendingProvisioning or AssignmentStatusKind.Scheduled
                        => ProfilePlanDisposition.Pending,
                    _ => ProfilePlanDisposition.Activate,
                };
            }
            else
            {
                disposition = ProfilePlanDisposition.Activate;
            }

            return new ProfilePlanItem(entry.RoleKey, role, duration, disposition);
        })];
    }
}
