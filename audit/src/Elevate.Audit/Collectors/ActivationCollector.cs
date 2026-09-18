using Elevate.Audit.Auth;
using Elevate.Audit.Model;
using Elevate.Core.Models;
using Elevate.Core.Providers;

namespace Elevate.Audit.Collectors;

/// <summary>
/// PIM activations from the directory audit log: who actually used an eligibility, for Entra directory
/// roles and for PIM for Groups. Azure resource activations are not in the directory audit log; they come
/// from ARM's request history, which <see cref="AzureCollector"/> reads.
/// </summary>
/// <remarks>
/// The log is a record of events, not of assignments, so matching an entry back to an eligibility is
/// best-effort: the entry names the role by whichever identifier PIM logged (role definition id, template
/// id or display name) and the rules match on any of them. An entry that names neither a role nor a group
/// is dropped rather than guessed at — an eligibility reported as never used because of a mis-parse would
/// be worse than one quietly not reported.
/// </remarks>
public sealed class ActivationCollector(GraphTransport graph, Identity identity, string tenantId)
{
    internal sealed record WireTarget(string? Id, string? Type, string? DisplayName, string? UserPrincipalName);

    internal sealed record WireActor(WireTarget? User, WireTarget? App);

    internal sealed record WireAudit(
        string? Id,
        DateTimeOffset? ActivityDateTime,
        string? ActivityDisplayName,
        string? Category,
        string? Result,
        WireActor? InitiatedBy,
        IReadOnlyList<WireTarget>? TargetResources);

    public async Task<IReadOnlyList<ActivationRecord>> CollectAsync(DateTimeOffset since, CancellationToken ct)
    {
        var entries = await graph.ListAllAsync<WireAudit>(identity, tenantId, GraphUrls.DirectoryAudits(since), ClientIds.GraphReadScopes, ct).ConfigureAwait(false);
        return entries.Select(Map).OfType<ActivationRecord>().ToList();
    }

    /// <summary>
    /// True for an entry that records an eligibility being activated. PIM writes "Add member to role
    /// completed (PIM activation)" and the group equivalents; a deactivation, a denial or a request that
    /// never completed must not count as use.
    /// </summary>
    internal static bool IsActivation(WireAudit entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Result is { } result && !result.Equals("success", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var activity = entry.ActivityDisplayName ?? string.Empty;
        return activity.Contains("activat", StringComparison.OrdinalIgnoreCase)
            && !activity.Contains("deactivat", StringComparison.OrdinalIgnoreCase)
            && !activity.Contains("denied", StringComparison.OrdinalIgnoreCase)
            && !activity.Contains("cancel", StringComparison.OrdinalIgnoreCase)
            && !activity.Contains("request", StringComparison.OrdinalIgnoreCase);
    }

    internal static ActivationRecord? Map(WireAudit entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!IsActivation(entry) || entry.ActivityDateTime is not { } at)
        {
            return null;
        }

        var targets = entry.TargetResources ?? [];
        var principalId = targets.FirstOrDefault(t => IsType(t, "User") || IsType(t, "ServicePrincipal"))?.Id
            ?? entry.InitiatedBy?.User?.Id;
        if (string.IsNullOrEmpty(principalId))
        {
            return null;
        }

        if (targets.FirstOrDefault(t => IsType(t, "Role")) is { } role)
        {
            var keys = new[] { role.Id, role.DisplayName }.OfType<string>().Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return keys.Count == 0 ? null : new ActivationRecord(at, principalId, RoleSystem.Entra, keys, null);
        }

        if (targets.FirstOrDefault(t => IsType(t, "Group")) is { Id: { Length: > 0 } groupId })
        {
            return new ActivationRecord(at, principalId, RoleSystem.Group, [groupId], groupId);
        }

        return null;
    }

    private static bool IsType(WireTarget target, string type) => string.Equals(target.Type, type, StringComparison.OrdinalIgnoreCase);
}
