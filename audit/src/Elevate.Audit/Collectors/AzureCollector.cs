using System.Text.Json;
using Elevate.Audit.Model;
using Elevate.Core.Auth;
using Elevate.Core.Models;
using Elevate.Core.Providers;
using Elevate.Core.Support;

namespace Elevate.Audit.Collectors;

public sealed record AzureData(
    IReadOnlyList<AzureScopeRecord> Scopes,
    IReadOnlyList<AzureRoleDefinitionRecord> RoleDefinitions,
    IReadOnlyList<AzureAssignmentRecord> Assignments,
    IReadOnlyList<AzureAssignmentRecord> Eligibilities,
    IReadOnlyList<ActivationRecord> Activations,
    bool ActivationsReadable,
    IReadOnlyList<string> Notes);

/// <summary>Azure RBAC through ARM: management groups (best effort), subscriptions, classic role assignments, PIM schedule instances, role definitions.</summary>
public sealed class AzureCollector(GraphTransport arm, Identity identity, string tenantId)
{
    internal sealed record Named(string? DisplayName);
    internal sealed record WireManagementGroup(string Id, string Name, Named? Properties);
    internal sealed record WireSubscription(string Id, string SubscriptionId, string? DisplayName);
    internal sealed record AssignmentProperties(string? Scope, string? RoleDefinitionId, string? PrincipalId, string? PrincipalType, string? AssignmentType, DateTimeOffset? StartDateTime, DateTimeOffset? EndDateTime);
    internal sealed record RequestProperties(string? Scope, string? RoleDefinitionId, string? PrincipalId, string? RequestType, string? Status, DateTimeOffset? CreatedOn);
    internal sealed record WireRequest(string Id, string Name, RequestProperties? Properties);
    internal sealed record WireAssignment(string Id, string Name, AssignmentProperties? Properties);
    internal sealed record Permission(IReadOnlyList<string>? Actions);
    internal sealed record DefinitionProperties(string? RoleName, string? Type, IReadOnlyList<Permission>? Permissions);
    internal sealed record WireDefinition(string Id, string Name, DefinitionProperties? Properties);

    private readonly IReadOnlyList<string> _scopes = Scopes.ArmAll;

    public async Task<AzureData> CollectAsync(DateTimeOffset activationsSince, CancellationToken ct)
    {
        var notes = new List<string>();
        var scopes = new List<AzureScopeRecord>();

        IReadOnlyList<WireManagementGroup> managementGroups = [];
        try
        {
            managementGroups = await ListAllAsync<WireManagementGroup>(GraphUrls.ManagementGroups, ct).ConfigureAwait(false);
        }
        // An account with no management-group access gets ARM's 403 for "not authorized", which Core
        // maps to PolicyViolation, not Forbidden (that's reserved for Graph's permission-scope shape).
        catch (PimException e) when (e.Kind is PimErrorKind.PolicyViolation)
        {
            notes.Add("Azure management groups are not readable by this account; scanned subscriptions only.");
        }

        var subscriptions = await ListAllAsync<WireSubscription>(GraphUrls.Subscriptions, ct).ConfigureAwait(false);
        if (subscriptions.Count == 0)
        {
            throw new PimException(PimErrorKind.NotEligible, "No Azure subscriptions are visible to this account");
        }

        scopes.AddRange(managementGroups.Select(m => new AzureScopeRecord(m.Id, AzureScopeKind.ManagementGroup, m.Properties?.DisplayName ?? m.Name)));
        scopes.AddRange(subscriptions.Select(s => new AzureScopeRecord(s.Id, AzureScopeKind.Subscription, s.DisplayName ?? s.SubscriptionId)));

        var assignments = new Dictionary<string, AzureAssignmentRecord>(StringComparer.OrdinalIgnoreCase);
        var eligibilities = new Dictionary<string, AzureAssignmentRecord>(StringComparer.OrdinalIgnoreCase);
        var definitions = new Dictionary<string, AzureRoleDefinitionRecord>(StringComparer.OrdinalIgnoreCase);
        var activations = new List<ActivationRecord>();
        var activationsReadable = true;

        foreach (var mg in managementGroups)
        {
            foreach (var a in await ListAllAsync<WireAssignment>(GraphUrls.ManagementGroupRoleAssignments(mg.Name), ct).ConfigureAwait(false))
            {
                assignments.TryAdd(a.Id, Map(a, fromSchedule: false));
            }
        }

        foreach (var sub in subscriptions)
        {
            foreach (var a in await ListAllAsync<WireAssignment>(GraphUrls.SubscriptionRoleAssignments(sub.SubscriptionId), ct).ConfigureAwait(false))
            {
                assignments.TryAdd(a.Id, Map(a, fromSchedule: false));
            }

            foreach (var a in await ListAllAsync<WireAssignment>(GraphUrls.SubscriptionAssignmentInstances(sub.SubscriptionId), ct).ConfigureAwait(false))
            {
                assignments.TryAdd(a.Id, Map(a, fromSchedule: true));
            }

            foreach (var e in await ListAllAsync<WireAssignment>(GraphUrls.SubscriptionEligibilityInstances(sub.SubscriptionId), ct).ConfigureAwait(false))
            {
                eligibilities.TryAdd(e.Id, Map(e, fromSchedule: true));
            }

            // ARM keeps its own PIM request history, so Azure activations need no extra consent. A
            // subscription that refuses the read only costs this subscription's history, not the scan.
            try
            {
                foreach (var r in await ListAllAsync<WireRequest>(GraphUrls.SubscriptionAssignmentRequests(sub.SubscriptionId), ct).ConfigureAwait(false))
                {
                    if (Activation(r, activationsSince) is { } activation)
                    {
                        activations.Add(activation);
                    }
                }
            }
            catch (PimException)
            {
                activationsReadable = false;
            }

            foreach (var d in await ListAllAsync<WireDefinition>(GraphUrls.SubscriptionRoleDefinitions(sub.SubscriptionId), ct).ConfigureAwait(false))
            {
                definitions.TryAdd(d.Name, new AzureRoleDefinitionRecord(
                    d.Id,
                    d.Name,
                    d.Properties?.RoleName ?? d.Name,
                    d.Properties?.Type ?? "CustomRole",
                    d.Properties?.Permissions?.SelectMany(p => p.Actions ?? []).ToList() ?? []));
            }
        }

        return new AzureData(scopes, definitions.Values.ToList(), assignments.Values.ToList(), eligibilities.Values.ToList(), activations, activationsReadable, notes);
    }

    /// <summary>
    /// An Azure PIM request that actually granted access from an eligibility: a self-activation, or an
    /// extension or renewal of one. An assignment made by an administrator is not use of an eligibility.
    /// </summary>
    internal static ActivationRecord? Activation(WireRequest request, DateTimeOffset since)
    {
        ArgumentNullException.ThrowIfNull(request);
        var p = request.Properties;
        var type = p?.RequestType ?? string.Empty;
        var activated = type.Contains("Activate", StringComparison.OrdinalIgnoreCase)
            || (type.StartsWith("Self", StringComparison.OrdinalIgnoreCase)
                && (type.Contains("Extend", StringComparison.OrdinalIgnoreCase) || type.Contains("Renew", StringComparison.OrdinalIgnoreCase)));
        if (!activated
            || p?.CreatedOn is not { } at
            || at < since
            || string.IsNullOrEmpty(p.PrincipalId)
            || string.IsNullOrEmpty(p.RoleDefinitionId)
            || (p.Status is { } status && status.Equals("Revoked", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return new ActivationRecord(at, p.PrincipalId, RoleSystem.Azure, [ScopeDisplayName(p.RoleDefinitionId)], p.Scope);
    }

    public static AzureScopeKind ScopeKindOf(string scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrEmpty(scope) || scope == "/")
        {
            return AzureScopeKind.ManagementGroup;
        }

        var parts = scope.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && parts[0].Equals("providers", StringComparison.OrdinalIgnoreCase) && parts[1].Equals("Microsoft.Management", StringComparison.OrdinalIgnoreCase))
        {
            return AzureScopeKind.ManagementGroup;
        }

        return parts.Length switch
        {
            <= 2 => AzureScopeKind.Subscription,
            4 when parts[2].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase) => AzureScopeKind.ResourceGroup,
            _ => AzureScopeKind.Resource,
        };
    }

    public static string ScopeDisplayName(string scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return scope.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "/";
    }

    private static AzureAssignmentRecord Map(WireAssignment a, bool fromSchedule) => new(
        a.Id,
        a.Properties?.Scope ?? "/",
        a.Properties?.RoleDefinitionId ?? string.Empty,
        a.Properties?.PrincipalId ?? string.Empty,
        a.Properties?.PrincipalType,
        Wire.AssignmentTypeOf(a.Properties?.AssignmentType),
        a.Properties?.StartDateTime,
        a.Properties?.EndDateTime,
        fromSchedule);

    /// <summary>ARM pages with <c>nextLink</c>, not <c>@odata.nextLink</c>; Core's helper for that is internal, so this is ours.</summary>
    private async Task<IReadOnlyList<T>> ListAllAsync<T>(Uri url, CancellationToken ct)
    {
        Uri? next = url;
        var all = new List<T>();
        while (next is { } current)
        {
            var response = await arm.GetAsync(identity, tenantId, current, _scopes, ct).ConfigureAwait(false);
            var page = JsonSerializer.Deserialize<Wire.ArmPage<T>>(response.Body, GraphJson.Options);
            if (page?.Value is { } items)
            {
                all.AddRange(items);
            }

            next = page?.NextLink is { } link && Uri.TryCreate(link, UriKind.Absolute, out var parsed) ? parsed : null;
        }

        return all;
    }
}
